using System.Globalization;
using F1Telemetry.Core.Analytics;
using F1Telemetry.Protocol;

namespace F1Telemetry.Core.Energy;

/// <summary>
/// The energy data guide: what the ERS car model and the lap plans rest on at a track, measured or not yet, and what to
/// drive to measure the rest. The same checklist as the strategy guide (<see cref="DataGuide"/>), built from the car model
/// and the laps the plans are built on. Items are in order of how much the plans depend on them.
/// </summary>
public static class EnergyGuideBuilder
{
    /// <summary>Laps with ICE and MGU-K power from which the model's figures are steady.</summary>
    public const int PowerLapsWanted = 10;

    /// <summary>Typical laps the race plan is averaged over from which it holds for a whole stint.</summary>
    public const int RaceLapsWanted = 5;

    /// <summary>The fewest typical laps a race plan is built on (as the live ERS plan).</summary>
    public const int RaceLapsMinimum = 3;

    /// <summary>A mode's speed curve covers this share of the lap's flat-out speed bands to count as measured.</summary>
    public const double SpeedCoverage = 0.8;

    /// <summary>A mode's own output is known on this share of the flat-out stretches to count as measured on the straights.</summary>
    public const double StraightCoverage = 0.6;

    /// <summary>Harvest is known on this share of the braking and lifting to count as measured.</summary>
    public const double HarvestCoverage = 0.8;

    /// <summary>A qualifying lap that starts with this share of the battery counts as from a full one.</summary>
    public const double FullShare = 0.9;

    // A stretch off full throttle harvests on the reference lap above this, J (25 m): braking or lifting, not coasting.
    private const double HarvestingSegment = 2_000;

    // Below this the aero state is a corner exit, not a straight (as the straight-line fits).
    private const double StraightKmh = 120;

    private static readonly int[] PlannedModes = [DeployModes.None, DeployModes.Medium, DeployModes.Hotlap, DeployModes.Overtake];
    private static readonly int[] DeployingModes = [DeployModes.Medium, DeployModes.Hotlap, DeployModes.Overtake];

    /// <param name="model">The track's car model.</param>
    /// <param name="race">The lap the race plan is built on (typical laps averaged); null when there is none.</param>
    /// <param name="qualifying">The lap the qualifying plan is built on (the fastest); null when there is none.</param>
    public static DataGuide Build(ErsCarModel model, LapProfile? race, LapProfile? qualifying)
    {
        var items = new List<GuideItem>
        {
            PowerLaps(model),
            RaceRun(race),
        };

        // What the lap asks of the car: its flat-out stretches, their speeds, its braking.
        var flat = race?.Segments.Where(s => s.FullThrottle).ToList() ?? [];
        var bands = flat.Select(s => (int)(SpeedKmh(race!, s.Index) / ErsModelBuilder.SpeedBand) * ErsModelBuilder.SpeedBand).ToHashSet();
        if (bands.Count == 0)
        {
            // No lap to plan on yet: the speeds any mode was learned at stand in.
            bands = model.Deploy.Curves.SelectMany(c => c.Points).Select(p => p.SpeedKmh).ToHashSet();
        }

        foreach (var mode in DeployingModes)
        {
            items.Add(ModeSpeeds(model, mode, bands));
        }

        items.Add(ModeStraights(model, flat));
        items.Add(BrakingHarvest(model, race));
        items.Add(AeroStates(model, race));
        items.Add(FullBattery(qualifying));
        if (Replay(model, race) is { } replay)
        {
            items.Add(replay);
        }

        return new DataGuide(items);
    }

    private static GuideItem PowerLaps(ErsCarModel model)
    {
        var laps = model.PowerLaps;
        var state = laps >= PowerLapsWanted ? GuideState.Done : laps > 0 ? GuideState.Partial : GuideState.Missing;
        return new GuideItem(
            "Laps with engine power",
            state,
            laps == 0 ? "no lap with ICE and MGU-K power here" : Inv($"{laps} {Plural(laps, "lap")} with ICE and MGU-K power"),
            state == GuideState.Done ? "" : Inv($"Record {PowerLapsWanted}+ laps at this track: every lap with ICE and MGU-K power teaches the car model."),
            "Every energy figure is learned from these laps: what each mode gives, where the lap harvests, how the car accelerates. With a few laps it is rough.",
            GuideSession.Practice,
            Short: Inv($"{PowerLapsWanted}+ laps at this track"),
            Kind: GuideKind.PowerLaps,
            Progress: Math.Min(1, (double)laps / PowerLapsWanted),
            Count: Inv($"{Math.Min(laps, PowerLapsWanted)} / {PowerLapsWanted} LAPS"));
    }

    private static GuideItem RaceRun(LapProfile? race)
    {
        var laps = race?.LapCount ?? 0;
        var state = laps >= RaceLapsWanted ? GuideState.Done : laps >= RaceLapsMinimum ? GuideState.Partial : GuideState.Missing;
        return new GuideItem(
            "Race-pace run",
            state,
            laps == 0 ? "no typical lap with power data" : Inv($"{laps} typical {Plural(laps, "lap")} averaged"),
            state == GuideState.Done ? "" : Inv($"Run {RaceLapsWanted}+ laps in a row at race pace: no flashbacks, no pit or safety car laps."),
            "The race plan is built on the typical lap: several laps averaged, so one lap's traffic or mistake doesn't steer it.",
            GuideSession.Practice,
            Short: Inv($"{RaceLapsWanted} laps at race pace"),
            Kind: GuideKind.RaceRun,
            Progress: Math.Min(1, (double)laps / RaceLapsWanted),
            Count: Inv($"{Math.Min(laps, RaceLapsWanted)} / {RaceLapsWanted} LAPS"));
    }

    /// <summary>A mode's output by speed: the share of the lap's flat-out speed bands its curve has a point in.</summary>
    private static GuideItem ModeSpeeds(ErsCarModel model, int mode, HashSet<int> bands)
    {
        var name = DeployModes.Name(mode);
        var curve = model.Deploy.Curve(mode);
        var covered = curve is null ? 0 : bands.Count(b => curve.Points.Any(p => p.SpeedKmh == b));
        var share = bands.Count == 0 ? 0 : (double)covered / bands.Count;
        var state = share >= SpeedCoverage ? GuideState.Done : covered > 0 ? GuideState.Partial : GuideState.Missing;
        var range = curve is null || curve.Points.Count == 0
            ? ""
            : Inv($", {curve.Points.Min(p => p.SpeedKmh)}–{curve.Points.Max(p => p.SpeedKmh) + ErsModelBuilder.SpeedBand} km/h");
        return new GuideItem(
            Inv($"{name} by speed"),
            state,
            covered == 0 ? "never run flat out with charge" : Inv($"{covered} of {bands.Count} speed bands{range}"),
            state == GuideState.Done ? "" : Inv($"Run laps in {name} with the battery above 10 %: flat out on every straight, from the corner exits to top speed."),
            Inv($"The plan knows what {name} gives only at the speeds it was run at; between and beyond them it stretches the curve, so its gains there are guesses."),
            GuideSession.Practice,
            Short: Inv($"{name} · flat out, with charge"),
            Kind: GuideKind.ModeSpeeds,
            Progress: share,
            Count: Inv($"{covered} / {bands.Count} BANDS"),
            Mode: mode);
    }

    /// <summary>Each mode's own output, stretch by stretch: the game's energy map isn't the same on every straight.</summary>
    private static GuideItem ModeStraights(ErsCarModel model, List<PlanSegment> flat)
    {
        var shares = PlannedModes.Select(mode => flat.Count == 0 ? 0 : (double)flat.Count(s => model.Map.HasDeploy(mode, s.Index)) / flat.Count).ToArray();
        var learned = shares.Count(s => s >= StraightCoverage);
        var state = learned == PlannedModes.Length ? GuideState.Done : shares.Any(s => s > 0) ? GuideState.Partial : GuideState.Missing;
        var have = string.Join(", ", PlannedModes.Select((mode, i) => Inv($"{DeployModes.Name(mode)} {shares[i] * 100:0} %")));
        return new GuideItem(
            "Every mode on every straight",
            state,
            flat.Count == 0 ? "no lap to plan on yet" : have + " of the flat-out stretches",
            state == GuideState.Done ? "" : "Change the deploy mode lap by lap: a lap in None, one in Medium, one in Hotlap, one in Overtake, each with charge in the battery.",
            "The game deploys differently on each straight. Where a mode was run, the plan follows what it really gave there; elsewhere it uses the mode's speed curve.",
            GuideSession.Practice,
            Short: "A lap in each mode",
            Kind: GuideKind.ModeStraights,
            Progress: shares.Average(),
            Count: Inv($"{learned} / {PlannedModes.Length} MODES"));
    }

    /// <summary>Harvest off full throttle, learned only where the battery and the lap's limit had room.</summary>
    private static GuideItem BrakingHarvest(ErsCarModel model, LapProfile? race)
    {
        var braking = race?.Segments.Where(s => !s.FullThrottle && s.Harvest - s.Clip > HarvestingSegment).ToList() ?? [];
        var known = braking.Count(s => model.Map.Lift(s.Index) is not null);
        var share = braking.Count == 0 ? 0 : (double)known / braking.Count;
        var state = share >= HarvestCoverage ? GuideState.Done : known > 0 ? GuideState.Partial : GuideState.Missing;
        return new GuideItem(
            "Harvest under braking",
            state,
            braking.Count == 0 ? "no lap to plan on yet" : Inv($"learned on {share * 100:0} % of the braking and lifting"),
            state == GuideState.Done ? "" : "Reach the braking zones with room in the battery and before the lap's harvest limit: deploy hard (Hotlap or Overtake) on the straights before them.",
            "Harvest is learned only when nothing cuts it short: braking with a full battery, or after the lap's harvest limit, shows less than the car could take.",
            GuideSession.Practice,
            Short: "Brake with room in the battery",
            Kind: GuideKind.BrakingHarvest,
            Progress: share,
            Count: Inv($"{share * 100:0} / {HarvestCoverage * 100:0} %"));
    }

    /// <summary>A straight-line fit for each aero state the lap runs flat out on the straights.</summary>
    private static GuideItem AeroStates(ErsCarModel model, LapProfile? race)
    {
        var used = race?.Segments.Where(s => s.FullThrottle && SpeedKmh(race, s.Index) > StraightKmh).Select(s => s.Aero).ToHashSet() ?? [];
        if (used.Count == 0)
        {
            used = model.StraightLine.Select(f => f.Aero).ToHashSet();
        }

        var fitted = used.Count(aero => model.StraightLine.Any(f => f.Aero == aero));
        var state = used.Count > 0 && fitted == used.Count ? GuideState.Done : fitted > 0 ? GuideState.Partial : GuideState.Missing;
        var aeroName = model.Format == GameFormat.F1_26 ? "active aero" : "DRS";
        return new GuideItem(
            "Acceleration in every aero state",
            state,
            used.Count == 0 ? "no straight-line fit yet" : Inv($"{fitted} of {used.Count} {Plural(used.Count, "state")} fitted"),
            state == GuideState.Done ? "" : Inv($"Run full laps flat out on the straights without lifting, with {aeroName} wherever it is allowed."),
            "The plan turns energy into lap time with these fits. A state without its own fit is simulated with another one's drag, so time on those straights is off.",
            GuideSession.Practice,
            Short: Inv($"Flat out with and without {aeroName}"),
            Kind: GuideKind.AeroStates,
            Progress: used.Count == 0 ? 0 : (double)fitted / used.Count,
            Count: Inv($"{fitted} / {used.Count} STATES"));
    }

    /// <summary>The qualifying plan spends a full battery: the lap it is built on should start with one.</summary>
    private static GuideItem FullBattery(LapProfile? qualifying)
    {
        var share = qualifying is null ? 0 : Math.Clamp(qualifying.StartStore / LapSimulator.Capacity, 0, 1);
        var state = share >= FullShare ? GuideState.Done : share >= 0.6 ? GuideState.Partial : GuideState.Missing;
        return new GuideItem(
            "Push lap from a full battery",
            state,
            qualifying is null ? "no timed lap with power data" : Inv($"the fastest lap started with {share * 100:0} %"),
            state == GuideState.Done ? "" : "In qualifying, fill the battery on the out-lap (None, lift before the braking zones), then push a full lap.",
            "The qualifying plan spends a full battery. Built on a lap that started short, it has never seen the car with all its energy, so where to spend it is less sure.",
            GuideSession.Qualifying,
            Short: "Full battery, then push",
            Kind: GuideKind.FullBattery,
            Progress: Math.Min(1, share / FullShare),
            Count: Inv($"{share * 100:0} / {FullShare * 100:0} %"));
    }

    /// <summary>How close the model replays the typical lap with the modes as driven: the plan's confidence.</summary>
    private static GuideItem? Replay(ErsCarModel model, LapProfile? race)
    {
        if (race is null || LapSimulator.Create(race, model, LapSimulator.RaceReserve) is not { } simulator)
        {
            return null;
        }

        var check = ErsOptimizer.Check(simulator);
        var error = Math.Abs(check.Error);
        var state = check.IsTrusted ? GuideState.Done : GuideState.Partial;
        return new GuideItem(
            "The model replays your lap",
            state,
            Inv($"the typical lap replayed {error:0.00} s {(check.Error > 0 ? "slow" : "fast")}"),
            state == GuideState.Done ? "" : "Record more laps in the modes the plan uses, with the overtake button off on the laps you keep for the model.",
            Inv($"A plan's gains are only as good as the model's replay of a lap you drove: within {ModelCheck.TrustedWithin:0.0} s it is trusted."),
            GuideSession.Practice,
            Short: Inv($"Replay within {ModelCheck.TrustedWithin:0.0} s"),
            Kind: GuideKind.Replay,
            Progress: check.IsTrusted ? 1 : Math.Clamp(1 - (error - ModelCheck.TrustedWithin) / (3 * ModelCheck.TrustedWithin), 0, 0.9),
            Count: Inv($"±{error:0.00} S"));
    }

    private static double SpeedKmh(LapProfile lap, int segment) => (lap.Speed[segment] + lap.Speed[segment + 1]) / 2 * 3.6;

    private static string Plural(int count, string noun) => count == 1 ? noun : noun + "s";

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
