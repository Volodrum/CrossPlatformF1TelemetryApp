using F1Telemetry.Core.Models;

namespace F1Telemetry.Core.Engine;

public enum SpotterZone
{
    /// <summary>A car to the player's left, beside or coming alongside.</summary>
    Left,

    /// <summary>The same on the right.</summary>
    Right,

    /// <summary>A car in line behind the player.</summary>
    Behind,
}

/// <summary>
/// One car on the spotter bars.
/// </summary>
/// <param name="Offset">
/// Side lanes: metres the car's centre is ahead of the player's (negative = behind). <see cref="SpotterZone.Behind"/>:
/// metres its centre is to the player's right (negative = left).
/// </param>
/// <param name="Gap">
/// Side lanes: metres between the two cars' centre lines. <see cref="SpotterZone.Behind"/>: metres its centre is behind
/// the player's.
/// </param>
/// <param name="Alert">Side lanes: the cars overlap, so turning in hits it. Behind: it is on the gearbox.</param>
public readonly record struct SpotterMark(SpotterZone Zone, float Offset, float Gap, bool Alert);

/// <summary>
/// Reduces a <see cref="RadarFrame"/> to what a spotter would call: who is beside the player on each side, and who is
/// right behind. It drives the radar overlay's bars, which are read out of the corner of the eye: a side lights up when
/// a car is there, and turns to the alert colour once the cars overlap. A car in line ahead is left out (it is on screen).
/// </summary>
public static class Spotter
{
    /// <summary>Length of a car, m: two cars whose centres are closer than this along the track overlap.</summary>
    public const float CarLength = 5.4f;

    /// <summary>Metres ahead of and behind the player a side lane covers.</summary>
    public const float LaneRange = 12f;

    /// <summary>A car whose centre line is within this of the player's is in line with it, not beside it, m.</summary>
    public const float InLine = 1.0f;

    /// <summary>Cars further to the side than this are left out (another line of the track), m.</summary>
    public const float MaxLateral = 7f;

    /// <summary>An overlapping car within this to the side is an alert, m (a car's width is about 2).</summary>
    public const float AlongsideLateral = 4.5f;

    /// <summary>A car in line behind closer than this is an alert, m: about a metre and a half between the cars.</summary>
    public const float RearAlert = 7f;

    public static IReadOnlyList<SpotterMark> Marks(RadarFrame? frame)
    {
        if (frame is null || frame.Cars.Count == 0)
        {
            return [];
        }

        List<SpotterMark>? marks = null;
        foreach (var car in frame.Cars)
        {
            if (Mark(car) is { } mark)
            {
                (marks ??= []).Add(mark);
            }
        }

        return marks ?? (IReadOnlyList<SpotterMark>)[];
    }

    /// <summary>Where one car goes on the bars; null when it isn't worth a call.</summary>
    public static SpotterMark? Mark(RadarBlip car)
    {
        var lateral = MathF.Abs(car.RelativeX);
        if (MathF.Abs(car.RelativeZ) > LaneRange || lateral > MaxLateral)
        {
            return null;
        }

        if (lateral < InLine)
        {
            return car.RelativeZ < 0 ? new SpotterMark(SpotterZone.Behind, car.RelativeX, -car.RelativeZ, -car.RelativeZ < RearAlert) : null;
        }

        var overlap = MathF.Abs(car.RelativeZ) < CarLength;
        return new SpotterMark(car.RelativeX < 0 ? SpotterZone.Left : SpotterZone.Right, car.RelativeZ, lateral, overlap && lateral <= AlongsideLateral);
    }
}
