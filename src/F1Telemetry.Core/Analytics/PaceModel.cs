using F1Telemetry.Core.Models;
using F1Telemetry.Protocol.Lookups;

namespace F1Telemetry.Core.Analytics;

/// <summary>One clean lap as seen by the pace model.</summary>
/// <param name="TyreAge">Laps since the stint began (its first lap, usually the out-lap, is 0).</param>
/// <param name="FuelKg">Mean fuel mass in the tank over the lap.</param>
public sealed record PaceLap(int LapNumber, int StintNumber, string Compound, int TyreAge, double LapSeconds, double FuelKg);

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
    /// <summary>Laps this much slower than the stint's median are mistakes, traffic or damage, not pace.</summary>
    public const double OutlierSeconds = 3;

    /// <summary>Fitted fuel effects outside this range are noise, and fall back to the default.</summary>
    public const double MaxPlausibleFuelEffect = 0.1;

    /// <summary>
    /// The laps of one stint that represent pace: regular laps with a time and fuel data, without a race's standing-start
    /// lap 1 and without outliers.
    /// </summary>
    public static List<PaceLap> CleanLaps(int stintNumber, IReadOnlyList<LapRecord> stintLaps, IReadOnlyDictionary<int, LapAggregate> aggregates, int sessionType)
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

            laps.Add(new PaceLap(lap.LapNumber, stintNumber, lap.Compound, lap.LapNumber - firstLap, lap.LapTimeMs / 1000.0,
                (aggregate.MinFuel + aggregate.MaxFuel) / 2));
        }

        if (laps.Count < 3)
        {
            return laps;
        }

        var median = Median(laps.Select(l => l.LapSeconds));
        return laps.Where(l => l.LapSeconds <= median + OutlierSeconds).ToList();
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

        if (Solve(ata, atb) is not { } x || x[^1] is < 0 or > MaxPlausibleFuelEffect)
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
