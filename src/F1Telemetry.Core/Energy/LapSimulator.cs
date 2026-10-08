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
public sealed record PlanSegment(int Index, double From, bool FullThrottle, int Aero, double IcePower, double MgukPower, double Harvest, double Deploy, int Mode, double ReferenceSeconds);

/// <summary>
/// A lap cut into <see cref="Step"/> m segments for the <see cref="LapSimulator"/>: speeds at every boundary, where it
/// was flat out, what it harvested where, and the speed cap ahead of every braking zone. Either one lap as driven
/// (<see cref="From"/>) or the average of several (<see cref="Average"/>).
/// </summary>
public sealed class LapProfile
{
    public const double Step = 25;

    private LapProfile(int lapNumber, int lapCount, IReadOnlyList<PlanSegment> segments, double[] speed, double[] level, double braking, double massKg,
        double actualSeconds)
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
            var full = inside.Count > 0 && inside.Count(s => s.Throttle >= 0.98 && s.Brake <= 0.05) * 2 > inside.Count;
            double harvest = 0, deploy = 0;
            var at = samples.FindIndex(s => s.LapDistance >= from);
            for (var k = Math.Max(1, at); k < samples.Count && samples[k].LapDistance < from + Step; k++)
            {
                harvest += CounterStep(samples[k].ErsHarvestedMguk - samples[k - 1].ErsHarvestedMguk);
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
                ReferenceSeconds: 2 * Step / (speed[i] + speed[i + 1])));
        }

        // The braking capability the lap showed: 80th percentile of its deceleration under braking.
        var decelerations = samples.Where(s => s.Brake > 0.5).Select(s => -s.GForceLon * 9.81).Where(a => a > 0).Order().ToList();
        var braking = Math.Clamp(decelerations.Count > 10 ? decelerations[(int)(decelerations.Count * 0.8)] : 35, 15, 60);

        static double CounterStep(double step) => step is >= 0 and <= 200_000 ? step : 0; // resets and flashbacks aren't energy

        var fuel = samples.Average(s => s.FuelInTank);
        return new LapProfile(samples[0].LapNumber, 1, segments, speed, level, braking, ErsModelBuilder.CarMass(format) + fuel,
            lapSeconds is > 0 ? lapSeconds.Value : segments.Sum(s => s.ReferenceSeconds));
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
                ReferenceSeconds: 2 * Step / (speed[i] + speed[i + 1])));
        }

        return new LapProfile(0, laps.Count, segments, speed, level, laps.Average(l => l.Braking), laps.Average(l => l.MassKg),
            laps.Average(l => l.ActualSeconds));
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
/// MGU-K output of the chosen mode (<see cref="DeployMap"/>), against the fitted drag (<see cref="StraightLineFit"/>),
/// never faster than it can still brake for the next corner. The battery takes the reference's harvest (losing what
/// doesn't fit in it) and pays for the deployment, which stops at <see cref="Reserve"/>. Off the flat-out stretches
/// it pays what the reference deployed there.</para>
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

    private LapSimulator(LapProfile lap, ErsCarModel model, double reserve, StraightLineFit fallbackFit)
    {
        Lap = lap;
        Model = model;
        Reserve = reserve;
        _fits = model.StraightLine.ToDictionary(f => f.Aero);
        _fallbackFit = fallbackFit;
        _residual = new double[lap.Segments.Count];
        for (var i = 0; i < lap.Segments.Count; i++)
        {
            var segment = lap.Segments[i];
            if (!segment.FullThrottle)
            {
                continue;
            }

            var (v0, v1) = (lap.Speed[i], lap.Speed[i + 1]);
            var actual = (v1 * v1 - v0 * v0) / (2 * LapProfile.Step);
            var fitted = Fit(segment).Acceleration(segment.IcePower + segment.MgukPower, v0, lap.MassKg);
            _residual[i] = Math.Clamp(actual - fitted, -MaxResidual, MaxResidual);
        }
    }

    private StraightLineFit Fit(PlanSegment segment) => _fits.GetValueOrDefault(segment.Aero, _fallbackFit);

    public LapProfile Lap { get; }
    public ErsCarModel Model { get; }

    /// <summary>Charge kept back for fights, J: deployment stops there.</summary>
    public double Reserve { get; }

    public const double Capacity = EnergyAnalyzer.Capacity;

    /// <summary>Null when the model has no straight-line fit or deploy map to simulate with.</summary>
    public static LapSimulator? Create(LapProfile lap, ErsCarModel model, double reserve = 0) =>
        model.StraightLine.Count == 0 || model.Deploy.Curves.Count == 0
            ? null
            : new LapSimulator(lap, model, reserve, model.StraightLine.MaxBy(f => f.Samples)!);

    /// <summary>
    /// Runs segments <paramref name="from"/> to <paramref name="to"/> (exclusive) from <paramref name="store"/> J, the
    /// flat-out ones within [<paramref name="from"/>, <paramref name="deployEnd"/>) with <paramref name="option"/>.
    /// Fills <paramref name="modes"/> and <paramref name="stores"/> per segment when given.
    /// </summary>
    public StretchResult Run(int from, int deployEnd, int to, DeployOption option, double store, int[]? modes = null, double[]? stores = null)
    {
        var segments = Lap.Segments;
        double time = 0, deployed = 0;
        var v = Lap.Speed[from];
        var switched = false;
        var zoneLength = Math.Max(1, deployEnd - from);
        for (var i = from; i < to; i++)
        {
            var segment = segments[i];
            store = Math.Min(Capacity, store + segment.Harvest);
            if (i >= deployEnd || !segment.FullThrottle)
            {
                // Not flat out: the reference speed, time and part-throttle deployment.
                var paid = Math.Min(Math.Max(0, store - Reserve), segment.Deploy);
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
                switched |= option.UntilKmh is { } limit && kmh >= limit;
                switched |= option.UntilFraction is { } fraction && (i - from) >= fraction * zoneLength;
                mode = switched ? option.Then : option.First;
            }

            var power = option == DeployOption.AsDriven ? segment.MgukPower
                : mode == DeployModes.None ? 0
                : Model.Deploy.Curve(mode)?.PowerAt(kmh) ?? 0;
            var available = Math.Max(0, store - Reserve);
            var estimate = LapProfile.Step / v;
            if (power * estimate > available)
            {
                power = available / estimate;
            }

            var a = Fit(segment).Acceleration(segment.IcePower + power, v, Lap.MassKg) + _residual[i];
            var next = Math.Sqrt(Math.Max(25, v * v + 2 * a * LapProfile.Step));
            next = Math.Min(next, Lap.Envelope[i + 1]);
            var dt = 2 * LapProfile.Step / (v + next);
            var energy = Math.Min(available, power * dt);
            store -= energy;
            deployed += energy;
            time += dt;
            v = next;
            Record(i, mode, store);
        }

        return new StretchResult(time, store, deployed);

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
public readonly record struct StretchResult(double Seconds, double ExitStore, double Deployed);
