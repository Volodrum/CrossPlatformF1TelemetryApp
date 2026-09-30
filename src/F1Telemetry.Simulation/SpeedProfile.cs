using F1Telemetry.Core.Tracks;

namespace F1Telemetry.Simulation;

/// <summary>
/// Physically plausible speed-vs-distance profile for a closed racing line: corner speed limited by lateral
/// grip (v = sqrt(a_lat * R)), then forward (traction) and backward (braking) passes.
/// </summary>
internal sealed class SpeedProfile
{
    private const double Spacing = 5.0;
    private const double MaxSpeed = 92;        // ~330 km/h
    private const double MinSpeed = 18;
    private const double LateralGrip = 4.2 * 9.81;
    private const double Braking = 38;

    private readonly TrackPoint[] _points;
    private readonly double[] _speed;
    private readonly double[] _curvature;

    public SpeedProfile(IReadOnlyList<TrackPoint> rawLine)
    {
        _points = Resample(rawLine, Spacing);
        var n = _points.Length;
        Length = n * Spacing;
        _curvature = new double[n];
        _speed = new double[n];

        for (var i = 0; i < n; i++)
        {
            _curvature[i] = SignedCurvature(_points[(i - 4 + n) % n], _points[i], _points[(i + 4) % n]);
            var radius = Math.Abs(_curvature[i]) < 1e-6 ? double.PositiveInfinity : 1 / Math.Abs(_curvature[i]);
            _speed[i] = Math.Clamp(Math.Sqrt(LateralGrip * radius), MinSpeed, MaxSpeed);
        }

        // Two laps of passes so the start/finish seam converges.
        for (var pass = 0; pass < 2; pass++)
        {
            for (var k = 1; k <= n; k++)
            {
                var i = k % n;
                var prev = _speed[(i - 1 + n) % n];
                var traction = 11 * (1 - prev / (MaxSpeed + 10)) + 1.5;
                _speed[i] = Math.Min(_speed[i], Math.Sqrt(prev * prev + 2 * traction * Spacing));
            }

            for (var k = n - 1; k >= 0; k--)
            {
                var next = _speed[(k + 1) % n];
                _speed[k] = Math.Min(_speed[k], Math.Sqrt(next * next + 2 * Braking * Spacing));
            }
        }
    }

    public double Length { get; }

    public double SpeedAt(double distance) => Interpolate(_speed, distance);

    public (double X, double Z, double ForwardX, double ForwardZ, double Curvature) Sample(double distance)
    {
        var n = _points.Length;
        var d = (distance % Length + Length) % Length;
        var i = (int)(d / Spacing) % n;
        var j = (i + 1) % n;
        var f = d / Spacing - Math.Floor(d / Spacing);
        var a = _points[i];
        var b = _points[j];
        var dx = b.X - a.X;
        var dz = b.Z - a.Z;
        var len = Math.Sqrt(dx * dx + dz * dz);
        if (len < 1e-9)
        {
            len = 1;
        }

        return (a.X + dx * f, a.Z + dz * f, dx / len, dz / len, _curvature[i]);
    }

    private double Interpolate(double[] values, double distance)
    {
        var n = values.Length;
        var d = (distance % Length + Length) % Length;
        var i = (int)(d / Spacing) % n;
        var f = d / Spacing - Math.Floor(d / Spacing);
        return values[i] + (values[(i + 1) % n] - values[i]) * f;
    }

    private static double SignedCurvature(TrackPoint a, TrackPoint b, TrackPoint c)
    {
        var ab = Dist(a, b);
        var bc = Dist(b, c);
        var ca = Dist(c, a);
        var cross = (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);
        var denominator = ab * bc * ca;
        return denominator < 1e-9 ? 0 : 2 * cross / denominator;
    }

    private static double Dist(TrackPoint a, TrackPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static TrackPoint[] Resample(IReadOnlyList<TrackPoint> line, double spacing)
    {
        var closed = line.Append(line[0]).ToList();
        var result = new List<TrackPoint> { closed[0] };
        var carry = 0.0;
        for (var i = 1; i < closed.Count; i++)
        {
            var a = closed[i - 1];
            var b = closed[i];
            var segment = Dist(a, b);
            var pos = spacing - carry;
            while (pos <= segment)
            {
                var f = pos / segment;
                result.Add(new TrackPoint(a.X + (b.X - a.X) * f, a.Z + (b.Z - a.Z) * f));
                pos += spacing;
            }

            carry = segment - (pos - spacing);
        }

        return result.ToArray();
    }
}
