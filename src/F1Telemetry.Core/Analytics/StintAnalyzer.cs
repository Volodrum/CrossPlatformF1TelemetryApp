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
        var segments = Segment(classifiedLaps, aggregateByLap).Select(OneCompound).ToList();
        var paceLaps = segments.Select((laps, idx) => PaceModel.CleanLaps(idx + 1, laps, aggregateByLap, sessionType, options.TyreCliffWearPercent)).ToList();
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

    /// <summary>Fuel this much above what the previous lap ended on was put in: the car went back to the garage.</summary>
    public const double RefuelKg = 1.0;

    /// <summary>Fuel this much below what the previous lap ended on (more than two laps' burn) was taken out in the garage.</summary>
    public const double DefuelKg = 3.0;

    /// <summary>Wear (%) a set may move between the end of one lap and the start of the next and still be the same set.</summary>
    public const double SameSetWear = 0.5;

    /// <summary>
    /// Splits the laps into runs: one set of tyres on one fill of fuel. The game's stint index decides, except where the
    /// fuel and wear say otherwise:
    /// <list type="bullet">
    /// <item>Fuel going up between two laps (or down by more than the burn) is the garage changing it (practice,
    /// qualifying): a new run, even on the same set, which the game can keep in one stint.</item>
    /// <item>Fuel and every tyre's wear carrying straight on from a worn set is the same run, however the game numbers it.
    /// In practice the game ends a stint a lap early (the lap before the in-lap), which would put the last lap on a
    /// set onto the next set's compound.</item>
    /// </list>
    /// </summary>
    internal static IEnumerable<List<LapRecord>> Segment(IReadOnlyList<LapRecord> laps, IReadOnlyDictionary<int, LapAggregate> aggregates)
    {
        var current = new List<LapRecord>();
        foreach (var lap in laps)
        {
            if (current.Count > 0 && IsNewStint(current[^1], lap, aggregates))
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

    private static bool IsNewStint(LapRecord previous, LapRecord lap, IReadOnlyDictionary<int, LapAggregate> aggregates)
    {
        var gameSays = previous.StintIndex >= 0 && lap.StintIndex >= 0
            ? previous.StintIndex != lap.StintIndex
            : previous.Compound != lap.Compound;
        if (!aggregates.TryGetValue(previous.LapNumber, out var before) || !aggregates.TryGetValue(lap.LapNumber, out var after)
            || before.MaxFuel <= 0 || after.MaxFuel <= 0)
        {
            return gameSays;
        }

        // Fuel put in, or taken out (a low-fuel run after a heavy one): only the garage changes it.
        if (after.MaxFuel > before.MinFuel + RefuelKg || after.MaxFuel < before.MinFuel - DefuelKg)
        {
            return true;
        }

        return gameSays && !SameSet(before, after);
    }

    /// <summary>Every tyre of <paramref name="after"/> starts where <paramref name="before"/> left it, on a set already worn.</summary>
    private static bool SameSet(LapAggregate before, LapAggregate after)
    {
        (double End, double Start)[] tyres =
        [
            (before.MaxWearFl, after.MinWearFl), (before.MaxWearFr, after.MinWearFr),
            (before.MaxWearRl, after.MinWearRl), (before.MaxWearRr, after.MinWearRr),
        ];
        return tyres.Max(t => t.End) > 2 * SameSetWear && tyres.All(t => Math.Abs(t.Start - t.End) <= SameSetWear);
    }

    /// <summary>A run is on one compound: a lap the game put on another (see <see cref="Segment"/>) takes the run's.</summary>
    private static List<LapRecord> OneCompound(List<LapRecord> run)
    {
        var compound = run.GroupBy(l => l.Compound).OrderByDescending(g => g.Count()).ThenBy(g => run.FindIndex(l => l.Compound == g.Key)).First().Key;
        return run.All(l => l.Compound == compound) ? run : [.. run.Select(l => l with { Compound = compound })];
    }

    private static StintAnalysis AnalyzeStint(int number, List<LapRecord> laps, List<PaceLap> paceLaps, double fuelEffect,
        Dictionary<int, LapAggregate> aggregates, StrategyOptions options)
    {
        var regular = laps.Where(l => l.LapType == LapType.Regular && l.HasTime).ToList();

        // Per lap, then the median: one lap with a flashback, a refuel artefact or a trip through the gravel can't move it.
        List<double> fl = [], fr = [], rl = [], rr = [], fuel = [];
        foreach (var lap in regular)
        {
            if (!aggregates.TryGetValue(lap.LapNumber, out var a))
            {
                continue;
            }

            fl.Add(Math.Max(0, a.MaxWearFl - a.MinWearFl));
            fr.Add(Math.Max(0, a.MaxWearFr - a.MinWearFr));
            rl.Add(Math.Max(0, a.MaxWearRl - a.MinWearRl));
            rr.Add(Math.Max(0, a.MaxWearRr - a.MinWearRr));

            var used = a.MaxFuel - a.MinFuel;
            if (used > 0 && used < options.MaxPlausibleFuelPerLap)
            {
                fuel.Add(used);
            }
        }

        var rate = fl.Count > 0
            ? new WheelValues(PaceModel.Median(fl), PaceModel.Median(fr), PaceModel.Median(rl), PaceModel.Median(rr))
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
            AverageFuelPerLap = fuel.Count > 0 ? PaceModel.Median(fuel) : 0,
            TotalFuelUsed = fuel.Sum(),
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
