namespace F1Telemetry.Core.Analytics;

/// <param name="FirstLap">First race lap on this set (its out-lap, or lap 1).</param>
public sealed record PlannedStint(string Compound, int FirstLap, int LastLap)
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
public sealed record PlannedStop(int InLap, int WindowFirst, int WindowLast, string FromCompound, string ToCompound, double UndercutSeconds);

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
    /// Sets of each compound the race can use (a plan runs each set once); a compound not listed has no limit.
    /// Defaults to the F1 weekend allocation: 8 soft, 3 medium, 2 hard.
    /// </summary>
    public IReadOnlyDictionary<string, int> MaxSets { get; init; } = DefaultSets;

    public static IReadOnlyDictionary<string, int> DefaultSets { get; } = new Dictionary<string, int> { ["Soft"] = 8, ["Medium"] = 3, ["Hard"] = 2 };
}

/// <summary>
/// Dry-race plans from a <see cref="CompoundLibrary"/>: every 1-, 2- and 3-stop plan that runs two different dry compounds
/// and keeps each set within its tyre life, the stint lengths chosen to minimise race time, ranked.
/// <para>A stint of n laps on compound c costs n × base + degradation × n(n−1)/2, whatever lap it starts on, so for a set
/// of compounds the stint order doesn't change the time; plans are shown softest first. The fuel load slows every plan
/// alike, so it moves the predicted time but not the ranking.</para>
/// </summary>
public static class StrategyPlanner
{
    public static StrategyPlans Plan(CompoundLibrary library, int raceLaps, PlannerOptions? options = null)
    {
        options ??= new PlannerOptions();
        var dry = library.Compounds.Where(c => c.IsDry && c.Laps >= options.MinCompoundLaps && SetsOf(c.Compound, options) > 0).ToList();
        if (raceLaps < 2 * options.MinStintLaps)
        {
            return new StrategyPlans(raceLaps, [], dry, $"A race needs at least {2 * options.MinStintLaps} laps for a stop.");
        }

        if (dry.Count < 2)
        {
            var have = dry.Count == 0 ? "no dry compound" : $"only the {dry[0].Compound}";
            return new StrategyPlans(raceLaps, [], dry,
                $"A dry race must use two compounds, and there is {have} with {options.MinCompoundLaps}+ clean laps recorded here and a set available. Run another compound at this track (a practice session will do), or check the sets.");
        }

        // The tank burns down from what the race needs to empty: the mean load over each lap.
        double FuelAt(int lap) => library.FuelEffect.SecondsPerKg * library.FuelPerLap * (raceLaps - lap + 0.5);
        var candidates = new List<RacePlan>();
        for (var stops = options.MinStops; stops <= options.MaxStops; stops++)
        {
            foreach (var set in Multisets(dry.Count, stops + 1))
            {
                if (set.Distinct().Count() < 2 || set.GroupBy(i => i).Any(g => g.Count() > SetsOf(dry[g.Key].Compound, options)))
                {
                    continue;
                }

                var compounds = set.Select(i => dry[i]).ToList();
                if (Lengths(compounds, raceLaps, options.MinStintLaps) is { } lengths)
                {
                    candidates.Add(Build(compounds, lengths, library.PitLossSeconds, FuelAt, options));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return new StrategyPlans(raceLaps, [], dry,
                $"No plan with up to {options.MaxStops} stops keeps every set within its tyre life over {raceLaps} laps with the sets available ({string.Join(", ", dry.Select(c => $"{SetsOf(c.Compound, options)} {c.Compound.ToLowerInvariant()}"))}).");
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

    private static int SetsOf(string compound, PlannerOptions options) => options.MaxSets.TryGetValue(compound, out var sets) ? sets : int.MaxValue;

    /// <summary>Cost of one stint: its laps on the compound's model, fuel aside.</summary>
    public static double StintSeconds(CompoundModel compound, int laps) =>
        laps * compound.BaseLapSeconds + compound.DegradationPerLap * laps * (laps - 1) / 2.0;

    private static bool Fits(CompoundModel compound, int laps, int minStint) =>
        laps >= minStint && (compound.LifeLaps is not { } life || laps <= Math.Floor(life + 1e-9));

    /// <summary>
    /// Stint lengths that minimise the race time for these compounds in this order (dynamic programming over laps used);
    /// null when no split fits every set's life.
    /// </summary>
    private static int[]? Lengths(IReadOnlyList<CompoundModel> compounds, int raceLaps, int minStint)
    {
        var n = compounds.Count;
        var cost = new double[n + 1, raceLaps + 1];
        var choice = new int[n + 1, raceLaps + 1];
        for (var i = 0; i <= n; i++)
        {
            for (var used = 0; used <= raceLaps; used++)
            {
                cost[i, used] = double.PositiveInfinity;
            }
        }

        cost[0, 0] = 0;
        for (var i = 0; i < n; i++)
        {
            for (var used = 0; used <= raceLaps; used++)
            {
                if (double.IsPositiveInfinity(cost[i, used]))
                {
                    continue;
                }

                for (var laps = minStint; used + laps <= raceLaps; laps++)
                {
                    if (!Fits(compounds[i], laps, minStint))
                    {
                        break;
                    }

                    var total = cost[i, used] + StintSeconds(compounds[i], laps);
                    if (total < cost[i + 1, used + laps])
                    {
                        cost[i + 1, used + laps] = total;
                        choice[i + 1, used + laps] = laps;
                    }
                }
            }
        }

        if (double.IsPositiveInfinity(cost[n, raceLaps]))
        {
            return null;
        }

        var lengths = new int[n];
        for (int i = n, used = raceLaps; i > 0; i--)
        {
            lengths[i - 1] = choice[i, used];
            used -= lengths[i - 1];
        }

        return lengths;
    }

    private static RacePlan Build(IReadOnlyList<CompoundModel> compounds, int[] lengths, double pitLoss, Func<int, double> fuelAt, PlannerOptions options)
    {
        var stints = new List<PlannedStint>();
        var lap = 1;
        for (var i = 0; i < compounds.Count; i++)
        {
            stints.Add(new PlannedStint(compounds[i].Compound, lap, lap + lengths[i] - 1));
            lap += lengths[i];
        }

        var stops = new List<PlannedStop>();
        for (var i = 0; i < compounds.Count - 1; i++)
        {
            var (before, after) = (compounds[i], compounds[i + 1]);
            var pair = StintSeconds(before, lengths[i]) + StintSeconds(after, lengths[i + 1]);
            var inLap = stints[i].LastLap;
            int first = inLap, last = inLap;
            bool Within(int shift) =>
                Fits(before, lengths[i] + shift, options.MinStintLaps) && Fits(after, lengths[i + 1] - shift, options.MinStintLaps)
                && StintSeconds(before, lengths[i] + shift) + StintSeconds(after, lengths[i + 1] - shift) - pair <= options.WindowSeconds + 1e-9;
            while (Within(first - 1 - inLap))
            {
                first--;
            }

            while (Within(last + 1 - inLap))
            {
                last++;
            }

            stops.Add(new PlannedStop(inLap, first, last, before.Compound, after.Compound, before.LapSeconds(lengths[i]) - after.LapSeconds(1)));
        }

        var lapSeconds = new List<double>();
        for (var i = 0; i < compounds.Count; i++)
        {
            for (var age = 0; age < lengths[i]; age++)
            {
                lapSeconds.Add(compounds[i].LapSeconds(age) + fuelAt(lapSeconds.Count + 1));
            }
        }

        return new RacePlan(stints, stops, lapSeconds.Sum() + stops.Count * pitLoss, 0) { LapSeconds = lapSeconds };
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
