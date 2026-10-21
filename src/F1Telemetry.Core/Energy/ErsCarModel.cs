using F1Telemetry.Protocol;

namespace F1Telemetry.Core.Energy;

/// <summary>MGU-K output in one deploy mode over a 10 km/h band starting at <paramref name="SpeedKmh"/>: the median of
/// <paramref name="Samples"/> full-throttle samples.</summary>
/// <param name="DrawW">
/// What the mode takes from the battery there, W, from the game's deploy counter: about a tenth more than the MGU-K
/// gives. Null when unknown: the output is used.
/// </param>
public sealed record DeployPoint(int SpeedKmh, double PowerW, int Samples, double? DrawW = null)
{
    public double Draw => DrawW ?? PowerW;
}

/// <summary>
/// MGU-K output by speed in one deploy mode, and the speed from which it fades (null when it doesn't).
/// </summary>
public sealed record DeployCurve(int Mode, IReadOnlyList<DeployPoint> Points, int? FadeFromKmh)
{
    public double PeakW => Points.Count == 0 ? 0 : Points.Max(p => p.PowerW);

    /// <summary>
    /// Output at <paramref name="kmh"/>: between the middles of the two bands either side, in a straight line (out of a
    /// slow corner the output climbs steeply with speed), or the nearest band past the ends of the curve.
    /// </summary>
    public double? PowerAt(double kmh) => At(kmh, p => p.PowerW);

    /// <summary>What the mode takes from the battery at <paramref name="kmh"/>, W (see <see cref="DeployPoint.DrawW"/>).</summary>
    public double? DrawAt(double kmh) => At(kmh, p => p.Draw);

    private double? At(double kmh, Func<DeployPoint, double> value)
    {
        if (Points.Count == 0)
        {
            return null;
        }

        static double Middle(DeployPoint p) => p.SpeedKmh + ErsModelBuilder.SpeedBand / 2.0;
        var above = Points.FirstOrDefault(p => Middle(p) >= kmh);
        var below = Points.LastOrDefault(p => Middle(p) < kmh);
        if (above is null || below is null)
        {
            return value(above ?? below!);
        }

        // Bands apart by more than one step have no data between them: take the nearer.
        if (above.SpeedKmh - below.SpeedKmh > ErsModelBuilder.SpeedBand)
        {
            return value(Middle(above) - kmh < kmh - Middle(below) ? above : below);
        }

        var f = (kmh - Middle(below)) / (Middle(above) - Middle(below));
        return value(below) + f * (value(above) - value(below));
    }
}

/// <summary>
/// What each deploy mode delivers and what it costs the battery, learned from full-throttle samples with charge in the
/// battery and the overtake override off (a plan can't count on a car ahead). None has a curve too when it deploys:
/// flat out at high speed the game still spends charge in None.
/// </summary>
public sealed record DeployMap(IReadOnlyList<DeployCurve> Curves)
{
    public static DeployMap Empty { get; } = new([]);

    public DeployCurve? Curve(int mode) => Curves.FirstOrDefault(c => c.Mode == mode);

    public int? FadeSpeed(int mode) => Curve(mode)?.FadeFromKmh;
}

/// <summary>
/// Straight-line acceleration at full throttle, fitted by least squares:
/// <c>a = efficiency · P / (m · v) − drag · v² / m − resistance</c>, with P the ICE plus MGU-K output and m the car
/// (<see cref="ErsModelBuilder.CarMass"/>) plus fuel.
/// </summary>
/// <param name="Aero">Active aero mode (2026) or DRS open (2025) the fit is for.</param>
/// <param name="Drag">½ ρ C<sub>d</sub>A, kg/m.</param>
/// <param name="Resistance">Everything that doesn't grow with speed squared, m/s².</param>
/// <param name="RmsError">Typical error of a predicted acceleration, m/s².</param>
public sealed record StraightLineFit(int Aero, double Efficiency, double Drag, double Resistance, double RSquared, double RmsError, int Samples)
{
    private const double AirDensity = 1.225;

    /// <summary>C<sub>d</sub>A, m².</summary>
    public double DragArea => 2 * Drag / AirDensity;

    public double Acceleration(double powerW, double speedMs, double massKg) =>
        Efficiency * powerW / (massKg * speedMs) - Drag * speedMs * speedMs / massKg - Resistance;
}

/// <summary>Energy harvested and deployed in a 50 m stretch of the lap, averaged over <paramref name="Laps"/> laps.</summary>
public sealed record EnergyBin(double From, double HarvestedJ, double DeployedJ, int Laps);

/// <summary>
/// The car's ERS as learned from every lap recorded at a track: what each deploy mode delivers by speed and at every
/// point of the lap, where on the lap energy is harvested and deployed, how the car accelerates on the straights, and
/// how much of a change of power the tyres let through.
/// </summary>
/// <param name="Positions">The game's deploy and harvest at every point of the lap, by mode; null or empty: the speed curves only.</param>
/// <param name="Traction">Share of a change of power that becomes acceleration; null: all of it.</param>
public sealed record ErsCarModel(
    GameFormat Format,
    int TrackId,
    int Laps,
    int PowerLaps,
    DeployMap Deploy,
    IReadOnlyList<StraightLineFit> StraightLine,
    IReadOnlyList<EnergyBin> AlongLap,
    PositionMap? Positions = null,
    TractionMap? Traction = null)
{
    /// <summary>Laps that recorded ICE and MGU-K output (from v0.3, or re-imported): the deploy map and fits need them.</summary>
    public bool HasPowerData => PowerLaps > 0;

    public PositionMap Map => Positions ?? PositionMap.Empty;

    public TractionMap Grip => Traction ?? TractionMap.Unlimited;
}

/// <summary>Learns an <see cref="ErsCarModel"/> from a track's <see cref="ModelSample"/>s.</summary>
public static class ErsModelBuilder
{
    public const int SpeedBand = 10;
    public const int DistanceBin = 50;

    /// <summary>A band needs this many samples to count: a few seconds at full throttle.</summary>
    public const int MinBandSamples = 20;

    /// <summary>A straight-line fit needs this many samples.</summary>
    public const int MinFitSamples = 200;

    private const double MinPeakW = 5_000;

    // A mode fades where its output drops below 60 % of its peak and stays under 75 % at every higher speed.
    private const double FadeBelow = 0.6;
    private const double FadeStaysBelow = 0.75;

    // The part of a lap a lap must cover to count for the along-the-lap profile.
    private const double LapCoverage = 0.9;

    // Per-sample counter steps above this are resets or flashbacks, not harvest (2 MW at 10 Hz).
    private const double MaxCounterStep = 200_000;

    // Longer gaps between samples are pauses or dropped packets: no rate to read from them.
    private const double MaxSampleGap = 0.5;

    /// <summary>Minimum car mass with driver, kg: 798 in the 2025 rules, 768 in 2026.</summary>
    public static double CarMass(GameFormat format) => format == GameFormat.F1_26 ? 768 : 798;

    // The battery has charge to give a mode all it asks above this, J.
    private const float Charged = 300_000;

    // Harvest is learned only this far below a full battery and the lap's harvest limit, J: there it isn't cut short.
    private const float HarvestHeadroom = 100_000;

    // The traction bands: speeds (km/h) and lateral loads (g) a band starts at.
    private static readonly int[] TractionSpeeds = [0, 100, 140, 170, TractionMap.ReferenceKmh, 1000];
    private static readonly double[] TractionLoads = [0, 1.5, double.PositiveInfinity];

    // A traction band needs this many samples, and a slope known to within this share of the reference's.
    private const int MinTractionSamples = 300;
    private const double MaxTractionError = 0.15;

    public static ErsCarModel Build(GameFormat format, int trackId, IReadOnlyList<ModelSample> samples)
    {
        var laps = samples.Select(s => (s.RecordingId, s.LapNumber)).Distinct().Count();
        var powerLaps = samples.Where(s => s.IcePower > 0).Select(s => (s.RecordingId, s.LapNumber)).Distinct().Count();
        var steps = Steps(samples);
        return new ErsCarModel(format, trackId, laps, powerLaps, BuildDeployMap(samples, steps), FitStraightLine(format, samples), AlongLap(samples),
            BuildPositionMap(samples, steps), FitTraction(format, samples, steps));
    }

    /// <summary>What the deploy and harvest counters moved from the sample before to each sample; null across laps and gaps.</summary>
    /// <param name="Deployed">J; null on a reset or flashback.</param>
    /// <param name="Harvested">J; null on a reset or flashback.</param>
    public readonly record struct CounterStep(double Seconds, double? Deployed, double? Harvested);

    public static CounterStep?[] Steps(IReadOnlyList<ModelSample> samples)
    {
        static double? Valid(float step) => step is >= 0 and <= (float)MaxCounterStep ? step : null;
        var steps = new CounterStep?[samples.Count];
        for (var i = 1; i < samples.Count; i++)
        {
            var (a, b) = (samples[i - 1], samples[i]);
            var dt = b.SessionTime - a.SessionTime;
            if (a.RecordingId == b.RecordingId && a.LapNumber == b.LapNumber && dt is > 0 and <= MaxSampleGap)
            {
                steps[i] = new CounterStep(dt, Valid(b.Deployed - a.Deployed), Valid(b.Harvested - a.Harvested));
            }
        }

        return steps;
    }

    public static DeployMap BuildDeployMap(IReadOnlyList<ModelSample> samples) => BuildDeployMap(samples, Steps(samples));

    private static DeployMap BuildDeployMap(IReadOnlyList<ModelSample> samples, CounterStep?[] steps)
    {
        // What the battery paid from each sample to the next, by the deploy counter: J and s, per sample.
        var paid = steps.Select(s => s is { Deployed: { } joules } step ? (joules, step.Seconds) : ((double Joules, double Seconds)?)null).ToArray();

        var bands = Enumerable.Range(0, samples.Count)
            .Where(i => samples[i] is { IcePower: > 0, Throttle: >= 0.98f, Brake: <= 0.01f, ErsStore: > Charged, Override: false })
            .GroupBy(i => ((int)samples[i].Mode, (int)samples[i].Speed / SpeedBand * SpeedBand))
            .Where(b => b.Count() >= MinBandSamples)
            .Select(b =>
            {
                var steps = b.Where(i => paid[i] is not null).Select(i => paid[i]!.Value).ToList();
                var output = Median(b.Select(i => (double)samples[i].MgukPower));
                double? draw = steps.Count >= MinBandSamples && steps.Sum(p => p.Seconds) > 0 ? steps.Sum(p => p.Joules) / steps.Sum(p => p.Seconds) : null;
                return (Mode: b.Key.Item1, Point: new DeployPoint(b.Key.Item2, output, b.Count(), draw));
            })
            .ToList();

        // A band without enough counter steps pays what the measured ones pay per watt of output.
        var measured = bands.Where(b => b.Point.DrawW is not null && b.Point.PowerW > MinPeakW).ToList();
        var perWatt = measured.Count == 0 ? 1 : measured.Sum(b => b.Point.DrawW!.Value * b.Point.Samples) / measured.Sum(b => b.Point.PowerW * b.Point.Samples);

        var curves = bands
            .GroupBy(b => b.Mode)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var points = g.Select(b => b.Point.DrawW is null ? b.Point with { DrawW = b.Point.PowerW * perWatt } : b.Point).OrderBy(p => p.SpeedKmh).ToList();
                return new DeployCurve(g.Key, points, g.Key == DeployModes.None ? null : FadeFrom(points));
            })
            .Where(c => c.PeakW >= MinPeakW) // a mode that gave (next to) nothing, Hotlap in a race say, has no curve
            .ToList();
        return new DeployMap(curves);
    }

    /// <summary>
    /// The game's deploy and harvest at every segment of the lap: flat out, by mode, the MGU-K output and battery draw
    /// with charge to give and what it harvests (clipping); off full throttle, what it harvests. Harvest is only learned
    /// where the battery and the lap's harvest limit had room, so it is what the lap could take, not what it was left.
    /// </summary>
    public static PositionMap BuildPositionMap(IReadOnlyList<ModelSample> samples) => BuildPositionMap(samples, Steps(samples));

    private static PositionMap BuildPositionMap(IReadOnlyList<ModelSample> samples, CounterStep?[] steps)
    {
        const int modes = DeployModes.Overtake + 1;
        var length = samples.Where(s => s.LapDistance >= 0).Select(s => s.LapDistance).DefaultIfEmpty(-1).Max();
        if (length < 0)
        {
            return PositionMap.Empty;
        }

        var count = (int)(length / LapProfile.Step) + 1;
        var output = new (double Watts, double Kmh, int Samples, double DrawJ, double DrawS)[modes, count];
        var clip = new (double Joules, double Seconds, int Samples)[modes, count];
        var lift = new (double Joules, double Seconds, int Samples)[count];
        double drawn = 0, given = 0;
        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            if (s.LapDistance < 0 || s.Override)
            {
                continue;
            }

            var segment = (int)(s.LapDistance / LapProfile.Step);
            var mode = Math.Min((int)s.Mode, modes - 1);
            var flat = s.Throttle >= 0.98f && s.Brake <= 0.01f;
            if (flat && s.IcePower > 0 && s.ErsStore > Charged)
            {
                ref var cell = ref output[mode, segment];
                (cell.Watts, cell.Kmh, cell.Samples) = (cell.Watts + s.MgukPower, cell.Kmh + s.Speed, cell.Samples + 1);
                if (steps[i] is { Deployed: { } joules } step)
                {
                    (cell.DrawJ, cell.DrawS) = (cell.DrawJ + joules, cell.DrawS + step.Seconds);
                    (drawn, given) = (drawn + joules, given + s.MgukPower * step.Seconds);
                }
            }

            var room = s.ErsStore < LapSimulator.Capacity - HarvestHeadroom && (s.HarvestLimit <= 0 || s.Harvested < s.HarvestLimit - HarvestHeadroom);
            if (room && steps[i] is { Harvested: { } harvested } h)
            {
                ref var rate = ref flat ? ref clip[mode, segment] : ref lift[segment];
                (rate.Joules, rate.Seconds, rate.Samples) = (rate.Joules + harvested, rate.Seconds + h.Seconds, rate.Samples + 1);
            }
        }

        // A segment with too few counter steps draws what the map draws per watt of output over the lap.
        var perWatt = given > 0 ? drawn / given : 1;
        var deploy = new DeployCell?[modes][];
        var clipping = new HarvestRate?[modes][];
        for (var m = 0; m < modes; m++)
        {
            deploy[m] = new DeployCell?[count];
            clipping[m] = new HarvestRate?[count];
            for (var k = 0; k < count; k++)
            {
                var c = output[m, k];
                if (c.Samples > 0)
                {
                    var watts = c.Watts / c.Samples;
                    deploy[m][k] = new DeployCell(watts, c.DrawS >= 0.2 ? c.DrawJ / c.DrawS : watts * perWatt, c.Kmh / c.Samples, c.Samples);
                }

                clipping[m][k] = Rate(clip[m, k]);
            }
        }

        return new PositionMap(deploy, clipping, [.. lift.Select(Rate)]);

        static HarvestRate? Rate((double Joules, double Seconds, int Samples) r) => r.Seconds > 0 ? new HarvestRate(r.Joules / r.Seconds, r.Samples) : null;
    }

    /// <summary>
    /// The share of a change of MGU-K output that becomes acceleration, by speed and lateral load, against the share
    /// above <see cref="TractionMap.ReferenceKmh"/>. Samples are compared only with others at the same place (100 m) and
    /// speed (10 km/h), so the corner's own grip and drag cancel and what is left is what more or less power did: the
    /// slope of acceleration on output ÷ (m · v) there. Output counts clipping as negative.
    /// </summary>
    public static TractionMap FitTraction(GameFormat format, IReadOnlyList<ModelSample> samples) => FitTraction(format, samples, Steps(samples));

    private static TractionMap FitTraction(GameFormat format, IReadOnlyList<ModelSample> samples, CounterStep?[] steps)
    {
        var mass = CarMass(format);
        var points = new List<(int Cell, double Kmh, double G, double X, double Y)>();
        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            if (s is not { IcePower: > 0, Throttle: >= 0.98f, Brake: <= 0.01f, Override: false, Speed: >= 60, LapDistance: >= 0 })
            {
                continue;
            }

            var clip = steps[i] is { Harvested: { } joules } step ? joules / step.Seconds : 0;
            var v = s.Speed / 3.6;
            points.Add(((int)(s.LapDistance / 100) * 1000 + (int)(s.Speed / 10), s.Speed, Math.Abs(s.GForceLateral),
                (s.MgukPower - clip) / ((mass + s.Fuel) * v), s.GForceLongitudinal * 9.81));
        }

        var reference = Slope(points.Where(p => p.Kmh >= TractionMap.ReferenceKmh));
        if (reference is not { Samples: >= MinTractionSamples, Value: > 0.1 } r)
        {
            return TractionMap.Unlimited;
        }

        var bands = new List<TractionBand>();
        for (var b = 0; b + 1 < TractionSpeeds.Length; b++)
        {
            var (from, to) = (TractionSpeeds[b], TractionSpeeds[b + 1]);
            var inBand = points.Where(p => p.Kmh >= from && p.Kmh < to).ToList();
            Add(inBand, from, to, null, null);
            for (var g = 0; g + 1 < TractionLoads.Length; g++)
            {
                var (low, high) = (TractionLoads[g], TractionLoads[g + 1]);
                Add(inBand.Where(p => p.G >= low && p.G < high), from, to, low, high);
            }
        }

        return new TractionMap(bands);

        void Add(IEnumerable<(int Cell, double Kmh, double G, double X, double Y)> band, int from, int to, double? low, double? high)
        {
            if (Slope(band) is { Samples: >= MinTractionSamples } s && s.Error <= MaxTractionError * r.Value)
            {
                bands.Add(new TractionBand(from, to, low, high, Math.Clamp(s.Value / r.Value, 0.05, 1), s.Samples));
            }
        }
    }

    /// <summary>Least-squares slope of Y on X within each cell (both less the cell's mean), with its standard error.</summary>
    private static (double Value, double Error, int Samples)? Slope(IEnumerable<(int Cell, double Kmh, double G, double X, double Y)> points)
    {
        double sxy = 0, sxx = 0;
        var residuals = new List<(double X, double Y)>();
        foreach (var cell in points.GroupBy(p => p.Cell))
        {
            var list = cell.ToList();
            if (list.Count < 2)
            {
                continue;
            }

            var (mx, my) = (list.Average(p => p.X), list.Average(p => p.Y));
            foreach (var p in list)
            {
                var (x, y) = (p.X - mx, p.Y - my);
                (sxy, sxx) = (sxy + x * y, sxx + x * x);
                residuals.Add((x, y));
            }
        }

        if (residuals.Count < 3 || sxx <= 0)
        {
            return null;
        }

        var slope = sxy / sxx;
        var error = Math.Sqrt(residuals.Sum(p => (p.Y - slope * p.X) * (p.Y - slope * p.X)) / (residuals.Count - 2) / sxx);
        return (slope, error, residuals.Count);
    }

    private static int? FadeFrom(IReadOnlyList<DeployPoint> points)
    {
        if (points.Count < 2)
        {
            return null;
        }

        var peak = points.Max(p => p.PowerW);
        var peakIndex = points.ToList().FindIndex(p => p.PowerW == peak);
        for (var i = peakIndex + 1; i < points.Count; i++)
        {
            if (points[i].PowerW < FadeBelow * peak && points.Skip(i).All(p => p.PowerW < FadeStaysBelow * peak))
            {
                return points[i].SpeedKmh;
            }
        }

        return null;
    }

    public static IReadOnlyList<StraightLineFit> FitStraightLine(GameFormat format, IReadOnlyList<ModelSample> samples)
    {
        var mass = CarMass(format);
        var fits = new List<StraightLineFit>();
        foreach (var group in samples
                     .Where(s => s.IcePower > 0 && s.Throttle >= 0.98f && s.Brake <= 0.01f && s.Speed > 120 && Math.Abs(s.Steer) < 0.05f)
                     .GroupBy(s => (int)s.Aero)
                     .OrderBy(g => g.Key))
        {
            var points = group.ToList();
            if (points.Count < MinFitSamples)
            {
                continue;
            }

            // a = η·x1 + β·x2 + γ with x1 = P/(m v), x2 = v²/m; drag = −β, resistance = −γ.
            var x = new double[points.Count][];
            var y = new double[points.Count];
            for (var i = 0; i < points.Count; i++)
            {
                var s = points[i];
                var v = s.Speed / 3.6;
                var m = mass + s.Fuel;
                x[i] = [(s.IcePower + s.MgukPower) / (m * v), v * v / m, 1];
                y[i] = s.GForceLongitudinal * 9.81;
            }

            if (LeastSquares(x, y) is not { } c)
            {
                continue;
            }

            var mean = y.Average();
            double residual = 0, total = 0;
            for (var i = 0; i < y.Length; i++)
            {
                var predicted = c[0] * x[i][0] + c[1] * x[i][1] + c[2];
                residual += (y[i] - predicted) * (y[i] - predicted);
                total += (y[i] - mean) * (y[i] - mean);
            }

            fits.Add(new StraightLineFit(group.Key, c[0], -c[1], -c[2], total > 0 ? 1 - residual / total : 0, Math.Sqrt(residual / y.Length), points.Count));
        }

        return fits;
    }

    /// <summary>Energy harvested and deployed per 50 m of the lap, averaged over the laps that cover most of it.</summary>
    public static IReadOnlyList<EnergyBin> AlongLap(IReadOnlyList<ModelSample> samples)
    {
        var lapLength = samples.Count == 0 ? 0 : samples.Max(s => s.LapDistance);
        if (lapLength <= 0)
        {
            return [];
        }

        var bins = (int)(lapLength / DistanceBin) + 1;
        var harvested = new double[bins];
        var deployed = new double[bins];
        var laps = new int[bins];

        foreach (var lap in samples.GroupBy(s => (s.RecordingId, s.LapNumber)))
        {
            var list = lap.ToList();
            var covered = list.Max(s => s.LapDistance) - Math.Max(0, list.Min(s => s.LapDistance));
            if (covered < LapCoverage * lapLength)
            {
                continue;
            }

            var seen = new bool[bins];
            for (var i = 1; i < list.Count; i++)
            {
                if (list[i].LapDistance < 0)
                {
                    continue;
                }

                var bin = Math.Min(bins - 1, (int)(list[i].LapDistance / DistanceBin));
                seen[bin] = true;
                var harvest = list[i].Harvested - list[i - 1].Harvested;
                var deploy = list[i].Deployed - list[i - 1].Deployed;
                if (harvest is >= 0 and <= (float)MaxCounterStep)
                {
                    harvested[bin] += harvest;
                }

                if (deploy is >= 0 and <= (float)MaxCounterStep)
                {
                    deployed[bin] += deploy;
                }
            }

            for (var b = 0; b < bins; b++)
            {
                laps[b] += seen[b] ? 1 : 0;
            }
        }

        return [.. Enumerable.Range(0, bins)
            .Where(b => laps[b] > 0)
            .Select(b => new EnergyBin(b * DistanceBin, harvested[b] / laps[b], deployed[b] / laps[b], laps[b]))];
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    /// <summary>Solves the normal equations; null when the columns are degenerate.</summary>
    private static double[]? LeastSquares(double[][] x, double[] y)
    {
        var n = x[0].Length;
        var a = new double[n, n + 1];
        for (var i = 0; i < y.Length; i++)
        {
            for (var r = 0; r < n; r++)
            {
                a[r, n] += x[i][r] * y[i];
                for (var c = 0; c < n; c++)
                {
                    a[r, c] += x[i][r] * x[i][c];
                }
            }
        }

        // Gauss–Jordan with partial pivoting.
        for (var c = 0; c < n; c++)
        {
            var pivot = c;
            for (var r = c + 1; r < n; r++)
            {
                if (Math.Abs(a[r, c]) > Math.Abs(a[pivot, c]))
                {
                    pivot = r;
                }
            }

            if (Math.Abs(a[pivot, c]) < 1e-12)
            {
                return null;
            }

            for (var k = 0; k <= n; k++)
            {
                (a[c, k], a[pivot, k]) = (a[pivot, k], a[c, k]);
            }

            for (var r = 0; r < n; r++)
            {
                if (r == c)
                {
                    continue;
                }

                var f = a[r, c] / a[c, c];
                for (var k = 0; k <= n; k++)
                {
                    a[r, k] -= f * a[c, k];
                }
            }
        }

        return [.. Enumerable.Range(0, n).Select(i => a[i, n] / a[i, i])];
    }
}
