using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Formatting;
using F1Telemetry.Core.Models;

namespace F1Telemetry.Core.Analytics;

public sealed record LimitingTyre(string Name, double LapsRemaining, double RatePerLap, double Wear);

public sealed record StintAnalysis
{
    public required int StintNumber { get; init; }
    public required string Compound { get; init; }
    public required int StartLap { get; init; }
    public required int EndLap { get; init; }
    public required int LapCount { get; init; }
    public required int RegularLapCount { get; init; }

    public required WheelValues AverageWearPerLap { get; init; }
    public double AverageWearCombined => AverageWearPerLap.Average;
    public required WheelValues StartWear { get; init; }
    public required WheelValues EndWear { get; init; }

    /// <summary>Projected laps from fresh tyres to the wear limit.</summary>
    public required double TyreLifespanLaps { get; init; }

    public required LimitingTyre? LimitingTyre { get; init; }

    public required double AverageFuelPerLap { get; init; }
    public required double TotalFuelUsed { get; init; }

    public required uint BestLapMs { get; init; }
    public required double AverageLapMs { get; init; }

    /// <summary>Slope of the raw lap times, seconds per lap: tyre wear net of the fuel burn-off gain.</summary>
    public required double PaceDegradationPerLap { get; init; }

    /// <summary>Slope with the fuel effect taken out, seconds per lap: what the tyres alone cost.</summary>
    public required double FuelCorrectedDegradationPerLap { get; init; }

    /// <summary>The fit behind both slopes; null when the stint has no clean laps.</summary>
    public required StintPaceFit? PaceFit { get; init; }

    /// <summary>Laps used for the pace fit: clean laps without a race's lap 1 and without outliers.</summary>
    public int PaceLapCount => PaceFit?.Laps ?? 0;

    public double TotalStintTimeLoss => FuelCorrectedDegradationPerLap * PaceLapCount;

    public string BestLapFormatted => TimeFormat.Lap(BestLapMs);
    public string AverageLapFormatted => TimeFormat.Lap(AverageLapMs);
    public double FuelForLaps(int laps) => AverageFuelPerLap * laps;
}

public sealed record SessionAnalysis(IReadOnlyList<StintAnalysis> Stints, WheelValues CurrentWear, string LatestCompound, int SessionType)
{
    /// <summary>Fuel effect used for every fuel-corrected figure of this session.</summary>
    public FuelEffect FuelEffect { get; init; } = new(0, FuelEffectSource.Default, "");

    public IReadOnlyList<PitStopAnalysis> PitStops { get; init; } = [];

    /// <summary>Every lap that fed a pace fit, across all stints.</summary>
    public IReadOnlyList<PaceLap> PaceLaps { get; init; } = [];
}

/// <summary>
/// Race strategy post-mortem: segments laps into tyre stints and derives wear rates, projected tyre
/// life, fuel consumption and pace degradation from recorded telemetry.
/// </summary>
public static class StintAnalyzer
{
    public static SessionAnalysis? Analyze(
        IReadOnlyList<LapRecord> classifiedLaps,
        IReadOnlyList<LapAggregate> aggregates,
        WheelValues currentWear,
        int sessionType,
        StrategyOptions? options = null)
    {
        options ??= new StrategyOptions();
        if (classifiedLaps.Count == 0)
        {
            return null;
        }

        var aggregateByLap = aggregates.ToDictionary(a => a.LapNumber);
        var segments = Segment(classifiedLaps).ToList();
        var paceLaps = segments.Select((laps, idx) => PaceModel.CleanLaps(idx + 1, laps, aggregateByLap, sessionType)).ToList();
        var allPaceLaps = paceLaps.SelectMany(l => l).ToList();
        var fuelEffect = PaceModel.Resolve(allPaceLaps, options.FuelEffectSecondsPerKg);
        var stints = segments
            .Select((laps, idx) => AnalyzeStint(idx + 1, laps, paceLaps[idx], fuelEffect.SecondsPerKg, aggregateByLap, options))
            .ToList();

        // Clean-lap time the stint's fit gives any of its laps (the in- and out-laps of a stop, say).
        double? PredictCleanLap(int lapNumber)
        {
            var idx = segments.FindIndex(laps => laps.Exists(l => l.LapNumber == lapNumber));
            return idx >= 0 && stints[idx].PaceFit is { Laps: >= 2 } fit && aggregateByLap.TryGetValue(lapNumber, out var a)
                ? fit.Predict(lapNumber - segments[idx][0].LapNumber, (a.MinFuel + a.MaxFuel) / 2, fuelEffect.SecondsPerKg)
                : null;
        }

        return new SessionAnalysis(stints, currentWear, classifiedLaps[^1].Compound, sessionType)
        {
            FuelEffect = fuelEffect,
            PitStops = PitStopAnalyzer.Analyze(classifiedLaps, sessionType, PredictCleanLap),
            PaceLaps = allPaceLaps,
        };
    }

    private static IEnumerable<List<LapRecord>> Segment(IReadOnlyList<LapRecord> laps)
    {
        var current = new List<LapRecord>();
        foreach (var lap in laps)
        {
            if (current.Count > 0 && IsNewStint(current[^1], lap))
            {
                yield return current;
                current = [];
            }

            current.Add(lap);
        }

        if (current.Count > 0)
        {
            yield return current;
        }
    }

    private static bool IsNewStint(LapRecord previous, LapRecord lap) =>
        previous.StintIndex >= 0 && lap.StintIndex >= 0
            ? previous.StintIndex != lap.StintIndex
            : previous.Compound != lap.Compound;

    private static StintAnalysis AnalyzeStint(int number, List<LapRecord> laps, List<PaceLap> paceLaps, double fuelEffect,
        Dictionary<int, LapAggregate> aggregates, StrategyOptions options)
    {
        var regular = laps.Where(l => l.LapType == LapType.Regular && l.HasTime).ToList();

        double sumFl = 0, sumFr = 0, sumRl = 0, sumRr = 0, fuelSum = 0;
        int wearCount = 0, fuelCount = 0;
        foreach (var lap in regular)
        {
            if (!aggregates.TryGetValue(lap.LapNumber, out var a))
            {
                continue;
            }

            sumFl += Math.Max(0, a.MaxWearFl - a.MinWearFl);
            sumFr += Math.Max(0, a.MaxWearFr - a.MinWearFr);
            sumRl += Math.Max(0, a.MaxWearRl - a.MinWearRl);
            sumRr += Math.Max(0, a.MaxWearRr - a.MinWearRr);
            wearCount++;

            var used = a.MaxFuel - a.MinFuel;
            if (used > 0 && used < options.MaxPlausibleFuelPerLap)
            {
                fuelSum += used;
                fuelCount++;
            }
        }

        var rate = wearCount > 0
            ? new WheelValues(sumFl / wearCount, sumFr / wearCount, sumRl / wearCount, sumRr / wearCount)
            : default;

        aggregates.TryGetValue(laps[0].LapNumber, out var first);
        aggregates.TryGetValue(laps[^1].LapNumber, out var last);
        var startWear = first is null ? default : new WheelValues(first.MinWearFl, first.MinWearFr, first.MinWearRl, first.MinWearRr);
        var endWear = last is null ? default : new WheelValues(last.MaxWearFl, last.MaxWearFr, last.MaxWearRl, last.MaxWearRr);

        LimitingTyre? limiting = null;
        for (var i = 0; i < 4; i++)
        {
            if (rate[i] <= 0)
            {
                continue;
            }

            var remaining = (options.TyreWearLimitPercent - endWear[i]) / rate[i];
            if (limiting is null || remaining < limiting.LapsRemaining)
            {
                limiting = new LimitingTyre(WheelValues.Names[i], remaining, rate[i], endWear[i]);
            }
        }

        var lapTimes = regular.Select(l => (double)l.LapTimeMs).ToList();
        var fit = PaceModel.FitStint(number, laps[0].Compound, paceLaps, fuelEffect);
        return new StintAnalysis
        {
            StintNumber = number,
            Compound = laps[0].Compound,
            StartLap = laps[0].LapNumber,
            EndLap = laps[^1].LapNumber,
            LapCount = laps.Count,
            RegularLapCount = regular.Count,
            AverageWearPerLap = rate,
            StartWear = startWear,
            EndWear = endWear,
            TyreLifespanLaps = rate.Average > 0 ? options.TyreWearLimitPercent / rate.Average : 0,
            LimitingTyre = limiting,
            AverageFuelPerLap = fuelCount > 0 ? fuelSum / fuelCount : 0,
            TotalFuelUsed = fuelSum,
            BestLapMs = regular.Count > 0 ? regular.Min(l => l.LapTimeMs) : 0,
            AverageLapMs = lapTimes.Count > 0 ? lapTimes.Average() : 0,
            PaceDegradationPerLap = fit?.RawDegradationPerLap ?? 0,
            FuelCorrectedDegradationPerLap = fit?.DegradationPerLap ?? 0,
            PaceFit = fit,
        };
    }
}

public static class LinearRegression
{
    /// <summary>Least-squares slope dy/dx; 0 when fewer than two points or x has no variance.</summary>
    public static double Slope(IReadOnlyList<(double X, double Y)> points)
    {
        var n = points.Count;
        if (n < 2)
        {
            return 0;
        }

        double sumX = 0, sumY = 0, sumXy = 0, sumXx = 0;
        foreach (var (x, y) in points)
        {
            sumX += x;
            sumY += y;
            sumXy += x * y;
            sumXx += x * x;
        }

        var denominator = n * sumXx - sumX * sumX;
        return denominator == 0 ? 0 : (n * sumXy - sumX * sumY) / denominator;
    }
}
