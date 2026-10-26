using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol.Lookups;

namespace F1Telemetry.Core.Analytics;

/// <summary>
/// What one compound does at a track, learned from every recording there.
/// </summary>
/// <param name="Laps">Clean laps behind the pace figures.</param>
/// <param name="BaseLapSeconds">Fuel-corrected lap on a fresh set with an empty tank, in the anchor session's conditions.</param>
/// <param name="DegradationPerLap">Lap time each lap of tyre age adds, fuel taken out (never below 0), before the cliff.</param>
/// <param name="DegradationMeasured">False when no stint of the compound had two clean laps at different ages: the mean of the other compounds is used.</param>
/// <param name="WearPerLap">Wear of the most worn tyre per lap, %, weighted by laps.</param>
/// <param name="LifeLaps">Laps from a fresh set until that tyre reaches the wear limit; null when no wear was recorded.</param>
/// <param name="Linked">
/// True when the compound's pace is tied to the anchor session: it was run there, or in a session that shares a compound
/// with one that was. An unlinked compound's pace is taken as recorded, without correcting for that session's conditions.
/// </param>
/// <param name="CliffAge">
/// Tyre age (laps on a fresh set) where the most worn tyre reaches the cliff wear and the set starts losing time faster;
/// null when no wear was recorded.
/// </param>
/// <param name="CliffSecondsPerLap">What each lap past the cliff adds on top of <paramref name="DegradationPerLap"/>, s/lap.</param>
/// <param name="CliffMeasured">False when no clean lap was run past the cliff: the default is used.</param>
public sealed record CompoundModel(
    string Compound,
    int Laps,
    int Stints,
    int Sessions,
    double BaseLapSeconds,
    double DegradationPerLap,
    bool DegradationMeasured,
    double WearPerLap,
    double? LifeLaps,
    bool Linked,
    double? CliffAge = null,
    double CliffSecondsPerLap = 0,
    bool CliffMeasured = false)
{
    /// <summary>
    /// Fuel-corrected lap time on a set this many laps old (0 = its first lap on a fresh set): straight-line degradation,
    /// and past the cliff the cliff's slope on top.
    /// </summary>
    public double LapSeconds(double tyreAge) =>
        BaseLapSeconds + DegradationPerLap * tyreAge + (CliffAge is { } cliff && tyreAge > cliff ? CliffSecondsPerLap * (tyreAge - cliff) : 0);

    /// <summary>Tyre age a set with this wear (%, most worn tyre) is equivalent to: its laps' worth of this compound's wear.</summary>
    public double AgeForWear(double wearPercent) => WearPerLap > 0 ? Math.Max(0, wearPercent) / WearPerLap : 0;

    public bool IsDry => Compound is "Soft" or "Medium" or "Hard";
}

/// <param name="PitLossSeconds">What a stop costs at the track: median of the recorded races' stops (not those under a safety car), or the default.</param>
/// <param name="PitLossStops">Stops behind <paramref name="PitLossSeconds"/>; 0 = the default.</param>
/// <param name="FuelPerLap">Median fuel burn per clean lap, kg.</param>
/// <param name="RaceLaps">Longest race recorded at the track; null when there is none.</param>
public sealed record CompoundLibrary(
    IReadOnlyList<CompoundModel> Compounds,
    FuelEffect FuelEffect,
    double PitLossSeconds,
    int PitLossStops,
    double FuelPerLap,
    int Sessions,
    int? RaceLaps)
{
    public CompoundModel? this[string compound] => Compounds.FirstOrDefault(c => c.Compound == compound);

    /// <summary>
    /// What a stop under a safety car costs here: the median of the recorded races' stops under one, each measured against
    /// the laps behind it; null when none was recorded.
    /// </summary>
    public double? SafetyCarPitLossSeconds { get; init; }

    public int SafetyCarPitLossStops { get; init; }

    /// <summary>The same for a virtual safety car.</summary>
    public double? VirtualSafetyCarPitLossSeconds { get; init; }

    public int VirtualSafetyCarPitLossStops { get; init; }
}

/// <summary>
/// Builds a <see cref="CompoundLibrary"/> from every recording at one track and game format.
/// <para>Each clean lap is fuel-corrected (one fuel effect for the whole library), then fitted as
/// lap = base[compound] + offset[session] + degradation[compound] × tyre age. The degradation is the slope within each
/// stint, pooled over the compound's stints, so a session that was faster overall doesn't tilt it. The session offsets
/// take out what differs between sessions (track temperature, rubber, the driver's day), so compounds run in different
/// sessions still compare, as long as the sessions are linked by a compound they share.</para>
/// <para>Laps run with the most worn tyre past the cliff wear (<see cref="StrategyOptions.TyreCliffWearPercent"/>) stay
/// out of that fit; what they lose beyond the straight line, per lap past the cliff, is the compound's cliff slope.</para>
/// </summary>
public static class CompoundLibraryBuilder
{
    /// <param name="sessions">Every analysed recording at the track, in one game format.</param>
    /// <param name="anchorRecordingId">The session whose conditions the paces are expressed in (the one on screen).</param>
    public static CompoundLibrary Build(IReadOnlyList<RaceInput> sessions, long? anchorRecordingId, StrategyOptions? options = null)
    {
        options ??= new StrategyOptions();
        var fuelEffect = ResolveFuelEffect(sessions, options.FuelEffectSecondsPerKg);

        // Clean laps, fuel taken out, keyed by session index and stint.
        var laps = new List<LibraryLap>();
        for (var s = 0; s < sessions.Count; s++)
        {
            foreach (var lap in sessions[s].Analysis.PaceLaps)
            {
                if (lap.Compound != "Unknown")
                {
                    laps.Add(new LibraryLap(s, lap.StintNumber, lap.Compound, lap.TyreAge, lap.LapSeconds - fuelEffect.SecondsPerKg * lap.FuelKg, lap.WearPercent));
                }
            }
        }

        var compounds = laps.Select(l => l.Compound).Distinct().ToList();
        var wearPerLap = compounds.ToDictionary(c => c, compound =>
        {
            var stints = sessions.SelectMany(r => r.Analysis.Stints).Where(st => st.Compound == compound && st.RegularLapCount > 0).ToList();
            var wearLaps = stints.Sum(st => st.RegularLapCount);
            return wearLaps > 0 ? stints.Sum(st => MaxWheel(st) * st.RegularLapCount) / wearLaps : 0;
        });

        // Past the cliff a lap is slower than the straight line: those laps measure the cliff and stay out of the fit
        // (unless they are all the compound has).
        var cliffLaps = laps.Where(l => wearPerLap[l.Compound] > 0 && l.Wear >= options.TyreCliffWearPercent).ToHashSet();
        var onlyCliff = compounds.Where(c => laps.Where(l => l.Compound == c).All(cliffLaps.Contains)).ToHashSet();
        cliffLaps.RemoveWhere(l => onlyCliff.Contains(l.Compound));
        var straight = laps.Where(l => !cliffLaps.Contains(l)).ToList();

        var degradation = new Dictionary<string, double>();
        foreach (var compound in compounds)
        {
            if (PooledSlope(straight.Where(l => l.Compound == compound)) is { } slope)
            {
                degradation[compound] = Math.Max(0, slope);
            }
        }

        var assumed = degradation.Count > 0 ? degradation.Values.Average() : 0;
        double Degradation(string compound) => degradation.TryGetValue(compound, out var d) ? d : assumed;

        // Session offsets and compound bases by alternating least squares (a two-way layout: each step is a mean).
        var anchor = sessions.ToList().FindIndex(r => r.Recording.Id == anchorRecordingId);
        if (anchor < 0 || laps.All(l => l.Session != anchor))
        {
            anchor = laps.Count > 0 ? laps.GroupBy(l => l.Session).MaxBy(g => g.Count())!.Key : -1;
        }

        var (linkedSessions, linkedCompounds) = Linked(laps, anchor);
        var bySession = straight.ToLookup(l => l.Session);
        var byCompound = straight.ToLookup(l => l.Compound);
        var offset = new double[sessions.Count];
        var baseLap = compounds.ToDictionary(c => c, _ => 0.0);
        for (var iteration = 0; iteration < 100; iteration++)
        {
            foreach (var compound in compounds)
            {
                baseLap[compound] = byCompound[compound].Average(l => l.Seconds - Degradation(compound) * l.TyreAge - offset[l.Session]);
            }

            var change = 0.0;
            foreach (var s in linkedSessions.Where(s => s != anchor && bySession[s].Any()))
            {
                var next = bySession[s].Average(l => l.Seconds - Degradation(l.Compound) * l.TyreAge - baseLap[l.Compound]);
                change = Math.Max(change, Math.Abs(next - offset[s]));
                offset[s] = next;
            }

            if (change < 1e-6)
            {
                break;
            }
        }

        var models = compounds.Select(compound =>
        {
            var compoundLaps = laps.Where(l => l.Compound == compound).ToList();
            var wear = wearPerLap[compound];
            var cliff = CliffSlope(cliffLaps.Where(l => l.Compound == compound), l =>
                l.Seconds - baseLap[compound] - offset[l.Session] - Degradation(compound) * l.TyreAge, wear, options.TyreCliffWearPercent);
            return new CompoundModel(
                compound,
                compoundLaps.Count,
                compoundLaps.Select(l => (l.Session, l.Stint)).Distinct().Count(),
                compoundLaps.Select(l => l.Session).Distinct().Count(),
                baseLap[compound],
                Degradation(compound),
                degradation.ContainsKey(compound),
                wear,
                wear > 0 ? options.TyreWearLimitPercent / wear : null,
                linkedCompounds.Contains(compound),
                wear > 0 ? options.TyreCliffWearPercent / wear : null,
                cliff ?? options.DefaultTyreCliffSecondsPerLap,
                cliff is not null);
        }).OrderBy(c => CompoundOrder(c.Compound)).ToList();

        List<double> Losses(Neutralisation neutralisation, double min) => sessions
            .Where(r => SessionTypes.IsRace(r.Recording.SessionType))
            .SelectMany(r => r.Analysis.PitStops)
            .Where(p => p.Neutralisation == neutralisation)
            .Select(p => p.LossSeconds)
            .Where(loss => loss > min && loss < 90)
            .ToList();
        var stops = Losses(Neutralisation.None, 5);
        var safetyCar = Losses(Neutralisation.SafetyCar, 0);
        var virtualSafetyCar = Losses(Neutralisation.VirtualSafetyCar, 0);
        var fuel = sessions.SelectMany(r => r.Analysis.Stints).Select(st => st.AverageFuelPerLap).Where(f => f > 0).ToList();
        var raceLaps = sessions.Where(r => SessionTypes.IsRace(r.Recording.SessionType) && r.Laps.Count > 0).Select(r => r.Laps[^1].LapNumber).DefaultIfEmpty().Max();

        return new CompoundLibrary(
            models,
            fuelEffect,
            stops.Count > 0 ? PaceModel.Median(stops) : options.DefaultPitLossSeconds,
            stops.Count,
            fuel.Count > 0 ? PaceModel.Median(fuel) : 0,
            sessions.Count(r => r.Analysis.PaceLaps.Count > 0),
            raceLaps > 0 ? raceLaps : null)
        {
            SafetyCarPitLossSeconds = safetyCar.Count > 0 ? PaceModel.Median(safetyCar) : null,
            SafetyCarPitLossStops = safetyCar.Count,
            VirtualSafetyCarPitLossSeconds = virtualSafetyCar.Count > 0 ? PaceModel.Median(virtualSafetyCar) : null,
            VirtualSafetyCarPitLossStops = virtualSafetyCar.Count,
        };
    }

    /// <summary>Soft, medium, hard, then the wet compounds.</summary>
    public static int CompoundOrder(string compound) => compound switch
    {
        "Soft" => 0,
        "Medium" => 1,
        "Hard" => 2,
        "Inter" => 3,
        "Wet" => 4,
        _ => 5,
    };

    /// <summary>Measured in one or more sessions: their median. Otherwise the default.</summary>
    private static FuelEffect ResolveFuelEffect(IReadOnlyList<RaceInput> sessions, double defaultSecondsPerKg)
    {
        var measured = sessions.Select(r => r.Analysis.FuelEffect).Where(f => f.Source == FuelEffectSource.Measured).ToList();
        return measured.Count switch
        {
            0 => new FuelEffect(defaultSecondsPerKg, FuelEffectSource.Default, "typical F1 value, not measured"),
            1 => measured[0],
            _ => new FuelEffect(PaceModel.Median(measured.Select(f => f.SecondsPerKg)), FuelEffectSource.Measured, $"median of {measured.Count} sessions' measurements"),
        };
    }

    /// <summary>Slope of lap time on tyre age within each stint, pooled; null when no stint has two different ages.</summary>
    private static double? PooledSlope(IEnumerable<LibraryLap> laps)
    {
        double sxy = 0, sxx = 0;
        foreach (var stint in laps.GroupBy(l => (l.Session, l.Stint)))
        {
            var meanAge = stint.Average(l => (double)l.TyreAge);
            var meanTime = stint.Average(l => l.Seconds);
            foreach (var lap in stint)
            {
                sxy += (lap.TyreAge - meanAge) * (lap.Seconds - meanTime);
                sxx += (lap.TyreAge - meanAge) * (lap.TyreAge - meanAge);
            }
        }

        return sxx > 0 ? sxy / sxx : null;
    }

    /// <summary>
    /// Least-squares slope (through the cliff) of what laps past the cliff lose beyond the straight line, against the laps
    /// they are past it (their wear over the cliff wear, in laps of wear); null with fewer than 3 such laps.
    /// </summary>
    private static double? CliffSlope(IEnumerable<LibraryLap> laps, Func<LibraryLap, double> residual, double wearPerLap, double cliffWear)
    {
        double sxy = 0, sxx = 0;
        var count = 0;
        foreach (var lap in laps)
        {
            var past = (lap.Wear - cliffWear) / wearPerLap;
            sxy += past * residual(lap);
            sxx += past * past;
            count++;
        }

        return count >= 3 && sxx > 0 ? Math.Max(0, sxy / sxx) : null;
    }

    /// <summary>Sessions and compounds reachable from the anchor session through shared compounds.</summary>
    private static (HashSet<int> Sessions, HashSet<string> Compounds) Linked(List<LibraryLap> laps, int anchor)
    {
        var sessions = new HashSet<int>();
        var compounds = new HashSet<string>();
        if (anchor < 0)
        {
            return (sessions, compounds);
        }

        var pairs = laps.Select(l => (l.Session, l.Compound)).Distinct().ToList();
        var queue = new Queue<int>([anchor]);
        sessions.Add(anchor);
        while (queue.TryDequeue(out var session))
        {
            foreach (var compound in pairs.Where(p => p.Session == session).Select(p => p.Compound))
            {
                if (!compounds.Add(compound))
                {
                    continue;
                }

                foreach (var other in pairs.Where(p => p.Compound == compound && sessions.Add(p.Session)))
                {
                    queue.Enqueue(other.Session);
                }
            }
        }

        return (sessions, compounds);
    }

    private static double MaxWheel(StintAnalysis stint) =>
        Math.Max(Math.Max(stint.AverageWearPerLap.Fl, stint.AverageWearPerLap.Fr), Math.Max(stint.AverageWearPerLap.Rl, stint.AverageWearPerLap.Rr));

    private sealed record LibraryLap(int Session, int Stint, string Compound, int TyreAge, double Seconds, double Wear);
}
