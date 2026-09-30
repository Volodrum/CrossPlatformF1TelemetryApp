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

    /// <summary>Linear-regression slope of lap time against lap number, seconds per lap.</summary>
    public required double PaceDegradationPerLap { get; init; }

    public double TotalStintTimeLoss => PaceDegradationPerLap * RegularLapCount;

    public string BestLapFormatted => TimeFormat.Lap(BestLapMs);
    public string AverageLapFormatted => TimeFormat.Lap(AverageLapMs);
    public double FuelForLaps(int laps) => AverageFuelPerLap * laps;
}

public sealed record SessionAnalysis(IReadOnlyList<StintAnalysis> Stints, WheelValues CurrentWear, string LatestCompound, int SessionType);

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
        var stints = Segment(classifiedLaps)
            .Select((laps, idx) => AnalyzeStint(idx + 1, laps, aggregateByLap, options))
            .ToList();

        return new SessionAnalysis(stints, currentWear, classifiedLaps[^1].Compound, sessionType);
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

    private static StintAnalysis AnalyzeStint(int number, List<LapRecord> laps, Dictionary<int, LapAggregate> aggregates, StrategyOptions options)
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
            PaceDegradationPerLap = LinearRegression.Slope(regular.Select(l => ((double)l.LapNumber, (double)l.LapTimeMs)).ToList()) / 1000.0,
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
