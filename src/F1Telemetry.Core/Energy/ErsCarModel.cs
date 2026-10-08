using F1Telemetry.Protocol;

namespace F1Telemetry.Core.Energy;

/// <summary>MGU-K output in one deploy mode over a 10 km/h band starting at <paramref name="SpeedKmh"/>: the median of
/// <paramref name="Samples"/> full-throttle samples.</summary>
public sealed record DeployPoint(int SpeedKmh, double PowerW, int Samples);

/// <summary>
/// MGU-K output by speed in one deploy mode, and the speed from which it fades (null when it doesn't).
/// </summary>
public sealed record DeployCurve(int Mode, IReadOnlyList<DeployPoint> Points, int? FadeFromKmh)
{
    public double PeakW => Points.Count == 0 ? 0 : Points.Max(p => p.PowerW);

    /// <summary>Output at <paramref name="kmh"/>: the band it falls in, or the nearest band measured.</summary>
    public double? PowerAt(double kmh) =>
        Points.Count == 0 ? null : Points.MinBy(p => Math.Abs(p.SpeedKmh + ErsModelBuilder.SpeedBand / 2.0 - kmh))!.PowerW;
}

/// <summary>What each deploy mode delivers, learned from full-throttle samples with charge in the battery.</summary>
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
/// The car's ERS as learned from every lap recorded at a track: what each deploy mode delivers by speed, where on the
/// lap energy is harvested and deployed, and how the car accelerates on the straights.
/// </summary>
public sealed record ErsCarModel(
    GameFormat Format,
    int TrackId,
    int Laps,
    int PowerLaps,
    DeployMap Deploy,
    IReadOnlyList<StraightLineFit> StraightLine,
    IReadOnlyList<EnergyBin> AlongLap)
{
    /// <summary>Laps that recorded ICE and MGU-K output (from v0.3, or re-imported): the deploy map and fits need them.</summary>
    public bool HasPowerData => PowerLaps > 0;
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

    /// <summary>Minimum car mass with driver, kg: 798 in the 2025 rules, 768 in 2026.</summary>
    public static double CarMass(GameFormat format) => format == GameFormat.F1_26 ? 768 : 798;

    public static ErsCarModel Build(GameFormat format, int trackId, IReadOnlyList<ModelSample> samples)
    {
        var laps = samples.Select(s => (s.RecordingId, s.LapNumber)).Distinct().Count();
        var powerLaps = samples.Where(s => s.IcePower > 0).Select(s => (s.RecordingId, s.LapNumber)).Distinct().Count();
        return new ErsCarModel(format, trackId, laps, powerLaps, BuildDeployMap(samples), FitStraightLine(format, samples), AlongLap(samples));
    }

    public static DeployMap BuildDeployMap(IReadOnlyList<ModelSample> samples)
    {
        var curves = samples
            .Where(s => s.IcePower > 0 && s.Mode > 0 && s.Throttle >= 0.98f && s.Brake <= 0.01f && s.ErsStore > 300_000)
            .GroupBy(s => (int)s.Mode)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var points = g.GroupBy(s => (int)s.Speed / SpeedBand * SpeedBand)
                    .Where(b => b.Count() >= MinBandSamples)
                    .OrderBy(b => b.Key)
                    .Select(b => new DeployPoint(b.Key, Median(b.Select(s => (double)s.MgukPower)), b.Count()))
                    .ToList();
                return new DeployCurve(g.Key, points, FadeFrom(points));
            })
            .Where(c => c.PeakW >= MinPeakW) // a mode that gave (next to) nothing, Hotlap in a race say, has no curve
            .ToList();
        return new DeployMap(curves);
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
