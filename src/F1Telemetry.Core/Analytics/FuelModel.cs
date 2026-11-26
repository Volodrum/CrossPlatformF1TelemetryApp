using System.Globalization;
using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol.Lookups;

namespace F1Telemetry.Core.Analytics;

/// <summary>What a run (one set of tyres on one fill of fuel) was driven as.</summary>
public enum FuelRunKind
{
    /// <summary>Driven in a race.</summary>
    Race,

    /// <summary>A practice run driven as a race: a long run, or a few laps on a heavy tank.</summary>
    RaceSim,

    /// <summary>Driven in qualifying (or a sprint shootout).</summary>
    Qualifying,

    /// <summary>A practice run driven as qualifying: a short run on a light tank, or as fast as the session's best.</summary>
    QualiSim,

    /// <summary>A practice run that is neither: shown, not used.</summary>
    Other,
}

/// <summary>What a lap inside a run was.</summary>
public enum FuelLapKind
{
    /// <summary>A timed lap at the run's pace: race pace in a race or race simulation, a push lap in qualifying.</summary>
    Push,

    /// <summary>A timed lap well off the run's best (more than <see cref="FuelModel.CoolDownRatio"/>): a cool-down lap between pushes.</summary>
    CoolDown,

    /// <summary>The lap out of the pits or the garage.</summary>
    OutLap,

    /// <summary>The lap back into the pits.</summary>
    InLap,

    /// <summary>A lap behind a safety car or virtual safety car.</summary>
    SafetyCar,

    /// <summary>Anything else (a red flag, a lap without a time in the middle of a run): not used.</summary>
    Other,
}

/// <summary>One lap's fuel: what it burnt (kg; 0 when not measured) and what it was.</summary>
public sealed record FuelLap(int LapNumber, FuelLapKind Kind, double Kg, uint LapTimeMs);

/// <summary>A run of a session with what it was driven as, and why it was read so.</summary>
/// <param name="StartFuelKg">Fuel in the tank at the start of its first lap; 0 when not recorded.</param>
public sealed record FuelRun(
    RaceInput Session,
    int Number,
    int StartLap,
    int EndLap,
    string Compound,
    FuelRunKind Kind,
    string Why,
    double StartFuelKg,
    IReadOnlyList<FuelLap> Laps)
{
    public IEnumerable<FuelLap> Measured(FuelLapKind kind) => Laps.Where(l => l.Kind == kind && l.Kg > 0);

    /// <summary>Median burn of its push laps; 0 when none was measured.</summary>
    public double PushKgPerLap => Measured(FuelLapKind.Push).Select(l => l.Kg).ToList() is { Count: > 0 } kg ? PaceModel.Median(kg) : 0;

    public int PushLaps => Laps.Count(l => l.Kind == FuelLapKind.Push);

    public uint BestLapMs => Laps.Where(l => l.Kind is FuelLapKind.Push).Select(l => l.LapTimeMs).DefaultIfEmpty().Min();
}

/// <summary>What one kind of lap burns per lap at the track.</summary>
/// <param name="KgPerLap">Median burn, kg.</param>
/// <param name="Low">25th percentile, kg.</param>
/// <param name="High">75th percentile, kg.</param>
/// <param name="FromOtherLengths">No session at the race length planned for had one: taken from every length.</param>
public sealed record FuelBurn(double KgPerLap, double Low, double High, int Laps, int Runs, int Sessions, bool FromOtherLengths);

/// <summary>A lighter start than the fuel the race needs: what saving it takes and what the lighter car gains.</summary>
/// <param name="LapsLess">Laps of fuel left out.</param>
/// <param name="StartKg">Fuel to start with.</param>
/// <param name="SaveKgPerLap">Fuel to save every lap to finish.</param>
/// <param name="SavePercent">That saving against the race burn, %.</param>
/// <param name="GainSeconds">Race time the lighter car gains (before the saving's own cost).</param>
public sealed record LighterStart(double LapsLess, double StartKg, double SaveKgPerLap, double SavePercent, double GainSeconds);

/// <param name="BurnKgPerLap">The race burn planned with.</param>
/// <param name="FromRaceSims">No race was recorded: the burn is the practice race simulations'.</param>
/// <param name="FormationKg">The formation lap's fuel; 0 when left out.</param>
/// <param name="FormationMeasured">Whether that is a measured out-lap's burn (or a share of the race burn).</param>
/// <param name="MarginKg">The margin carried on top.</param>
/// <param name="MarginCostSeconds">What carrying the margin the whole race costs.</param>
public sealed record RaceFuelPlan(
    int Laps,
    double BurnKgPerLap,
    bool FromRaceSims,
    bool FromOtherLengths,
    double FormationKg,
    bool FormationMeasured,
    double MarginLaps,
    double MarginKg,
    double MarginCostSeconds,
    double NeededKg,
    double StartKg,
    IReadOnlyList<LighterStart> Lighter);

/// <summary>One part of a qualifying run's fuel.</summary>
/// <param name="Measured">Whether its burn is measured at the track (or assumed).</param>
public sealed record QualiFuelPart(string Name, int Laps, double KgPerLap, bool Measured)
{
    public double Kg => Laps * KgPerLap;
}

public sealed record QualiFuelPlan(int PushLaps, IReadOnlyList<QualiFuelPart> Parts, bool PushFromRace)
{
    public double TotalKg => Parts.Sum(p => p.Kg);
}

/// <summary>
/// The fuel a track's sessions burn, split by what each run was driven as: a race, a practice race simulation, a
/// qualifying run or a practice qualifying simulation (see <see cref="FuelModelBuilder"/>). The race and qualifying
/// calculators each use the laps driven the way they plan for, so a light, flat-out qualifying simulation doesn't skew
/// the race fuel, nor a heavy long run the qualifying fuel.
/// </summary>
public sealed record FuelModel(IReadOnlyList<FuelRun> Runs, int RaceLength)
{
    /// <summary>A timed lap slower than the run's best by more than this ratio is a cool-down lap.</summary>
    public const double CoolDownRatio = 1.05;

    /// <summary>A lap's share of the race burn assumed for laps never measured (out-lap, in-lap, cool-down, formation).</summary>
    public const double SlowLapShare = 0.6;

    public FuelBurn? Race { get; init; }
    public FuelBurn? RaceSim { get; init; }
    public FuelBurn? Qualifying { get; init; }
    public FuelBurn? QualiSim { get; init; }
    public FuelBurn? OutLap { get; init; }
    public FuelBurn? InLap { get; init; }
    public FuelBurn? CoolDown { get; init; }
    public FuelBurn? SafetyCar { get; init; }

    /// <summary>Qualifying push laps: those of qualifying and of the practice qualifying simulations, pooled.</summary>
    public FuelBurn? Push { get; init; }

    /// <summary>The race burn to plan with: the races', else the race simulations'.</summary>
    public FuelBurn? RaceBurn => Race ?? RaceSim;

    public bool HasData => Runs.Count > 0;

    /// <summary>
    /// The fuel to start a race of <paramref name="laps"/> laps with: every lap at the race burn, the formation lap, and
    /// <paramref name="marginLaps"/> laps' more; with lighter starts (1, 2 and 3 laps less than the fuel needed) and what
    /// they take and give. Null when no race or race simulation was measured.
    /// </summary>
    /// <param name="fuelEffect">Lap time cost of fuel, s/kg.</param>
    public RaceFuelPlan? PlanRace(int laps, double marginLaps, bool formationLap, double fuelEffect)
    {
        if (RaceBurn is not { KgPerLap: > 0 } burn || laps <= 0)
        {
            return null;
        }

        var perLap = burn.KgPerLap;
        var formation = !formationLap ? 0 : OutLap?.KgPerLap ?? SlowLapShare * perLap;
        var needed = laps * perLap + formation;
        var margin = Math.Max(0, marginLaps) * perLap;

        // Starting Δ lighter and saving it evenly, the car is Δ lighter at the start and level at the flag: Δ / 2 a lap on
        // average. A margin left in the tank at the flag is carried the whole race.
        var lighter = new[] { 1.0, 2.0, 3.0 }
            .Select(k => (Laps: k, Kg: k * perLap))
            .Where(x => x.Kg < needed)
            .Select(x => new LighterStart(x.Laps, needed - x.Kg, x.Kg / laps, 100.0 * x.Kg / laps / perLap, fuelEffect * x.Kg / 2 * laps))
            .ToList();
        return new RaceFuelPlan(laps, perLap, Race is null, burn.FromOtherLengths, formation, formationLap && OutLap is not null, marginLaps, margin,
            fuelEffect * margin * laps, needed, needed + margin, lighter);
    }

    /// <summary>
    /// The fuel a qualifying run takes: the out-lap, <paramref name="pushLaps"/> push laps with a cool-down lap between
    /// each, and the in-lap when <paramref name="inLap"/>. Parts never measured take <see cref="SlowLapShare"/> of the push
    /// burn; push laps never measured take the race burn. Null without either.
    /// </summary>
    public QualiFuelPlan? PlanQualifying(int pushLaps, bool inLap)
    {
        var push = Push?.KgPerLap ?? RaceBurn?.KgPerLap;
        if (push is not > 0 || pushLaps <= 0)
        {
            return null;
        }

        var parts = new List<QualiFuelPart>
        {
            new("Out-lap", 1, OutLap?.KgPerLap ?? SlowLapShare * push.Value, OutLap is not null),
            new("Push laps", pushLaps, push.Value, Push is not null),
        };
        if (pushLaps > 1)
        {
            parts.Add(new("Cool-down laps", pushLaps - 1, CoolDown?.KgPerLap ?? SlowLapShare * push.Value, CoolDown is not null));
        }

        if (inLap)
        {
            parts.Add(new("In-lap", 1, InLap?.KgPerLap ?? SlowLapShare * push.Value, InLap is not null));
        }

        return new QualiFuelPlan(pushLaps, parts, Push is null);
    }
}

/// <summary>
/// Reads every run of a track's sessions as a race, a race simulation, a qualifying run, a qualifying simulation or
/// neither, and what each of its laps burnt.
/// <list type="bullet">
/// <item>A race's runs are race runs; a qualifying session's are qualifying runs. Time trial is left out.</item>
/// <item>A practice run (or a session of unknown type) is a race simulation with <see cref="RaceSimLaps"/>+ push laps,
/// or <see cref="HeavyRunLaps"/>+ on a tank holding <see cref="HeavyExtraLaps"/> laps more than it ran; a qualifying
/// simulation on a tank holding at most <see cref="LightExtraLaps"/> laps more than the run, or with a best lap within
/// <see cref="QualiSimPace"/> of the session's best; otherwise neither.</item>
/// <item>In a run, the laps before its first timed lap are out-laps and those after its last are in-laps (in a race,
/// its pit laps); a timed lap over <see cref="FuelModel.CoolDownRatio"/> of the run's best is a cool-down lap.</item>
/// </list>
/// A lap's burn is its fuel at the start less at the end; out- and in-laps burning more than 1.5 times the run's push
/// laps had fuel changed in the garage and aren't used.
/// </summary>
public static class FuelModelBuilder
{
    /// <summary>Push laps from which a practice run is a race simulation, whatever its fuel.</summary>
    public const int RaceSimLaps = 5;

    /// <summary>Push laps from which a run on a heavy tank is a race simulation.</summary>
    public const int HeavyRunLaps = 3;

    /// <summary>A tank holding this many laps more than the run drove is heavy.</summary>
    public const double HeavyExtraLaps = 6;

    /// <summary>A tank holding at most this many laps more than the run drove is light.</summary>
    public const double LightExtraLaps = 3;

    /// <summary>A short run whose best lap is within this share of the session's best is a qualifying simulation.</summary>
    public const double QualiSimPace = 0.01;

    /// <param name="sessions">Every analysed session at the track, in one game format.</param>
    /// <param name="raceLength">
    /// The race length planned for: each burn comes from the sessions counted for it, and from every length when none of
    /// them had that kind of lap. 0 pools every length.
    /// </param>
    public static FuelModel Build(IReadOnlyList<RaceInput> sessions, int raceLength = 0, StrategyOptions? options = null)
    {
        options ??= new StrategyOptions();
        var target = RaceLengths.IsKnown(raceLength) ? raceLength : 0;
        var runs = sessions.Where(s => s.Recording.SessionType != SessionTypes.TimeTrial).SelectMany(s => Runs(s, options)).ToList();

        FuelBurn? Burn(Func<FuelRun, bool> runFilter, params FuelLapKind[] kinds)
        {
            var picked = runs.Where(runFilter).ToList();
            var atTarget = target == 0 ? picked : picked.Where(r => r.Session.RaceLength == target).ToList();
            return Stats(atTarget, kinds, false) ?? (target == 0 ? null : Stats(picked, kinds, true));
        }

        static bool QualiRun(FuelRun r) => r.Kind is FuelRunKind.QualiSim or FuelRunKind.Qualifying;
        var qualifyingRuns = runs.Any(r => QualiRun(r) && r.Measured(FuelLapKind.OutLap).Any());
        return new FuelModel(runs, target)
        {
            Race = Burn(r => r.Kind == FuelRunKind.Race, FuelLapKind.Push),
            RaceSim = Burn(r => r.Kind == FuelRunKind.RaceSim, FuelLapKind.Push),
            Qualifying = Burn(r => r.Kind == FuelRunKind.Qualifying, FuelLapKind.Push),
            QualiSim = Burn(r => r.Kind == FuelRunKind.QualiSim, FuelLapKind.Push),
            Push = Burn(QualiRun, FuelLapKind.Push),

            // A qualifying run's out-lap is what the qualifying plan needs; a race's out-lap after a stop is driven flat out.
            OutLap = Burn(r => qualifyingRuns ? QualiRun(r) : r.Kind != FuelRunKind.Race, FuelLapKind.OutLap) ?? Burn(_ => true, FuelLapKind.OutLap),
            InLap = Burn(r => r.Kind != FuelRunKind.Race, FuelLapKind.InLap) ?? Burn(_ => true, FuelLapKind.InLap),
            CoolDown = Burn(r => r.Kind != FuelRunKind.Race, FuelLapKind.CoolDown),
            SafetyCar = Burn(_ => true, FuelLapKind.SafetyCar),
        };
    }

    private static FuelBurn? Stats(IReadOnlyList<FuelRun> runs, FuelLapKind[] kinds, bool fromOtherLengths)
    {
        var laps = runs.SelectMany(r => kinds.SelectMany(r.Measured).Select(l => (Run: r, l.Kg))).ToList();
        if (laps.Count == 0)
        {
            return null;
        }

        var kg = laps.Select(l => l.Kg).Order().ToList();
        return new FuelBurn(PaceModel.Median(kg), Quantile(kg, 0.25), Quantile(kg, 0.75), laps.Count, laps.Select(l => l.Run).Distinct().Count(),
            laps.Select(l => l.Run.Session).Distinct().Count(), fromOtherLengths);
    }

    private static double Quantile(IReadOnlyList<double> sorted, double q)
    {
        var position = q * (sorted.Count - 1);
        var low = (int)Math.Floor(position);
        var high = Math.Min(sorted.Count - 1, low + 1);
        return sorted[low] + (sorted[high] - sorted[low]) * (position - low);
    }

    /// <summary>The runs of one session, each read as what it was driven as.</summary>
    public static IReadOnlyList<FuelRun> Runs(RaceInput session, StrategyOptions? options = null)
    {
        options ??= new StrategyOptions();
        var type = session.Recording.SessionType;
        var aggregates = session.Aggregates.GroupBy(a => a.LapNumber).ToDictionary(g => g.Key, g => g.First());
        var race = SessionTypes.IsRace(type);

        double Burnt(int lap) => aggregates.TryGetValue(lap, out var a) && a.MaxFuel - a.MinFuel is var used && used > 0 && used < options.MaxPlausibleFuelPerLap
            ? used
            : 0;

        var runs = new List<(StintAnalysis Stint, List<FuelLap> Laps, double StartFuel)>();
        foreach (var stint in session.Analysis.Stints)
        {
            var laps = session.Laps.Where(l => l.LapNumber >= stint.StartLap && l.LapNumber <= stint.EndLap).ToList();
            if (laps.Count == 0)
            {
                continue;
            }

            var timed = laps.Where(l => l.HasTime && l.LapType is LapType.Regular).ToList();
            var best = timed.Count > 0 ? timed.Min(l => l.LapTimeMs) : 0u;
            var firstTimed = timed.Count > 0 ? timed[0].LapNumber : int.MaxValue;
            var lastTimed = timed.Count > 0 ? timed[^1].LapNumber : int.MinValue;
            var fuelLaps = laps.Select(l => new FuelLap(l.LapNumber, Kind(l, race, laps[0].LapNumber, firstTimed, lastTimed, best), Burnt(l.LapNumber), l.LapTimeMs)).ToList();

            // Out- and in-laps with fuel changed in the garage on them.
            var push = fuelLaps.Where(l => l.Kind == FuelLapKind.Push && l.Kg > 0).Select(l => l.Kg).ToList();
            if (push.Count > 0)
            {
                var cap = 1.5 * PaceModel.Median(push);
                fuelLaps = [.. fuelLaps.Select(l => l.Kind is FuelLapKind.OutLap or FuelLapKind.InLap && l.Kg > cap ? l with { Kg = 0 } : l)];
            }

            runs.Add((stint, fuelLaps, aggregates.TryGetValue(laps[0].LapNumber, out var first) ? first.MaxFuel : 0));
        }

        var sessionPush = runs.SelectMany(r => r.Laps).Where(l => l.Kind == FuelLapKind.Push && l.Kg > 0).Select(l => l.Kg).ToList();
        var sessionBurn = sessionPush.Count > 0 ? PaceModel.Median(sessionPush) : 0;
        var sessionBest = runs.SelectMany(r => r.Laps).Where(l => l.Kind == FuelLapKind.Push).Select(l => l.LapTimeMs).DefaultIfEmpty().Min();

        return
        [
            .. runs.Select((r, i) =>
            {
                var (kind, why) = race ? (FuelRunKind.Race, "Driven in the race.")
                    : SessionTypes.IsQualifying(type) ? (FuelRunKind.Qualifying, "Driven in qualifying.")
                    : Classify(r.Laps, r.StartFuel, sessionBurn, sessionBest);
                return new FuelRun(session, i + 1, r.Stint.StartLap, r.Stint.EndLap, r.Stint.Compound, kind, why, r.StartFuel, r.Laps);
            }),
        ];
    }

    private static FuelLapKind Kind(LapRecord lap, bool race, int firstLap, int firstTimed, int lastTimed, uint best)
    {
        if (lap.LapType == LapType.RedFlag)
        {
            return FuelLapKind.Other;
        }

        if (lap.LapType is LapType.SafetyCar or LapType.VirtualSafetyCar)
        {
            return FuelLapKind.SafetyCar;
        }

        if (lap.LapType == LapType.Pit || !lap.HasTime)
        {
            return lap.LapNumber < firstTimed || (race && lap.LapNumber == firstLap) ? FuelLapKind.OutLap
                : lap.LapNumber > lastTimed || race ? FuelLapKind.InLap
                : FuelLapKind.Other;
        }

        return !race && best > 0 && lap.LapTimeMs > best * FuelModel.CoolDownRatio ? FuelLapKind.CoolDown : FuelLapKind.Push;
    }

    /// <summary>What a practice run was driven as, and why.</summary>
    private static (FuelRunKind, string) Classify(IReadOnlyList<FuelLap> laps, double startFuel, double sessionBurn, uint sessionBest)
    {
        var push = laps.Where(l => l.Kind == FuelLapKind.Push).ToList();
        var measured = push.Where(l => l.Kg > 0).Select(l => l.Kg).ToList();
        var burn = measured.Count > 0 ? PaceModel.Median(measured) : sessionBurn;
        double? tankLaps = startFuel > 0 && burn > 0 ? startFuel / burn : null;
        var best = push.Count > 0 ? push.Min(l => l.LapTimeMs) : 0u;
        var gap = sessionBest > 0 && best > 0 ? (double)best / sessionBest - 1 : double.NaN;
        string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
        var fuel = tankLaps is { } t ? Inv($"{startFuel:0.0} kg in the tank, about {t:0} laps' worth") : "";

        if (push.Count == 0)
        {
            return (FuelRunKind.Other, "No timed lap at its pace.");
        }

        if (push.Count >= RaceSimLaps)
        {
            return (FuelRunKind.RaceSim, Inv($"{push.Count} laps at a steady pace: a long run."));
        }

        if (tankLaps is { } light && light <= laps.Count + LightExtraLaps)
        {
            return (FuelRunKind.QualiSim, $"A short run on a light tank ({fuel}).");
        }

        if (push.Count >= HeavyRunLaps && tankLaps is { } heavy && heavy >= push.Count + HeavyExtraLaps)
        {
            return (FuelRunKind.RaceSim, Inv($"{push.Count} laps on a heavy tank ({fuel}): a short race run."));
        }

        if (gap <= QualiSimPace)
        {
            return (FuelRunKind.QualiSim, gap < 0.0005
                ? "A short run with the session's fastest lap."
                : Inv($"A short run with a lap within {gap * 100:0.0} % of the session's fastest."));
        }

        return (FuelRunKind.Other, Inv($"{push.Count} {(push.Count == 1 ? "lap" : "laps")}, neither long nor on a light tank, and off the session's best pace."));
    }
}
