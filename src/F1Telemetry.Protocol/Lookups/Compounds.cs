namespace F1Telemetry.Protocol.Lookups;

public enum VisualCompound
{
    Unknown,
    Soft,
    Medium,
    Hard,
    Inter,
    Wet,
}

public static class Compounds
{
    /// <summary>Visual compound (what the driver sees): drives stint colouring.</summary>
    public static VisualCompound Visual(byte id) => id switch
    {
        16 or 19 or 20 => VisualCompound.Soft, // F1 soft, F2 super soft / soft
        17 or 21 => VisualCompound.Medium,
        18 or 22 => VisualCompound.Hard,
        7 => VisualCompound.Inter,
        8 or 15 => VisualCompound.Wet,
        _ => VisualCompound.Unknown,
    };

    /// <summary>Actual compound (C0–C6 etc.).</summary>
    public static string Actual(byte id) => id switch
    {
        16 => "C5",
        17 => "C4",
        18 => "C3",
        19 => "C2",
        20 => "C1",
        21 => "C0",
        22 => "C6",
        7 => "Inter",
        8 => "Wet",
        9 => "Dry (Classic)",
        10 => "Wet (Classic)",
        11 => "Super Soft (F2)",
        12 => "Soft (F2)",
        13 => "Medium (F2)",
        14 => "Hard (F2)",
        15 => "Wet (F2)",
        _ => "Unknown",
    };
}

public static class WeatherTypes
{
    public static string Name(byte weather) => weather switch
    {
        0 => "Clear",
        1 => "Light Cloud",
        2 => "Overcast",
        3 => "Light Rain",
        4 => "Heavy Rain",
        5 => "Storm",
        _ => "Unknown",
    };
}
