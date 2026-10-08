using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Models;

namespace F1Telemetry.Core.Energy;

/// <summary>ERS deploy modes as the game numbers them.</summary>
public static class DeployModes
{
    public const int None = 0;
    public const int Medium = 1;
    public const int Hotlap = 2;
    public const int Overtake = 3;

    public static string Name(int mode) => mode switch
    {
        None => "None",
        Medium => "Medium",
        Hotlap => "Hotlap",
        Overtake => "Overtake",
        _ => $"Mode {mode}",
    };

    public static string Letter(int mode) => mode switch
    {
        None => "N",
        Medium => "M",
        Hotlap => "H",
        Overtake => "O",
        _ => "?",
    };
}

/// <summary>A stretch of the lap run in one deploy mode, by lap distance (m).</summary>
public sealed record ModeRun(int Mode, double From, double To);

public enum EnergyWaste
{
    /// <summary>Full throttle with an empty battery: no electric power.</summary>
    Flat,

    /// <summary>Braking with a full battery: the energy the MGU-K could have harvested is lost.</summary>
    Full,

    /// <summary>Braking after the lap's harvest limit was reached: nothing more can be harvested this lap.</summary>
    Capped,
}

/// <summary>Time spent in a <see cref="EnergyWaste"/> situation on one lap.</summary>
public sealed record EnergyIssue(EnergyWaste Kind, double Seconds);

/// <summary>
/// One lap's energy. Energies are in J, distances in m.
/// </summary>
/// <param name="HarvestLimit">The lap's harvest limit (2026 format); 0 when the game doesn't report one.</param>
/// <param name="LimitReachedAt">Lap distance where the harvest limit was reached, if it was.</param>
public sealed record LapEnergy(
    int LapNumber,
    double StartStore,
    double EndStore,
    double MinStore,
    double MaxStore,
    double Harvested,
    double Deployed,
    double HarvestLimit,
    double? LimitReachedAt,
    IReadOnlyList<EnergyIssue> Issues,
    IReadOnlyList<ModeRun> Modes)
{
    public double NetChange => EndStore - StartStore;
}

/// <summary>
/// What the battery did on a lap: where it started and ended, what was harvested and deployed, where the harvest limit
/// was reached, which deploy mode ran where, and where energy was wasted (<see cref="EnergyWaste"/>).
/// </summary>
public static class EnergyAnalyzer
{
    public const double Capacity = FieldTracker.ErsCapacityJoules;

    /// <summary>Within this of empty or full counts as empty or full (1 % of the store).</summary>
    public const double Margin = Capacity / 100;

    /// <summary>Waste shorter than this is noise: a sample or two at the edge of a braking zone.</summary>
    public static readonly IReadOnlyDictionary<EnergyWaste, double> ReportFrom = new Dictionary<EnergyWaste, double>
    {
        [EnergyWaste.Flat] = 0.2,
        [EnergyWaste.Full] = 0.5,
        [EnergyWaste.Capped] = 1.0,
    };

    // Longer gaps between samples are pauses or dropped packets, not driving.
    private const double MaxStep = 0.5;

    private const double FullThrottle = 0.98;
    private const double Braking = 0.05;

    /// <summary>Every lap of a recording, from its samples in session-time order (any lap order is fine).</summary>
    public static IReadOnlyList<LapEnergy> AnalyzeLaps(IEnumerable<TelemetrySample> samples) =>
        samples.GroupBy(s => s.LapNumber)
            .Where(g => g.Key > 0)
            .OrderBy(g => g.Key)
            .Select(g => Analyze(g.Key, g.ToList()))
            .Where(e => e is not null)
            .Select(e => e!)
            .ToList();

    /// <summary>One lap, from its samples in session-time order. Null without samples.</summary>
    public static LapEnergy? Analyze(int lapNumber, IReadOnlyList<TelemetrySample> lap)
    {
        if (lap.Count == 0)
        {
            return null;
        }

        // The game resets the per-lap counters at the line, but not always on the same packet as the lap number:
        // the first samples of a lap can still carry the previous lap's totals.
        var harvestFrom = AfterLastReset(lap, s => s.ErsHarvestedMguk);
        var deployFrom = AfterLastReset(lap, s => s.ErsDeployed);
        var harvested = Max(lap, harvestFrom, s => s.ErsHarvestedMguk);
        var deployed = Max(lap, deployFrom, s => s.ErsDeployed);
        var limit = lap.Max(s => s.ErsHarvestLimit);

        int? limitIndex = null;
        if (limit > 0)
        {
            for (var i = harvestFrom; i < lap.Count; i++)
            {
                if (lap[i].ErsHarvestedMguk >= limit - 1_000)
                {
                    limitIndex = i;
                    break;
                }
            }
        }

        double flat = 0, full = 0, capped = 0;
        for (var i = 0; i < lap.Count - 1; i++)
        {
            var s = lap[i];
            var dt = lap[i + 1].SessionTime - s.SessionTime;
            if (dt is <= 0 or > MaxStep)
            {
                continue;
            }

            var braking = s.Brake > Braking;
            if (s.Throttle >= FullThrottle && !braking && s.ErsStoreEnergy < Margin)
            {
                flat += dt;
            }

            if (braking && s.ErsStoreEnergy > Capacity - Margin)
            {
                full += dt;
            }

            if (braking && i >= limitIndex)
            {
                capped += dt;
            }
        }

        List<EnergyIssue> issues = [];
        foreach (var (kind, seconds) in new[] { (EnergyWaste.Flat, flat), (EnergyWaste.Full, full), (EnergyWaste.Capped, capped) })
        {
            if (seconds >= ReportFrom[kind])
            {
                issues.Add(new EnergyIssue(kind, seconds));
            }
        }

        return new LapEnergy(
            lapNumber,
            StartStore: lap[0].ErsStoreEnergy,
            EndStore: lap[^1].ErsStoreEnergy,
            MinStore: lap.Min(s => s.ErsStoreEnergy),
            MaxStore: lap.Max(s => s.ErsStoreEnergy),
            Harvested: harvested,
            Deployed: deployed,
            HarvestLimit: limit,
            LimitReachedAt: limitIndex is { } index ? lap[index].LapDistance : null,
            Issues: issues,
            Modes: ModeRuns(lap));
    }

    /// <summary>Consecutive samples in the same deploy mode, from the line on (lap distance below 0 is before it).</summary>
    public static IReadOnlyList<ModeRun> ModeRuns(IReadOnlyList<TelemetrySample> lap)
    {
        var runs = new List<ModeRun>();
        int? mode = null;
        double from = 0, last = 0;
        foreach (var s in lap)
        {
            if (s.LapDistance < 0)
            {
                continue;
            }

            if (s.ErsDeployMode != mode)
            {
                if (mode is { } previous)
                {
                    runs.Add(new ModeRun(previous, from, s.LapDistance));
                }

                mode = s.ErsDeployMode;
                from = s.LapDistance;
            }

            last = s.LapDistance;
        }

        if (mode is { } open && last > from)
        {
            runs.Add(new ModeRun(open, from, last));
        }

        return runs;
    }

    /// <summary>Index of the first sample after the counter last dropped (0 when it never did).</summary>
    private static int AfterLastReset(IReadOnlyList<TelemetrySample> lap, Func<TelemetrySample, double> counter)
    {
        var start = 0;
        for (var i = 1; i < lap.Count; i++)
        {
            if (counter(lap[i]) < counter(lap[i - 1]) - 1_000)
            {
                start = i;
            }
        }

        return start;
    }

    private static double Max(IReadOnlyList<TelemetrySample> lap, int from, Func<TelemetrySample, double> value)
    {
        var max = 0.0;
        for (var i = from; i < lap.Count; i++)
        {
            max = Math.Max(max, value(lap[i]));
        }

        return max;
    }
}
