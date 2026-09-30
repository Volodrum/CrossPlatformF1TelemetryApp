using F1Telemetry.Core.Models;

namespace F1Telemetry.Core.Analytics;

/// <summary>One side of a comparison: a recording's classified laps, their fuel/wear aggregates and its analysis.</summary>
public sealed record RaceInput(RecordingInfo Recording, IReadOnlyList<LapRecord> Laps, IReadOnlyList<LapAggregate> Aggregates, SessionAnalysis Analysis);

/// <summary>
/// Where one race's time went over the compared laps. The parts add up to <see cref="Total"/> exactly: every lap is
/// split into the fitted model (base pace + tyre wear + fuel load), the pit-stop loss, and whatever the model doesn't
/// explain (the standing start, traffic, mistakes).
/// </summary>
public sealed record TimeBreakdown(double Total, double PitStops, double TyreWear, double FuelLoad, double BasePace, double Other);

/// <param name="Laps">Lap numbers both races have a time for, ascending.</param>
/// <param name="CumulativeGap">B minus A in seconds after each of <see cref="Laps"/>; positive = B behind.</param>
/// <param name="FuelEffect">The single fuel effect applied to both races, so their fuel-corrected figures compare.</param>
public sealed record RaceComparisonResult(
    IReadOnlyList<int> Laps,
    IReadOnlyList<double> CumulativeGap,
    TimeBreakdown A,
    TimeBreakdown B,
    FuelEffect FuelEffect,
    IReadOnlyList<StintPaceFit> StintsA,
    IReadOnlyList<StintPaceFit> StintsB,
    IReadOnlyList<PitStopAnalysis> PitStopsA,
    IReadOnlyList<PitStopAnalysis> PitStopsB)
{
    public double TotalGap => CumulativeGap.Count > 0 ? CumulativeGap[^1] : 0;
}

/// <summary>Race vs race: lap-by-lap gap between two recordings of the same track, and why it grew.</summary>
public static class RaceComparison
{
    public static RaceComparisonResult? Compare(RaceInput a, RaceInput b, double defaultFuelEffect)
    {
        var timesA = a.Laps.Where(l => l.HasTime).ToDictionary(l => l.LapNumber);
        var timesB = b.Laps.Where(l => l.HasTime).ToDictionary(l => l.LapNumber);
        var laps = timesA.Keys.Intersect(timesB.Keys).Order().ToList();
        if (laps.Count == 0)
        {
            return null;
        }

        var gap = new List<double>(laps.Count);
        var running = 0.0;
        foreach (var lap in laps)
        {
            running += (timesB[lap].LapTimeMs - (double)timesA[lap].LapTimeMs) / 1000.0;
            gap.Add(running);
        }

        var fuelEffect = CommonFuelEffect(a.Analysis.FuelEffect, b.Analysis.FuelEffect, defaultFuelEffect);
        var window = laps.ToHashSet();
        var (breakdownA, fitsA, stopsA) = Breakdown(a, window, fuelEffect.SecondsPerKg);
        var (breakdownB, fitsB, stopsB) = Breakdown(b, window, fuelEffect.SecondsPerKg);
        return new RaceComparisonResult(laps, gap, breakdownA, breakdownB, fuelEffect, fitsA, fitsB, stopsA, stopsB);
    }

    /// <summary>Both measured: their mean. One measured: that one. Neither: the default.</summary>
    private static FuelEffect CommonFuelEffect(FuelEffect a, FuelEffect b, double defaultFuelEffect)
    {
        var measured = new[] { a, b }.Where(f => f.Source == FuelEffectSource.Measured).ToList();
        return measured.Count switch
        {
            2 => new FuelEffect((a.SecondsPerKg + b.SecondsPerKg) / 2, FuelEffectSource.Measured, "mean of both races' measurements"),
            1 => measured[0],
            _ => new FuelEffect(defaultFuelEffect, FuelEffectSource.Default, "typical F1 value, not measured"),
        };
    }

    private static (TimeBreakdown, List<StintPaceFit>, List<PitStopAnalysis>) Breakdown(RaceInput race, HashSet<int> window, double fuelEffect)
    {
        var fits = race.Analysis.PaceLaps
            .GroupBy(l => l.StintNumber)
            .Select(g => PaceModel.FitStint(g.Key, g.First().Compound, g.ToList(), fuelEffect)!)
            .ToDictionary(f => f.StintNumber);

        // A stint without clean laps (a short final stint, say) borrows the race's median base pace and no wear.
        var fallbackBase = fits.Count > 0 ? PaceModel.Median(fits.Values.Select(f => f.BaseLapSeconds)) : (double?)null;
        var fuelByLap = race.Aggregates.ToDictionary(x => x.LapNumber, x => (x.MinFuel + x.MaxFuel) / 2);
        var stops = race.Analysis.PitStops.Where(s => window.Contains(s.InLap) && window.Contains(s.OutLap)).ToList();

        // The same stint numbering as StintAnalyzer: a new stint at every tyre change, counted from 1.
        var stintByLap = new Dictionary<int, (int Number, int FirstLap)>();
        var stint = 0;
        var firstLap = 0;
        for (var i = 0; i < race.Laps.Count; i++)
        {
            if (i == 0 || LapClassifier.StintChanged(race.Laps[i - 1], race.Laps[i]))
            {
                stint++;
                firstLap = race.Laps[i].LapNumber;
            }

            stintByLap[race.Laps[i].LapNumber] = (stint, firstLap);
        }

        double total = 0, pit = stops.Sum(s => s.LossSeconds), tyre = 0, fuel = 0, basePace = 0;
        foreach (var lap in race.Laps.Where(l => l.HasTime && window.Contains(l.LapNumber)))
        {
            var seconds = lap.LapTimeMs / 1000.0;
            total += seconds;
            var (number, start) = stintByLap[lap.LapNumber];
            var fit = fits.GetValueOrDefault(number);
            var lapBase = fit?.BaseLapSeconds ?? fallbackBase;
            if (lapBase is null)
            {
                continue; // no clean lap anywhere: the whole lap stays in "other"
            }

            basePace += lapBase.Value;
            tyre += (fit?.DegradationPerLap ?? 0) * (lap.LapNumber - start);
            fuel += fuelEffect * fuelByLap.GetValueOrDefault(lap.LapNumber);
        }

        var breakdown = new TimeBreakdown(total, pit, tyre, fuel, basePace, total - pit - tyre - fuel - basePace);
        return (breakdown, fits.Values.OrderBy(f => f.StintNumber).ToList(), stops);
    }
}
