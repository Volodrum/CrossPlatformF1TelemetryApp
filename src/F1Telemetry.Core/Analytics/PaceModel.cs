using F1Telemetry.Core.Models;
using F1Telemetry.Protocol.Lookups;

namespace F1Telemetry.Core.Analytics;

/// <summary>One clean lap as seen by the pace model.</summary>
/// <param name="TyreAge">Laps since the stint began (its first lap, usually the out-lap, is 0).</param>
/// <param name="FuelKg">Mean fuel mass in the tank over the lap.</param>
/// <param name="WearPercent">Mean wear of the most worn tyre over the lap, %.</param>
public sealed record PaceLap(int LapNumber, int StintNumber, string Compound, int TyreAge, double LapSeconds, double FuelKg, double WearPercent = 0);

public enum FuelEffectSource
{
    /// <summary>The configured constant (see <see cref="Engine.StrategyOptions.FuelEffectSecondsPerKg"/>).</summary>
    Default,

    /// <summary>Fitted from the laps: the same compound was run at two different fuel loads.</summary>
    Measured,
}

public sealed record FuelEffect(double SecondsPerKg, FuelEffectSource Source, string Basis);

/// <summary>
/// Straight-line fit of a stint's fuel-corrected pace: lap time = base + degradation × tyre age + fuel effect × fuel kg.
/// </summary>
/// <param name="RawDegradationPerLap">Slope of the uncorrected lap times, s/lap: tyre wear minus the fuel burn-off gain.</param>
/// <param name="DegradationPerLap">Slope after taking the fuel effect out, s/lap: what the tyres cost.</param>
/// <param name="BaseLapSeconds">Fitted lap time on fresh tyres with an empty tank (compound pace plus driving).</param>
public sealed record StintPaceFit(int StintNumber, string Compound, int Laps, double RawDegradationPerLap, double DegradationPerLap, double BaseLapSeconds)
{
    public double Predict(int tyreAge, double fuelKg, double fuelEffect) => BaseLapSeconds + DegradationPerLap * tyreAge + fuelEffect * fuelKg;
}

/// <summary>
/// Separates the two things that change lap time over a stint: tyres wearing (slower) and fuel burning off (faster).
/// <para>Within one stint they can't be told apart: fuel falls in step with tyre age. When a compound is run in two
/// stints, the same tyre age comes round again at a different fuel load, which pins the fuel effect down; otherwise
/// the configured constant is used.</para>
/// </summary>
public static class PaceModel
{
    /// <summary>Laps this much slower than the stint's median are mistakes, traffic or damage, not pace (short stints).</summary>
    public const double OutlierSeconds = 3;

    /// <summary>
    /// A lap this much slower than the stint's trend line is traffic, a mistake or damage. Tighter than
    /// <see cref="OutlierSeconds"/> because the line follows the tyres wearing, so a lap late in a stint isn't mistaken
    /// for a slow one.
    /// </summary>
    public const double SlowOutlierSeconds = 1.5;

    /// <summary>
    /// A lap this much faster than the trend line is a cut corner, a tow or a lap stitched by a flashback: not pace.
    /// </summary>
    public const double FastOutlierSeconds = 1.0;

    /// <summary>Laps run on a set past the cliff wear lose time on purpose: they are allowed this far above the line.</summary>
    public const double CliffOutlierSeconds = 4;

    /// <summary>Fitted fuel effects outside this range are noise, and fall back to the default.</summary>
    public const double MaxPlausibleFuelEffect = 0.1;

    /// <summary>A measured fuel effect is used only when the data pins it down to within this, s/kg (one standard error).</summary>
    public const double MaxFuelEffectError = 0.008;

    /// <summary>
    /// The laps of one stint that represent pace: regular laps with a time and fuel data, without a race's standing-start
    /// lap 1 and without outliers.
    /// <para>From 4 laps the outliers are judged against the stint's trend (a Theil–Sen line through lap time against
    /// tyre age, which a few bad laps can't tilt), on both sides: much slower is traffic or a mistake, much faster a cut
    /// corner or a flashback. The band widens with the stint's own scatter. Laps on a set past
    /// <paramref name="cliffWearPercent"/> stay out of the line and may run further above it.</para>
    /// </summary>
    public static List<PaceLap> CleanLaps(int stintNumber, IReadOnlyList<LapRecord> stintLaps, IReadOnlyDictionary<int, LapAggregate> aggregates, int sessionType,
        double cliffWearPercent = 60)
    {
        if (stintLaps.Count == 0)
        {
            return [];
        }

        var isRace = SessionTypes.IsRace(sessionType);
        var firstLap = stintLaps[0].LapNumber;
        var laps = new List<PaceLap>();
        foreach (var lap in stintLaps)
        {
            if (lap.LapType != LapType.Regular || !lap.HasTime || (isRace && lap.LapNumber == 1)
                || !aggregates.TryGetValue(lap.LapNumber, out var aggregate))
            {
                continue;
            }

            var wear = Math.Max(Math.Max(aggregate.MinWearFl + aggregate.MaxWearFl, aggregate.MinWearFr + aggregate.MaxWearFr),
                Math.Max(aggregate.MinWearRl + aggregate.MaxWearRl, aggregate.MinWearRr + aggregate.MaxWearRr)) / 2;
            laps.Add(new PaceLap(lap.LapNumber, stintNumber, lap.Compound, lap.LapNumber - firstLap, lap.LapTimeMs / 1000.0,
                (aggregate.MinFuel + aggregate.MaxFuel) / 2, wear));
        }

        if (laps.Count < 3)
        {
            return laps;
        }

        var median = Median(laps.Select(l => l.LapSeconds));
        laps = laps.Where(l => l.LapSeconds <= median + OutlierSeconds).ToList();
        if (laps.Count < 4)
        {
            return laps;
        }

        var straight = laps.Where(l => l.WearPercent < cliffWearPercent).ToList();
        if (straight.Count < 3)
        {
            straight = laps;
        }

        var (intercept, slope) = TheilSen(straight.Select(l => ((double)l.TyreAge, l.LapSeconds)).ToList());
        double Residual(PaceLap l) => l.LapSeconds - (intercept + slope * l.TyreAge);
        var residuals = straight.Select(Residual).ToList();
        var centre = Median(residuals);
        var scatter = 1.4826 * Median(residuals.Select(r => Math.Abs(r - centre)));
        var slow = Math.Max(SlowOutlierSeconds, 4 * scatter);
        var fast = Math.Max(FastOutlierSeconds, 4 * scatter);
        return laps.Where(l =>
        {
            var r = Residual(l) - centre;
            return r >= -fast && r <= (l.WearPercent >= cliffWearPercent ? Math.Max(CliffOutlierSeconds, slow) : slow);
        }).ToList();
    }

    /// <summary>
    /// Theil–Sen line: the slope is the median of the slopes between every pair of points, the intercept the median of
    /// what is left. Up to about three in ten points can be anything without moving it far.
    /// </summary>
    public static (double Intercept, double Slope) TheilSen(IReadOnlyList<(double X, double Y)> points)
    {
        var slopes = new List<double>(points.Count * (points.Count - 1) / 2);
        for (var i = 0; i < points.Count; i++)
        {
            for (var j = i + 1; j < points.Count; j++)
            {
                if (points[j].X != points[i].X)
                {
                    slopes.Add((points[j].Y - points[i].Y) / (points[j].X - points[i].X));
                }
            }
        }

        var slope = slopes.Count > 0 ? Median(slopes) : 0;
        return (Median(points.Select(p => p.Y - slope * p.X)), slope);
    }

    public static StintPaceFit? FitStint(int stintNumber, string compound, IReadOnlyList<PaceLap> laps, double fuelEffect)
    {
        if (laps.Count == 0)
        {
            return null;
        }

        var raw = LinearRegression.Slope(laps.Select(l => ((double)l.TyreAge, l.LapSeconds)).ToList());
        var corrected = laps.Select(l => ((double)l.TyreAge, l.LapSeconds - fuelEffect * l.FuelKg)).ToList();
        var degradation = LinearRegression.Slope(corrected);
        var baseLap = corrected.Average(p => p.Item2) - degradation * corrected.Average(p => p.Item1);
        return new StintPaceFit(stintNumber, compound, laps.Count, raw, degradation, baseLap);
    }

    /// <summary>
    /// Least-squares fit of lap time = base[compound] + degradation[compound] × age + fuel effect × fuel over every
    /// compound run in two or more stints. Null when no compound was, or the result is implausible.
    /// </summary>
    public static FuelEffect? MeasureFuelEffect(IReadOnlyList<PaceLap> laps)
    {
        var repeated = laps
            .GroupBy(l => l.Compound)
            .Where(g => g.GroupBy(l => l.StintNumber).Count(s => s.Count() >= 2) >= 2)
            .Select(g => g.Key)
            .ToList();
        if (repeated.Count == 0)
        {
            return null;
        }

        var used = laps.Where(l => repeated.Contains(l.Compound)).ToList();
        var unknowns = 2 * repeated.Count + 1;
        if (used.Count < unknowns + 2)
        {
            return null;
        }

        // Normal equations for x = [base_c..., degradation_c..., fuel effect].
        var ata = new double[unknowns, unknowns];
        var atb = new double[unknowns];
        var row = new double[unknowns];
        foreach (var lap in used)
        {
            Array.Clear(row);
            var c = repeated.IndexOf(lap.Compound);
            row[c] = 1;
            row[repeated.Count + c] = lap.TyreAge;
            row[^1] = lap.FuelKg;
            for (var i = 0; i < unknowns; i++)
            {
                atb[i] += row[i] * lap.LapSeconds;
                for (var j = 0; j < unknowns; j++)
                {
                    ata[i, j] += row[i] * row[j];
                }
            }
        }

        if (Solve((double[,])ata.Clone(), (double[])atb.Clone()) is not { } x || x[^1] is < 0 or > MaxPlausibleFuelEffect)
        {
            return null;
        }

        // How well the laps pin it down: its standard error, from the scatter left over and how far apart the fuel loads
        // were. Two stints at nearly the same load, or noisy laps, give a figure that is mostly noise.
        var rss = 0.0;
        foreach (var lap in used)
        {
            var c = repeated.IndexOf(lap.Compound);
            var predicted = x[c] + x[repeated.Count + c] * lap.TyreAge + x[^1] * lap.FuelKg;
            rss += (lap.LapSeconds - predicted) * (lap.LapSeconds - predicted);
        }

        var unit = new double[unknowns];
        unit[^1] = 1;
        if (Solve((double[,])ata.Clone(), unit) is not { } column || column[^1] <= 0
            || Math.Sqrt(rss / (used.Count - unknowns) * column[^1]) > MaxFuelEffectError)
        {
            return null;
        }

        var stints = used.Select(l => l.StintNumber).Distinct().Count();
        return new FuelEffect(x[^1], FuelEffectSource.Measured, $"measured from {stints} stints on {string.Join(" / ", repeated)}");
    }

    public static FuelEffect Resolve(IReadOnlyList<PaceLap> laps, double defaultSecondsPerKg) =>
        MeasureFuelEffect(laps) ?? new FuelEffect(defaultSecondsPerKg, FuelEffectSource.Default, "typical F1 value, not measured");

    internal static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0)
        {
            return 0;
        }

        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>Gaussian elimination with partial pivoting; null when the system is singular (the data can't separate the terms).</summary>
    private static double[]? Solve(double[,] a, double[] b)
    {
        var n = b.Length;
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < n; r++)
            {
                if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col]))
                {
                    pivot = r;
                }
            }

            if (Math.Abs(a[pivot, col]) < 1e-9)
            {
                return null;
            }

            if (pivot != col)
            {
                for (var k = 0; k < n; k++)
                {
                    (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);
                }

                (b[col], b[pivot]) = (b[pivot], b[col]);
            }

            for (var r = col + 1; r < n; r++)
            {
                var f = a[r, col] / a[col, col];
                for (var k = col; k < n; k++)
                {
                    a[r, k] -= f * a[col, k];
                }

                b[r] -= f * b[col];
            }
        }

        var x = new double[n];
        for (var r = n - 1; r >= 0; r--)
        {
            var sum = b[r];
            for (var k = r + 1; k < n; k++)
            {
                sum -= a[r, k] * x[k];
            }

            x[r] = sum / a[r, r];
        }

        return x;
    }
}
