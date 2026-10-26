namespace F1Telemetry.Core.Analytics;

/// <summary>A tyre set a race can use.</summary>
/// <param name="WearPercent">Wear of its most worn tyre, %: 0 for a new set, more for one run before (in qualifying, say).</param>
public sealed record PlannerSet(string Compound, double WearPercent = 0)
{
    public bool IsNew => WearPercent <= 0;
}

/// <param name="FirstLap">First race lap on this set (its out-lap, or lap 1).</param>
/// <param name="StartWear">Wear the set had before the stint, % (0 = a new set).</param>
public sealed record PlannedStint(string Compound, int FirstLap, int LastLap, double StartWear = 0)
{
    public int Laps => LastLap - FirstLap + 1;
}

/// <summary>
/// One stop of a plan.
/// </summary>
/// <param name="InLap">The lap the car pits at the end of.</param>
/// <param name="WindowFirst">Earliest in-lap that costs at most the plan's window more than <paramref name="InLap"/>, the other stops staying put.</param>
/// <param name="WindowLast">Latest such in-lap.</param>
/// <param name="UndercutSeconds">
/// What pitting one lap before a car on the same plan gains: its lap on the old set, one lap older than yours was, against
/// your second lap on the new set (both pay the stop). Positive: the undercut works; negative: staying out (the overcut) does.
/// </param>
public sealed record PlannedStop(int InLap, int WindowFirst, int WindowLast, string FromCompound, string ToCompound, double UndercutSeconds)
{
    /// <summary>Laps on which a safety car makes pitting then, instead of at <see cref="InLap"/>, the faster race; null when none does.</summary>
    public SafetyCarWindow? SafetyCar { get; init; }

    /// <summary>The same for a virtual safety car, which saves less of the stop.</summary>
    public SafetyCarWindow? VirtualSafetyCar { get; init; }
}

/// <summary>
/// The laps of a stint on which a (virtual) safety car makes an early stop pay: pitting under it at the end of any lap
/// from <paramref name="FirstLap"/> to <paramref name="LastLap"/> (the planned in-lap), the stints after it re-planned
/// for the laps left, beats staying out and pitting as planned at the full loss.
/// </summary>
/// <param name="GainSeconds">The most such a stop gains over the plan, on the best of those laps.</param>
public sealed record SafetyCarWindow(int FirstLap, int LastLap, double GainSeconds);

/// <param name="RaceSeconds">Predicted race time: every lap from its compound's model, the fuel load, and the stops.</param>
/// <param name="GapSeconds">Behind the best plan.</param>
public sealed record RacePlan(IReadOnlyList<PlannedStint> Stints, IReadOnlyList<PlannedStop> Stops, double RaceSeconds, double GapSeconds)
{
    public int StopCount => Stops.Count;

    /// <summary>Predicted clean lap times with the fuel load, race lap 1 first; the stops' cost is not in them.</summary>
    public IReadOnlyList<double> LapSeconds { get; init; } = [];
}

/// <param name="Compounds">The compounds the plans were built from.</param>
/// <param name="Problem">Why there are no plans (empty when there are).</param>
public sealed record StrategyPlans(int RaceLaps, IReadOnlyList<RacePlan> Plans, IReadOnlyList<CompoundModel> Compounds, string Problem)
{
    public RacePlan? Best => Plans.Count > 0 ? Plans[0] : null;

    /// <summary>The fastest plan with this many stops, or null when none fits the tyres' life.</summary>
    public RacePlan? BestWith(int stops) => Plans.FirstOrDefault(p => p.StopCount == stops);
}

public sealed record PlannerOptions
{
    public int MinStops { get; init; } = 1;
    public int MaxStops { get; init; } = 3;

    /// <summary>No stint shorter than this, laps.</summary>
    public int MinStintLaps { get; init; } = 3;

    /// <summary>A stop's window: in-laps that cost at most this much more than the best one, s.</summary>
    public double WindowSeconds { get; init; } = 1.0;

    /// <summary>Plans listed: the fastest ones overall, plus the fastest of every stop count if it didn't make it.</summary>
    public int MaxPlans { get; init; } = 8;

    /// <summary>Compounds with fewer clean laps than this are left out of the plans.</summary>
    public int MinCompoundLaps { get; init; } = 3;

    /// <summary>
    /// New sets of each compound the race can use (a plan runs each set once); a compound not listed has no limit.
    /// Defaults to the F1 weekend allocation: 8 soft, 3 medium, 2 hard. Ignored when <see cref="Sets"/> is given.
    /// </summary>
    public IReadOnlyDictionary<string, int> MaxSets { get; init; } = DefaultSets;

    /// <summary>
    /// Every set the race can use, new and used (as the game's tyre-sets packet lists them); a compound not listed has
    /// none. Null: <see cref="MaxSets"/> new sets.
    /// </summary>
    public IReadOnlyList<PlannerSet>? Sets { get; init; }

    /// <summary>
    /// The set the race starts on, new or used (the qualifying set, say); null lets the plan pick. It is taken out of
    /// <see cref="Sets"/> when listed there, or out of <see cref="MaxSets"/> when it is new.
    /// </summary>
    public PlannerSet? StartSet { get; init; }

    /// <summary>
    /// Share of the pit-stop loss a stop under a safety car costs (the field is slow while you are in the lane), until a
    /// stop under one is recorded at the track. Real F1 races: about 14.2 s against 21.9 s under green.
    /// </summary>
    public double SafetyCarPitLossFactor { get; init; } = 0.65;

    /// <summary>The same for a virtual safety car. Real F1 races: about 18.1 s against 21.9 s.</summary>
    public double VirtualSafetyCarPitLossFactor { get; init; } = 0.83;

    public static IReadOnlyDictionary<string, int> DefaultSets { get; } = new Dictionary<string, int> { ["Soft"] = 8, ["Medium"] = 3, ["Hard"] = 2 };
}

/// <summary>
/// Dry-race plans from a <see cref="CompoundLibrary"/>: every 1-, 2- and 3-stop plan that runs two different dry compounds
/// and keeps each set within its tyre life, the stint lengths chosen to minimise race time, ranked.
/// <para>A used set runs as a new one would at the tyre age its wear is worth (wear over the compound's wear per lap),
/// with that much less life. A plan takes a compound's freshest sets first. A stint's cost depends on its set and its
/// length, not on the lap it starts on, so for a set of compounds the stint order doesn't change the time; plans are
/// shown softest first, after the start set when there is one. The fuel load slows every plan alike, so it moves the
/// predicted time but not the ranking.</para>
/// </summary>
public static class StrategyPlanner
{
    /// <summary>What a stop under a safety car costs at the track: as recorded there, else the share of a green stop.</summary>
    public static double SafetyCarPitLoss(CompoundLibrary library, PlannerOptions options) =>
        library.SafetyCarPitLossSeconds ?? library.PitLossSeconds * options.SafetyCarPitLossFactor;

    /// <summary>The same for a virtual safety car.</summary>
    public static double VirtualSafetyCarPitLoss(CompoundLibrary library, PlannerOptions options) =>
        library.VirtualSafetyCarPitLossSeconds ?? library.PitLossSeconds * options.VirtualSafetyCarPitLossFactor;

    public static StrategyPlans Plan(CompoundLibrary library, int raceLaps, PlannerOptions? options = null)
    {
        options ??= new PlannerOptions();
        var pool = Pool(options);
        var start = options.StartSet;
        var dry = library.Compounds
            .Where(c => c.IsDry && c.Laps >= options.MinCompoundLaps && (pool.ContainsKey(c.Compound) || c.Compound == start?.Compound))
            .ToList();
        if (raceLaps < 2 * options.MinStintLaps)
        {
            return new StrategyPlans(raceLaps, [], dry, $"A race needs at least {2 * options.MinStintLaps} laps for a stop.");
        }

        if (start is not null && dry.All(c => c.Compound != start.Compound))
        {
            return new StrategyPlans(raceLaps, [], dry,
                $"The race starts on the {start.Compound.ToLowerInvariant()}, which has no dry-race model here: it needs {options.MinCompoundLaps}+ clean laps recorded at this track.");
        }

        if (dry.Count < 2)
        {
            var have = dry.Count == 0 ? "no dry compound" : $"only the {dry[0].Compound}";
            return new StrategyPlans(raceLaps, [], dry,
                $"A dry race must use two compounds, and there is {have} with {options.MinCompoundLaps}+ clean laps recorded here and a set available. Run another compound at this track (a practice session will do), or check the sets.");
        }

        // The tank burns down from what the race needs to empty: the mean load over each lap.
        double FuelAt(int lap) => library.FuelEffect.SecondsPerKg * library.FuelPerLap * (raceLaps - lap + 0.5);
        var startModel = start is null ? null : dry.First(c => c.Compound == start.Compound);
        var candidates = new List<RacePlan>();
        for (var stops = options.MinStops; stops <= options.MaxStops; stops++)
        {
            foreach (var set in Multisets(dry.Count, start is null ? stops + 1 : stops))
            {
                var counts = set.GroupBy(i => i).ToDictionary(g => dry[g.Key].Compound, g => g.Count());
                if (set.Select(i => dry[i].Compound).Append(start?.Compound).OfType<string>().Distinct().Count() < 2
                    || counts.Any(c => c.Value > (pool.TryGetValue(c.Key, out var sets) ? sets.Count : 0)))
                {
                    continue;
                }

                // Each compound's freshest sets, in the order the sets are listed.
                var stints = new List<StintSet>();
                if (start is not null)
                {
                    stints.Add(new StintSet(startModel!, start.WearPercent));
                }

                foreach (var group in set.GroupBy(i => i))
                {
                    var model = dry[group.Key];
                    stints.AddRange(pool[model.Compound].Take(group.Count()).Select(wear => new StintSet(model, wear)));
                }

                var suffix = Suffix(stints, raceLaps, options.MinStintLaps);
                if (Lengths(stints, suffix, raceLaps, options.MinStintLaps) is { } lengths)
                {
                    candidates.Add(Build(stints, lengths, suffix, raceLaps, library, FuelAt, options));
                }
            }
        }

        if (candidates.Count == 0)
        {
            var sets = string.Join(", ", dry.Select(c => $"{(pool.TryGetValue(c.Compound, out var s) ? s.Count : 0)} {c.Compound.ToLowerInvariant()}"));
            return new StrategyPlans(raceLaps, [], dry,
                $"No plan with up to {options.MaxStops} stops keeps every set within its tyre life over {raceLaps} laps with the sets available ({sets}{(start is null ? "" : $", starting on the {start.Compound.ToLowerInvariant()}")}).");
        }

        candidates.Sort((a, b) => a.RaceSeconds.CompareTo(b.RaceSeconds));
        var best = candidates[0].RaceSeconds;
        var shown = candidates.Take(options.MaxPlans).ToList();
        for (var stops = options.MinStops; stops <= options.MaxStops; stops++)
        {
            if (shown.All(p => p.StopCount != stops) && candidates.FirstOrDefault(p => p.StopCount == stops) is { } plan)
            {
                shown.Add(plan);
            }
        }

        return new StrategyPlans(raceLaps, shown.Select(p => p with { GapSeconds = p.RaceSeconds - best }).ToList(), dry, "");
    }

    /// <summary>The sets each compound has for the race, freshest first (the start set taken out).</summary>
    private static Dictionary<string, List<double>> Pool(PlannerOptions options)
    {
        var pool = new Dictionary<string, List<double>>();
        if (options.Sets is { } sets)
        {
            foreach (var set in sets)
            {
                (pool.TryGetValue(set.Compound, out var wears) ? wears : pool[set.Compound] = []).Add(set.WearPercent);
            }
        }
        else
        {
            foreach (var compound in new[] { "Soft", "Medium", "Hard" })
            {
                var count = options.MaxSets.TryGetValue(compound, out var max) ? max : options.MaxStops + 1;
                if (count > 0)
                {
                    pool[compound] = Enumerable.Repeat(0.0, count).ToList();
                }
            }
        }

        // The start set is one of the sets listed, or one of the new sets counted when it is new (a used one wasn't counted).
        if (options.StartSet is { } start && (options.Sets is not null || start.IsNew) && pool.TryGetValue(start.Compound, out var same) && same.Count > 0)
        {
            same.Remove(same.MinBy(wear => Math.Abs(wear - start.WearPercent)));
        }

        foreach (var (compound, wears) in pool.ToList())
        {
            if (wears.Count == 0)
            {
                pool.Remove(compound);
            }
            else
            {
                wears.Sort();
            }
        }

        return pool;
    }

    /// <summary>Cost of one stint on a new set: its laps on the compound's model, fuel aside.</summary>
    public static double StintSeconds(CompoundModel compound, int laps) => StintSeconds(compound, laps, 0);

    /// <summary>Cost of one stint on a set that starts at <paramref name="startAge"/> laps of tyre age, fuel aside.</summary>
    public static double StintSeconds(CompoundModel compound, int laps, double startAge)
    {
        var total = 0.0;
        for (var age = 0; age < laps; age++)
        {
            total += compound.LapSeconds(startAge + age);
        }

        return total;
    }

    /// <summary>A stint's set: its compound and the tyre age its wear is worth.</summary>
    private sealed record StintSet(CompoundModel Model, double StartWear)
    {
        public double StartAge { get; } = Model.AgeForWear(StartWear);

        public bool Fits(int laps, int minStint) =>
            laps >= minStint && (Model.LifeLaps is not { } life || StartAge + laps <= Math.Floor(life + 1e-9) + 1e-9);

        public double Seconds(int laps) => StintSeconds(Model, laps, StartAge);

        public double LapSeconds(int lapOfStint) => Model.LapSeconds(StartAge + lapOfStint);
    }

    /// <summary>
    /// suffix[i, n]: the fastest the stints from <paramref name="stints"/>[i] on can run the last n laps, each set within
    /// its life (dynamic programming over laps); infinity when they can't.
    /// </summary>
    private static double[,] Suffix(IReadOnlyList<StintSet> stints, int raceLaps, int minStint)
    {
        var n = stints.Count;
        var suffix = new double[n + 1, raceLaps + 1];
        for (var i = 0; i <= n; i++)
        {
            for (var left = 0; left <= raceLaps; left++)
            {
                suffix[i, left] = i == n && left == 0 ? 0 : double.PositiveInfinity;
            }
        }

        for (var i = n - 1; i >= 0; i--)
        {
            var cost = 0.0;
            for (var laps = 1; laps <= raceLaps; laps++)
            {
                cost += stints[i].LapSeconds(laps - 1);
                if (laps < minStint)
                {
                    continue;
                }

                if (!stints[i].Fits(laps, minStint))
                {
                    break;
                }

                for (var left = laps; left <= raceLaps; left++)
                {
                    var total = cost + suffix[i + 1, left - laps];
                    if (total < suffix[i, left])
                    {
                        suffix[i, left] = total;
                    }
                }
            }
        }

        return suffix;
    }

    /// <summary>Stint lengths that minimise the race time for these sets in this order; null when no split fits every set's life.</summary>
    private static int[]? Lengths(IReadOnlyList<StintSet> stints, double[,] suffix, int raceLaps, int minStint)
    {
        if (double.IsPositiveInfinity(suffix[0, raceLaps]))
        {
            return null;
        }

        var lengths = new int[stints.Count];
        for (int i = 0, left = raceLaps; i < stints.Count; i++)
        {
            var target = suffix[i, left];
            var cost = 0.0;
            for (var laps = 1; laps <= left; laps++)
            {
                cost += stints[i].LapSeconds(laps - 1);
                if (laps >= minStint && stints[i].Fits(laps, minStint) && Math.Abs(cost + suffix[i + 1, left - laps] - target) < 1e-6)
                {
                    lengths[i] = laps;
                    break;
                }
            }

            left -= lengths[i];
        }

        return lengths;
    }

    private static RacePlan Build(IReadOnlyList<StintSet> sets, int[] lengths, double[,] suffix, int raceLaps, CompoundLibrary library,
        Func<int, double> fuelAt, PlannerOptions options)
    {
        var pitLoss = library.PitLossSeconds;
        var safetyCarSaving = Math.Max(0, pitLoss - SafetyCarPitLoss(library, options));
        var virtualSafetyCarSaving = Math.Max(0, pitLoss - VirtualSafetyCarPitLoss(library, options));
        var stints = new List<PlannedStint>();
        var lap = 1;
        for (var i = 0; i < sets.Count; i++)
        {
            stints.Add(new PlannedStint(sets[i].Model.Compound, lap, lap + lengths[i] - 1, sets[i].StartWear));
            lap += lengths[i];
        }

        var stops = new List<PlannedStop>();
        for (var i = 0; i < sets.Count - 1; i++)
        {
            var (before, after) = (sets[i], sets[i + 1]);
            var pair = before.Seconds(lengths[i]) + after.Seconds(lengths[i + 1]);
            var inLap = stints[i].LastLap;
            int first = inLap, last = inLap;
            bool Within(int shift) =>
                before.Fits(lengths[i] + shift, options.MinStintLaps) && after.Fits(lengths[i + 1] - shift, options.MinStintLaps)
                && before.Seconds(lengths[i] + shift) + after.Seconds(lengths[i + 1] - shift) - pair <= options.WindowSeconds + 1e-9;
            while (Within(first - 1 - inLap))
            {
                first--;
            }

            while (Within(last + 1 - inLap))
            {
                last++;
            }

            stops.Add(new PlannedStop(inLap, first, last, before.Model.Compound, after.Model.Compound,
                before.LapSeconds(lengths[i]) - after.LapSeconds(1))
            {
                SafetyCar = SafetyCarWindow(sets, stints, suffix, i, raceLaps, safetyCarSaving, options.MinStintLaps),
                VirtualSafetyCar = SafetyCarWindow(sets, stints, suffix, i, raceLaps, virtualSafetyCarSaving, options.MinStintLaps),
            });
        }

        var lapSeconds = new List<double>();
        for (var i = 0; i < sets.Count; i++)
        {
            for (var age = 0; age < lengths[i]; age++)
            {
                lapSeconds.Add(sets[i].LapSeconds(age) + fuelAt(lapSeconds.Count + 1));
            }
        }

        return new RacePlan(stints, stops, lapSeconds.Sum() + stops.Count * pitLoss, 0) { LapSeconds = lapSeconds };
    }

    /// <summary>
    /// The laps up to stop <paramref name="stop"/>'s in-lap on which pitting under a safety car (which saves
    /// <paramref name="saving"/> of the stop) beats the plan: the laps the plan would still run on the old set, against the
    /// stints after the stop re-planned for the laps then left. Both pay every later stop and the same fuel, so those cancel.
    /// The laps behind the safety car count at racing pace on both sides.
    /// </summary>
    private static SafetyCarWindow? SafetyCarWindow(IReadOnlyList<StintSet> sets, IReadOnlyList<PlannedStint> stints, double[,] suffix, int stop,
        int raceLaps, double saving, int minStint)
    {
        var stint = stints[stop];
        var planned = suffix[stop + 1, raceLaps - stint.LastLap];
        var oldSet = 0.0; // the plan's laps on the old set after the safety car lap
        int? first = null;
        var gainBest = double.NegativeInfinity;
        for (var lap = stint.LastLap; lap >= stint.FirstLap + minStint - 1; lap--)
        {
            if (lap < stint.LastLap)
            {
                oldSet += sets[stop].LapSeconds(lap - stint.FirstLap + 1);
            }

            var gain = oldSet + planned + saving - suffix[stop + 1, raceLaps - lap];
            if (!(gain > 0))
            {
                break;
            }

            first = lap;
            gainBest = Math.Max(gainBest, gain);
        }

        return first is { } from ? new SafetyCarWindow(from, stint.LastLap, gainBest) : null;
    }

    /// <summary>Every non-decreasing sequence of <paramref name="length"/> indices below <paramref name="count"/>: one per mix of compounds.</summary>
    private static IEnumerable<int[]> Multisets(int count, int length)
    {
        var current = new int[length];
        while (true)
        {
            yield return (int[])current.Clone();
            var i = length - 1;
            while (i >= 0 && current[i] == count - 1)
            {
                i--;
            }

            if (i < 0)
            {
                yield break;
            }

            current[i]++;
            for (var j = i + 1; j < length; j++)
            {
                current[j] = current[i];
            }
        }
    }
}
