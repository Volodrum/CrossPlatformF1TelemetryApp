namespace F1Telemetry.Core.Energy;

/// <summary>
/// What the game does with the ERS at every <see cref="LapProfile.Step"/> m of the lap, by deploy mode, over every lap
/// at the track. The game runs its own energy map under the mode: in None it deploys hard out of some corners and
/// nothing out of others, and at the end of some straights it harvests flat out ("clipping"), which takes that power
/// from the wheels. A speed-only <see cref="DeployMap"/> averages all of that away. Off full throttle it holds what the
/// MGU-K harvests at each point while the lap's harvest limit hasn't been reached.
/// </summary>
public sealed class PositionMap
{
    /// <summary>A segment needs this many samples for its clipping or lift harvest to count.</summary>
    public const int MinSamples = 6;

    // The speed curve counts as this many samples when a segment's own output is blended with it.
    private const double CurveWeight = 6;

    private readonly DeployCell?[][] _deploy;
    private readonly HarvestRate?[][] _clip;
    private readonly HarvestRate?[] _lift;

    /// <param name="deploy">[mode][segment]: flat out with charge in the battery.</param>
    /// <param name="clip">[mode][segment]: harvest flat out, below the harvest limit and a full battery.</param>
    /// <param name="lift">[segment]: harvest off full throttle (braking, lifting), below the limit and a full battery.</param>
    public PositionMap(DeployCell?[][] deploy, HarvestRate?[][] clip, HarvestRate?[] lift)
    {
        _deploy = deploy;
        _clip = clip;
        _lift = lift;
    }

    public static PositionMap Empty { get; } = new([], [], []);

    public int Segments => _lift.Length;

    /// <summary>
    /// MGU-K output and battery draw of <paramref name="mode"/> flat out at <paramref name="segment"/> and
    /// <paramref name="kmh"/>, W: the segment's own figures blended with the speed curve by how many samples it has,
    /// then moved along the curve from the speed it was learned at. The curve alone where the segment never ran the
    /// mode with charge; null when there is neither.
    /// </summary>
    public (double Output, double Draw)? Deploy(int mode, int segment, double kmh, DeployCurve? curve)
    {
        var cell = At(_deploy, mode, segment);
        if (cell is not { } c)
        {
            return curve is null ? null : (curve.PowerAt(kmh) ?? 0, curve.DrawAt(kmh) ?? 0);
        }

        if (curve is null)
        {
            return (c.OutputW, c.DrawW);
        }

        var w = c.Samples / (c.Samples + CurveWeight);
        var (learnedOutput, learnedDraw) = (curve.PowerAt(c.Kmh) ?? 0, curve.DrawAt(c.Kmh) ?? 0);
        var output = w * c.OutputW + (1 - w) * learnedOutput + (curve.PowerAt(kmh) ?? 0) - learnedOutput;
        var draw = w * c.DrawW + (1 - w) * learnedDraw + (curve.DrawAt(kmh) ?? 0) - learnedDraw;
        return (Math.Max(0, output), Math.Max(0, draw));
    }

    /// <summary>What the game harvests flat out at <paramref name="segment"/> in <paramref name="mode"/>, W; null when unknown.</summary>
    public double? Clip(int mode, int segment) => At(_clip, mode, segment) is { Samples: >= MinSamples } r ? r.Watts : null;

    /// <summary>What the MGU-K harvests off full throttle at <paramref name="segment"/>, W; null when unknown.</summary>
    public double? Lift(int segment) => segment >= 0 && segment < _lift.Length && _lift[segment] is { Samples: >= MinSamples } r ? r.Watts : null;

    private static T? At<T>(T?[][] table, int mode, int segment) where T : struct =>
        mode >= 0 && mode < table.Length && segment >= 0 && segment < table[mode].Length ? table[mode][segment] : null;
}

/// <summary>Mean MGU-K output and battery draw (by the deploy counter) over <paramref name="Samples"/> samples at <paramref name="Kmh"/> on average.</summary>
public readonly record struct DeployCell(double OutputW, double DrawW, double Kmh, int Samples);

/// <summary>Mean harvest rate over <paramref name="Samples"/> samples, W.</summary>
public readonly record struct HarvestRate(double Watts, int Samples);

/// <summary>
/// How much of a change in MGU-K output becomes acceleration at <paramref name="FromKmh"/>–<paramref name="ToKmh"/>
/// (and, when set, <paramref name="FromG"/>–<paramref name="ToG"/> lateral), as a share of what it does above
/// <see cref="TractionMap.ReferenceKmh"/>.
/// </summary>
public sealed record TractionBand(int FromKmh, int ToKmh, double? FromG, double? ToG, double Factor, int Samples);

/// <summary>
/// The share of a change in MGU-K output that becomes acceleration, by speed and lateral g, against what it does above
/// <see cref="ReferenceKmh"/>, where the straight-line fit is learned. Out of slow corners the tyres, not the power, set
/// the acceleration: in a 2026 race at Melbourne about a tenth of it below 140 km/h and two thirds at 140–200 km/h.
/// Learned by comparing samples at the same place and speed that had different output (mode, battery, clipping).
/// </summary>
public sealed record TractionMap(IReadOnlyList<TractionBand> Bands)
{
    public const int ReferenceKmh = 200;

    public static TractionMap Unlimited { get; } = new([]);

    /// <summary>The band for this speed and lateral load, else the speed's band over every load, else 1.</summary>
    public double Factor(double kmh, double lateralG)
    {
        var g = Math.Abs(lateralG);
        TractionBand? any = null;
        foreach (var band in Bands)
        {
            if (kmh < band.FromKmh || kmh >= band.ToKmh)
            {
                continue;
            }

            if (band.FromG is null)
            {
                any = band;
            }
            else if (g >= band.FromG && g < band.ToG)
            {
                return band.Factor;
            }
        }

        return any?.Factor ?? 1;
    }
}
