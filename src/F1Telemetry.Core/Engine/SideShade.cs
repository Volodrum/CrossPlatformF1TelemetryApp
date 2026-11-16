using F1Telemetry.Core.Models;

namespace F1Telemetry.Core.Engine;

/// <summary>How dark each side of the screen goes, 0 (clear) to 1 (as dark as it gets).</summary>
public readonly record struct SideShade(float Left, float Right)
{
    public static SideShade None => default;

    public bool Any => Left > 0 || Right > 0;
}

/// <summary>
/// The side darkening for a car alongside, as Gran Turismo does it: the edge of the screen on that side goes dark,
/// darker the closer the car. It is seen without looking away from the road, which is what a car beside you needs.
/// Only a car overlapping you (or about to) counts; one in line ahead or behind doesn't.
/// </summary>
public static class SideShadeCalculator
{
    /// <summary>Metres to the side from which a car starts to shade (about a car's width of space between you).</summary>
    public const float StartLateral = 5f;

    /// <summary>Metres to the side at which the shade is full (wheel to wheel).</summary>
    public const float FullLateral = 2.2f;

    /// <summary>A car whose centre line is within this of yours is in line with you, not beside you, m.</summary>
    public const float InLine = 1.0f;

    /// <summary>Metres along the track over which the cars overlap fully (a car's length).</summary>
    public const float Overlap = 5.4f;

    /// <summary>Metres along the track at which a car has dropped clear (or not yet arrived).</summary>
    public const float Clear = 8f;

    public static SideShade Compute(RadarFrame? frame)
    {
        if (frame is null)
        {
            return SideShade.None;
        }

        float left = 0, right = 0;
        foreach (var car in frame.Cars)
        {
            var lateral = MathF.Abs(car.RelativeX);
            if (lateral < InLine)
            {
                continue;
            }

            var amount = Ramp(lateral, StartLateral, FullLateral) * Ramp(MathF.Abs(car.RelativeZ), Clear, Overlap);
            if (car.RelativeX < 0)
            {
                left = MathF.Max(left, amount);
            }
            else
            {
                right = MathF.Max(right, amount);
            }
        }

        return new SideShade(left, right);
    }

    /// <summary>0 at <paramref name="zero"/> and beyond, 1 at <paramref name="one"/> and closer, smooth between.</summary>
    private static float Ramp(float value, float zero, float one)
    {
        var t = Math.Clamp((zero - value) / (zero - one), 0f, 1f);
        return t * t * (3 - 2 * t);
    }
}
