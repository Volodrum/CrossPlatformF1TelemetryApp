using System.Globalization;

namespace F1Telemetry.Core.Formatting;

public static class TimeFormat
{
    /// <summary>1:23.456, or "-" for zero.</summary>
    public static string Lap(double ms)
    {
        if (ms <= 0 || double.IsNaN(ms))
        {
            return "-";
        }

        var total = (long)Math.Round(ms);
        return string.Create(CultureInfo.InvariantCulture, $"{total / 60_000}:{total % 60_000 / 1000:00}.{total % 1000:000}");
    }

    /// <summary>23.456, or "-" for zero.</summary>
    public static string Sector(double ms) =>
        ms <= 0 ? "-" : (ms / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>+0.123 / -0.456.</summary>
    public static string Delta(double ms) =>
        (ms < 0 ? "-" : "+") + (Math.Abs(ms) / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>12:34 for session clocks.</summary>
    public static string Clock(double seconds) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)(seconds / 60)}:{(int)(seconds % 60):00}");
}
