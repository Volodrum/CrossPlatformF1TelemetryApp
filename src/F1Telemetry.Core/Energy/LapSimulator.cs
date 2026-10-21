using F1Telemetry.Core.Models;
using F1Telemetry.Protocol;

namespace F1Telemetry.Core.Energy;

/// <summary>A 25 m stretch of the reference lap.</summary>
/// <param name="FullThrottle">Flat out on the reference lap: a stretch where the deploy mode changes the speed.</param>
/// <param name="Harvest">Energy the MGU-K harvested here on the reference lap, J.</param>
/// <param name="Deploy">Energy the MGU-K deployed here on the reference lap, J (it deploys at part throttle too).</param>
/// <param name="MgukPower">MGU-K output here on the reference lap, W.</param>
/// <param name="Mode">Deploy mode the reference lap ran here.</param>
/// <param name="ReferenceSeconds">Time the reference lap took here.</param>
/// <param name="Override">The 2026 overtake override was on here: the MGU-K gave more than its mode does on its own.</param>
/// <param name="Empty">
/// The battery was (nearly) empty here, on the lap or on one of the laps averaged: it paid what it had, not what the mode
/// asks.
/// </param>
/// <param name="LateralG">Mean lateral load here, g (unsigned).</param>
/// <param name="Clip">The part of <paramref name="Harvest"/> taken flat out (the game's clipping), J; the rest was braking or lifting.</param>
public sealed record PlanSegment(int Index, double From, bool FullThrottle, int Aero, double IcePower, double MgukPower, double Harvest, double Deploy, int Mode,
    double ReferenceSeconds, bool Override = false, bool Empty = false, double LateralG = 0, double Clip = 0);

/// <summary>
/// A lap cut into <see cref="Step"/> m segments for the <see cref="LapSimulator"/>: speeds at every boundary, where it
/// was flat out, what it harvested where, and the speed cap ahead of every braking zone. Either one lap as driven
/// (<see cref="From"/>) or the average of several (<see cref="Average"/>).
/// </summary>
public sealed class LapProfile
{
    public const double Step = 25;

    /// <summary>Below this the battery counts as empty, J: it can't give a mode all it asks.</summary>
    public const double NearlyEmpty = 150_000;

    private LapProfile(int lapNumber, int lapCount, IReadOnlyList<PlanSegment> segments, double[] speed, double[] level, double braking, double massKg,
        double actualSeconds, double harvestLimit)
    {
        LapNumber = lapNumber;
        LapCount = lapCount;
        Segments = segments;
        Speed = speed;
        Level = level;
        Braking = braking;
        Envelope = BrakingEnvelope(segments, speed, braking);
        MassKg = massKg;
        ActualSeconds = actualSeconds;
        CoveredSeconds = segments.Sum(s => s.ReferenceSeconds);
        StartStore = level[0];
        EndStore = level[^1];
        HarvestLimit = harvestLimit;
        Harvested = new double[segments.Count + 1];
        for (var i = 0; i < segments.Count; i++)
        {
            Harvested[i + 1] = Harvested[i] + segments[i].Harvest;
        }
    }

    /// <summary>The lap's number; 0 for an average of several laps.</summary>
    public int LapNumber { get; }

    /// <summary>How many laps the profile stands for (1 for a single lap).</summary>
    public int LapCount { get; }

    public IReadOnlyList<PlanSegment> Segments { get; }

    /// <summary>Reference speed at each segment boundary, m/s (one more than the segments).</summary>
    public double[] Speed { get; }

    /// <summary>Battery at each segment boundary, J.</summary>
    public double[] Level { get; }

    /// <summary>The braking capability the lap showed, m/s².</summary>
    public double Braking { get; }

    /// <summary>Highest speed at each boundary that still makes the next braking zone, m/s.</summary>
    public double[] Envelope { get; }

    public double MassKg { get; }

    /// <summary>The lap's real time, s.</summary>
    public double ActualSeconds { get; }

    /// <summary>The time the reference took over the segments (the lap less the few metres past the last full segment), s.</summary>
    public double CoveredSeconds { get; }

    /// <summary>Battery at the first and last segment boundary, J.</summary>
    public double StartStore { get; }

    public double EndStore { get; }

    /// <summary>The lap's MGU-K harvest limit, J; 0 when the game doesn't report one (2025 format).</summary>
    public double HarvestLimit { get; }

    /// <summary>What the MGU-K had harvested on the lap at each segment boundary, J (one more than the segments).</summary>
    public double[] Harvested { get; }

    public double Length => Segments.Count * Step;

    /// <summary>
    /// Cuts a lap into segments. Null when it can't be simulated: too short, or recorded before ICE power was.
    /// </summary>
    public static LapProfile? From(IReadOnlyList<TelemetrySample> lap, GameFormat format, double? lapSeconds = null)
    {
        var samples = lap.Where(s => s.LapDistance >= 0).ToList();
        if (samples.Count < 50 || samples.All(s => s.EnginePowerIce <= 0))
        {
            return null;
        }

        var length = samples[^1].LapDistance;
        var count = (int)Math.Floor(length / Step);
        if (count < 20)
        {
            return null;
        }

        // Speed, session time and battery level at the boundaries, interpolated by distance.
        var speed = new double[count + 1];
        var time = new double[count + 1];
        var level = new double[count + 1];
        var j = 0;
        for (var i = 0; i <= count; i++)
        {
            var d = i * Step;
            while (j < samples.Count - 2 && samples[j + 1].LapDistance < d)
            {
                j++;
            }

            var (a, b) = (samples[j], samples[j + 1]);
            var f = b.LapDistance > a.LapDistance ? Math.Clamp((d - a.LapDistance) / (b.LapDistance - a.LapDistance), 0, 1) : 0;
            speed[i] = Math.Max(5, (a.Speed + f * (b.Speed - a.Speed)) / 3.6);
            time[i] = a.SessionTime + f * (b.SessionTime - a.SessionTime);
            level[i] = a.ErsStoreEnergy + f * (b.ErsStoreEnergy - a.ErsStoreEnergy);
        }

        // The game measures lap distance along the track's centre line, a little longer than the line the car drives:
        // scale the speeds so the segments take the time they really took.
        var covered = time[count] - time[0];
        var bySpeed = Enumerable.Range(0, count).Sum(i => 2 * Step / (speed[i] + speed[i + 1]));
        if (covered > 0)
        {
            var scale = bySpeed / covered;
            for (var i = 0; i <= count; i++)
            {
                speed[i] *= scale;
            }
        }

        var segments = new List<PlanSegment>(count);
        for (var i = 0; i < count; i++)
        {
            var from = i * Step;
            var inside = samples.Where(s => s.LapDistance >= from && s.LapDistance < from + Step).ToList();
            var full = inside.Count > 0 && inside.Count(Flat) * 2 > inside.Count;
            double harvest = 0, deploy = 0, clip = 0;
            var at = samples.FindIndex(s => s.LapDistance >= from);
            for (var k = Math.Max(1, at); k < samples.Count && samples[k].LapDistance < from + Step; k++)
            {
                var harvested = CounterStep(samples[k].ErsHarvestedMguk - samples[k - 1].ErsHarvestedMguk);
                harvest += harvested;
                clip += Flat(samples[k]) ? harvested : 0;
                deploy += CounterStep(samples[k].ErsDeployed - samples[k - 1].ErsDeployed);
            }

            segments.Add(new PlanSegment(
                i, from, full,
                Aero: inside.Count == 0 ? 0 : inside.GroupBy(s => format == GameFormat.F1_26 ? s.ActiveAeroMode : s.Drs).MaxBy(g => g.Count())!.Key,
                IcePower: inside.Count == 0 ? 0 : inside.Average(s => s.EnginePowerIce),
                MgukPower: inside.Count == 0 ? 0 : inside.Average(s => s.EnginePowerMguk),
                Harvest: harvest,
                Deploy: deploy,
                Mode: inside.Count == 0 ? 0 : inside.GroupBy(s => s.ErsDeployMode).MaxBy(g => g.Count())!.Key,
                ReferenceSeconds: 2 * Step / (speed[i] + speed[i + 1]),
                Override: inside.Count(s => s.OvertakeActive != 0) * 2 > inside.Count,
                Empty: Math.Min(level[i], level[i + 1]) <= NearlyEmpty,
                LateralG: inside.Count == 0 ? 0 : inside.Average(s => Math.Abs(s.GForceLat)),
                Clip: clip));
        }

        // The braking capability the lap showed: 80th percentile of its deceleration under braking.
        var decelerations = samples.Where(s => s.Brake > 0.5).Select(s => -s.GForceLon * 9.81).Where(a => a > 0).Order().ToList();
        var braking = Math.Clamp(decelerations.Count > 10 ? decelerations[(int)(decelerations.Count * 0.8)] : 35, 15, 60);

        static double CounterStep(double step) => step is >= 0 and <= 200_000 ? step : 0; // resets and flashbacks aren't energy
        static bool Flat(TelemetrySample s) => s.Throttle >= 0.98 && s.Brake <= 0.05;

        var fuel = samples.Average(s => s.FuelInTank);
        return new LapProfile(samples[0].LapNumber, 1, segments, speed, level, braking, ErsModelBuilder.CarMass(format) + fuel,
            lapSeconds is > 0 ? lapSeconds.Value : segments.Sum(s => s.ReferenceSeconds), samples.Max(s => s.ErsHarvestLimit));
    }

    /// <summary>
    /// The typical lap of a session: speeds, power, harvest, deployment and battery level averaged segment by segment,
    /// flat-out stretches, aero and deploy mode by majority. A race plan built on it holds for every lap of the stint,
    /// not just the one that happened to be fastest. Null for no laps.
    /// </summary>
    public static LapProfile? Average(IReadOnlyList<LapProfile> laps)
    {
        if (laps.Count == 0)
        {
            return null;
        }

        if (laps.Count == 1)
        {
            return laps[0];
        }

        var count = laps.Min(l => l.Segments.Count);
        var speed = new double[count + 1];
        var level = new double[count + 1];
        for (var i = 0; i <= count; i++)
        {
            speed[i] = laps.Average(l => l.Speed[i]);
            level[i] = laps.Average(l => l.Level[i]);
        }

        var segments = new List<PlanSegment>(count);
        for (var i = 0; i < count; i++)
        {
            var at = laps.Select(l => l.Segments[i]).ToList();
            segments.Add(new PlanSegment(
                i, i * Step,
                FullThrottle: at.Count(s => s.FullThrottle) * 2 > at.Count,
                Aero: at.GroupBy(s => s.Aero).MaxBy(g => g.Count())!.Key,
                IcePower: at.Average(s => s.IcePower),
                MgukPower: at.Average(s => s.MgukPower),
                Harvest: at.Average(s => s.Harvest),
                Deploy: at.Average(s => s.Deploy),
                Mode: at.GroupBy(s => s.Mode).MaxBy(g => g.Count())!.Key,
                ReferenceSeconds: 2 * Step / (speed[i] + speed[i + 1]),
                Override: at.Count(s => s.Override) * 2 > at.Count,
                Empty: at.Any(s => s.Empty),
                LateralG: at.Average(s => s.LateralG),
                Clip: at.Average(s => s.Clip)));
        }

        return new LapProfile(0, laps.Count, segments, speed, level, laps.Average(l => l.Braking), laps.Average(l => l.MassKg),
            laps.Average(l => l.ActualSeconds), laps.Average(l => l.HarvestLimit));
    }

    /// <summary>Highest speed at each boundary that still makes the next braking zone, braking at <paramref name="braking"/> m/s².</summary>
    private static double[] BrakingEnvelope(IReadOnlyList<PlanSegment> segments, double[] speed, double braking)
    {
        var count = segments.Count;
        var envelope = new double[count + 1];
        for (var i = 0; i <= count; i++)
        {
            envelope[i] = i < count && segments[i].FullThrottle ? double.PositiveInfinity : speed[i];
        }

        for (var i = count - 1; i >= 0; i--)
        {
            envelope[i] = Math.Max(speed[i], Math.Min(envelope[i], Math.Sqrt(envelope[i + 1] * envelope[i + 1] + 2 * braking * Step)));
        }

        return envelope;
    }
}

/// <summary>
/// How to run one deploy zone: <see cref="First"/>, then <see cref="Then"/> from <see cref="UntilKmh"/> or after
/// <see cref="UntilFraction"/> of the zone (a single mode when both are null). At most one switch, so it can be driven.
/// </summary>
public sealed record DeployOption(int First, int Then, int? UntilKmh = null, double? UntilFraction = null)
{
    /// <summary>The reference lap as driven: its MGU-K output, segment by segment (the baseline plans are measured against).</summary>
    public static DeployOption AsDriven { get; } = new(-1, -1);

    /// <summary>The reference lap's modes with the <see cref="DeployMap"/>'s output for them (the model check).</summary>
    public static DeployOption AsDrivenByMap { get; } = new(-2, -2);

    public bool IsAsDriven => First < 0;

    public string Describe()
    {
        var first = DeployModes.Name(First);
        if (UntilKmh is { } kmh)
        {
            return $"{first} to {kmh} km/h, then {DeployModes.Name(Then)}";
        }

        if (UntilFraction is { } fraction)
        {
            var part = fraction < 0.5 ? "first third" : "first two thirds";
            return $"{first} for the {part}, then {DeployModes.Name(Then)}";
        }

        return first;
    }
}

/// <summary>
/// Simulates stretches of a <see cref="LapProfile"/> with other deploy modes, on an <see cref="ErsCarModel"/>.
/// <para>Corners and braking zones keep the reference speeds: grip limits them, not power. On the flat-out stretches
/// the car accelerates from the reference speed at the zone's start with the ICE output of the reference plus the
/// MGU-K output of the chosen mode, less what the game harvests there flat out (clipping takes it from the wheels),
/// against the fitted drag (<see cref="StraightLineFit"/>), never faster than it can still brake for the next corner.
/// What a mode gives, draws and clips at each point comes from the <see cref="PositionMap"/> where the mode was run
/// there (the game runs its own energy map under the mode), else from the speed curve (<see cref="DeployMap"/>). Only
/// the share of a change of power the tyres let through at that speed and load (<see cref="TractionMap"/>) changes the
/// acceleration.</para>
/// <para>The battery pays what the mode draws by the game's deploy counter, more than the MGU-K gives; deployment stops
/// at <see cref="Reserve"/>. Off full throttle it takes the reference's harvest, or what the map says the MGU-K takes
/// there where the reference had already reached the lap's harvest limit; flat out it takes the chosen mode's clipping.
/// All harvest stops at the lap's harvest limit and at a full battery, so a plan that clips less, or reaches the limit
/// later, gets the energy it really would. The reference as driven pays and harvests what its counters did.</para>
/// <para>What the modes cost on this lap is calibrated against the reference: <see cref="DrawScale"/> makes its own
/// modes, at its own speeds, cost what its deploy counter paid on the flat-out stretches where the battery had charge
/// to give and the overtake override was off. A plan never counts on the override; the model check replays the
/// reference's override stretches as driven.</para>
/// <para>The fit is an average over many laps; gear shifts, traction and the tow make every stretch differ from it. Each
/// flat-out segment keeps the difference between the reference's acceleration and the fit's at the reference's power,
/// so the reference replays as driven and the fit only decides what a change of MGU-K output does.</para>
/// </summary>
public sealed class LapSimulator
{
    private readonly Dictionary<int, StraightLineFit> _fits;
    private readonly StraightLineFit _fallbackFit;

    // Larger differences are glitches (a lift on the straight, a missed sample), not the car.
    private const double MaxResidual = 15;

    private readonly double[] _residual;

    // What the reference harvested flat out in each segment, W: its clipping.
    private readonly double[] _clip;

    // The calibration stays within this of the map: beyond it the reference is too unlike the laps the map was learned on.
    private const double MaxDrawCorrection = 0.25;

    // Within this of the harvest limit the reference counts as having reached it, J.
    private const double LimitMargin = 100_000;

    private LapSimulator(LapProfile lap, ErsCarModel model, double reserve, StraightLineFit fallbackFit)
    {
        Lap = lap;
        Model = model;
        Reserve = reserve;
        _fits = model.StraightLine.ToDictionary(f => f.Aero);
        _fallbackFit = fallbackFit;
        _residual = new double[lap.Segments.Count];
        _clip = new double[lap.Segments.Count];
        for (var i = 0; i < lap.Segments.Count; i++)
        {
            var segment = lap.Segments[i];
            if (!segment.FullThrottle)
            {
                continue;
            }

            _clip[i] = segment.ReferenceSeconds > 0 ? segment.Clip / segment.ReferenceSeconds : 0;
            var (v0, v1) = (lap.Speed[i], lap.Speed[i + 1]);
            var actual = (v1 * v1 - v0 * v0) / (2 * LapProfile.Step);
            var fitted = Fit(segment).Acceleration(segment.IcePower + segment.MgukPower - _clip[i], v0, lap.MassKg);
            _residual[i] = Math.Clamp(actual - fitted, -MaxResidual, MaxResidual);
        }

        double paid = 0, byMap = 0;
        for (var i = 0; i < lap.Segments.Count; i++)
        {
            var segment = lap.Segments[i];
            // Where the battery was (nearly) empty it paid what it had, and under the override more: not what the mode asks.
            if (segment.FullThrottle && !segment.Override && !segment.Empty)
            {
                paid += segment.Deploy;
                byMap += Deploy(segment.Mode, i, (lap.Speed[i] + lap.Speed[i + 1]) / 2 * 3.6).Draw * segment.ReferenceSeconds;
            }
        }

        DrawScale = paid > 0 && byMap > 0 ? Math.Clamp(paid / byMap, 1 - MaxDrawCorrection, 1 + MaxDrawCorrection) : 1;
    }

    /// <summary>
    /// What the modes cost on this lap against the deploy map: the reference's flat-out deployment by its deploy counter
    /// over what the map says its modes cost at its speeds (1 when the map is right for this lap).
    /// </summary>
    public double DrawScale { get; }

    private StraightLineFit Fit(PlanSegment segment) => _fits.GetValueOrDefault(segment.Aero, _fallbackFit);

    public LapProfile Lap { get; }
    public ErsCarModel Model { get; }

    /// <summary>Charge kept back, J: deployment stops there (not for the reference's own modes, which are what happened).</summary>
    public double Reserve { get; }

    /// <summary>
    /// The charge a race plan never goes below, J: room for the laps not to be driven exactly as planned (a mode
    /// switched late, traffic, a different line), so the battery doesn't run flat. A qualifying lap spends it all.
    /// </summary>
    public const double RaceReserve = 400_000;

    public const double Capacity = EnergyAnalyzer.Capacity;

    /// <summary>Null when the model has no straight-line fit or deploy map to simulate with.</summary>
    public static LapSimulator? Create(LapProfile lap, ErsCarModel model, double reserve = 0) =>
        model.StraightLine.Count == 0 || model.Deploy.Curves.Count == 0
            ? null
            : new LapSimulator(lap, model, reserve, model.StraightLine.MaxBy(f => f.Samples)!);

    /// <summary>MGU-K output and battery draw (before <see cref="DrawScale"/>) of <paramref name="mode"/> flat out at segment <paramref name="i"/>, W.</summary>
    private (double Output, double Draw) Deploy(int mode, int i, double kmh) => Model.Map.Deploy(mode, i, kmh, Model.Deploy.Curve(mode)) ?? (0, 0);

    /// <summary>
    /// What the game harvests flat out at segment <paramref name="i"/> in <paramref name="mode"/>, W. In the reference's
    /// own mode, what the reference clipped; in another, that moved by what the map says the other mode clips there more
    /// or less (the lap's own conditions stay, as with the acceleration), or the map's alone. Nothing where the mode
    /// was never run there.
    /// </summary>
    private double Clip(int mode, int i)
    {
        var own = Lap.Segments[i].Mode;
        if (mode == own)
        {
            return _clip[i];
        }

        return Model.Map.Clip(mode, i) is not { } other ? 0
            : Model.Map.Clip(own, i) is { } mine ? Math.Max(0, _clip[i] + other - mine)
            : other;
    }

    /// <summary>
    /// What segment <paramref name="i"/> can harvest off full throttle, J: the reference's, or what the map says the
    /// MGU-K takes there where the reference had already reached the harvest limit (it took nothing because it couldn't).
    /// </summary>
    private double Lift(int i)
    {
        var segment = Lap.Segments[i];
        var capped = Lap.HarvestLimit > 0 && Lap.Harvested[i + 1] >= Lap.HarvestLimit - LimitMargin;
        return capped && Model.Map.Lift(i) is { } watts ? Math.Max(segment.Harvest, watts * segment.ReferenceSeconds) : segment.Harvest;
    }

    /// <summary>
    /// Runs segments <paramref name="from"/> to <paramref name="to"/> (exclusive) from <paramref name="store"/> J with
    /// <paramref name="harvested"/> J harvested on the lap so far, the flat-out ones within
    /// [<paramref name="from"/>, <paramref name="deployEnd"/>) with <paramref name="option"/>. Fills
    /// <paramref name="modes"/> and <paramref name="stores"/> per segment when given.
    /// </summary>
    public StretchResult Run(int from, int deployEnd, int to, DeployOption option, double store, int[]? modes = null, double[]? stores = null, double harvested = 0)
    {
        var segments = Lap.Segments;
        double time = 0, deployed = 0;
        var v = Lap.Speed[from];
        var switched = false;
        var reserve = option.IsAsDriven ? 0 : Reserve; // the reference as driven, and its modes, are what happened
        var limit = Lap.HarvestLimit > 0 ? Lap.HarvestLimit : double.PositiveInfinity;
        var zoneLength = Math.Max(1, deployEnd - from);
        for (var i = from; i < to; i++)
        {
            var segment = segments[i];
            if (i >= deployEnd || !segment.FullThrottle)
            {
                // Not flat out: the reference speed, time and part-throttle deployment.
                Take(option == DeployOption.AsDriven ? segment.Harvest : Lift(i));
                var paid = Math.Min(Math.Max(0, store - reserve), segment.Deploy);
                store -= paid;
                deployed += paid;
                time += segment.ReferenceSeconds;
                v = Lap.Speed[i + 1];
                Record(i, segment.Mode, store);
                continue;
            }

            var kmh = v * 3.6;
            int mode;
            if (option.IsAsDriven)
            {
                mode = segment.Mode;
            }
            else
            {
                switched |= option.UntilKmh is { } limitKmh && kmh >= limitKmh;
                switched |= option.UntilFraction is { } fraction && (i - from) >= fraction * zoneLength;
                mode = switched ? option.Then : option.First;
            }

            // What the reference harvested here off full throttle (braking into the next corner) any mode harvests.
            Take(segment.Harvest - segment.Clip);

            // The mode's output at the segment's mean speed: guessed from the reference's speed-up first, then from the
            // car's own. Out of a slow corner the output climbs steeply, and the entry speed alone undercounts it.
            var asDriven = option == DeployOption.AsDriven || (option == DeployOption.AsDrivenByMap && segment.Override);
            var reference = segment.MgukPower - _clip[i];
            var room = Math.Max(0, Math.Min(limit - harvested, Capacity - store));
            var next = Lap.Speed[i + 1] - Lap.Speed[i] + v;
            double power = 0, draw = 0, clip = 0;
            for (var pass = 0; pass < 2; pass++)
            {
                var mean = (v + next) / 2 * 3.6;
                // The reference's own modes under the override did what the override does: the map doesn't know it.
                if (asDriven)
                {
                    (power, draw, clip) = (segment.MgukPower, segment.Deploy / segment.ReferenceSeconds, _clip[i]);
                }
                else
                {
                    (power, draw) = Deploy(mode, i, mean);
                    (draw, clip) = (draw * DrawScale, Clip(mode, i));
                }

                // Clipping first, and only into a battery and a lap limit with room for it.
                var estimate = 2 * LapProfile.Step / (v + next);
                clip = Math.Min(clip, room / estimate);
                var available = Math.Max(0, store + clip * estimate - reserve);
                if (draw * estimate > available)
                {
                    // Not enough charge for the whole segment: the output drops with what the battery can give.
                    var share = available / (draw * estimate);
                    (power, draw) = (power * share, draw * share);
                }

                // Only the share of a change of power the tyres let through changes the acceleration.
                var grip = asDriven ? 1 : Model.Grip.Factor(mean, segment.LateralG);
                var wheels = segment.IcePower + reference + grip * (power - clip - reference);
                var a = Fit(segment).Acceleration(wheels, v, Lap.MassKg) + _residual[i];
                next = Math.Min(Math.Sqrt(Math.Max(25, v * v + 2 * a * LapProfile.Step)), Lap.Envelope[i + 1]);
            }

            var dt = 2 * LapProfile.Step / (v + next);
            Take(clip * dt);
            var energy = Math.Min(Math.Max(0, store - reserve), draw * dt);
            store -= energy;
            deployed += energy;
            time += dt;
            v = next;
            Record(i, mode, store);
        }

        return new StretchResult(time, store, deployed, harvested);

        // Harvest stops at the lap's harvest limit and at a full battery.
        void Take(double joules)
        {
            var taken = Math.Clamp(joules, 0, Math.Max(0, Math.Min(limit - harvested, Capacity - store)));
            store += taken;
            harvested += taken;
        }

        void Record(int index, int mode, double level)
        {
            if (modes is not null)
            {
                modes[index] = mode;
            }

            if (stores is not null)
            {
                stores[index] = level;
            }
        }
    }
}

/// <param name="ExitStore">Battery at the end of the stretch, J.</param>
/// <param name="ExitHarvested">What the lap has harvested at the end of the stretch, J.</param>
public readonly record struct StretchResult(double Seconds, double ExitStore, double Deployed, double ExitHarvested = 0);
