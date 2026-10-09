namespace F1Telemetry.Protocol.Lookups;

public sealed record TrackInfo(sbyte Id, string Name, string? MapFile);

/// <summary>Track ids from the UDP spec, with the matching track-outline asset (if one ships with the app).</summary>
public static class Tracks
{
    private static readonly Dictionary<sbyte, TrackInfo> ById = new TrackInfo[]
    {
        new(0, "Melbourne", "Melbourne"),
        new(1, "Paul Ricard", "Paul Ricard"),
        new(2, "Shanghai", "Shanghai"),
        new(3, "Sakhir", "Bahrain"),
        new(4, "Catalunya", "Catalunya"),
        new(5, "Monaco", "Monaco"),
        new(6, "Montreal", "Montreal"),
        new(7, "Silverstone", "Silverstone"),
        new(8, "Hockenheim", null),
        new(9, "Hungaroring", "Hungaroring"),
        new(10, "Spa", "Spa"),
        new(11, "Monza", "Monza"),
        new(12, "Singapore", "Singapore"),
        new(13, "Suzuka", "Suzuka"),
        new(14, "Abu Dhabi", "Abu Dhabi"),
        new(15, "Texas", "Texas"),
        new(16, "Brazil", "Interlagos"),
        new(17, "Austria", "Austria"),
        new(18, "Sochi", "Sochi"),
        new(19, "Mexico", "Mexico"),
        new(20, "Baku", "Baku"),
        new(21, "Sakhir Short", null),
        new(22, "Silverstone Short", null),
        new(23, "Texas Short", null),
        new(24, "Suzuka Short", null),
        new(25, "Hanoi", null),
        new(26, "Zandvoort", "Zandvoort"),
        new(27, "Imola", "Imola"),
        new(28, "Portimao", "Portimao"),
        new(29, "Jeddah", "Jeddah"),
        new(30, "Miami", "Miami"),
        new(31, "Las Vegas", "Las Vegas"),
        new(32, "Losail", "Losail"),
        new(39, "Silverstone (Reverse)", "Silverstone (reverse)"),
        new(40, "Austria (Reverse)", "Austria (reverse)"),
        new(41, "Zandvoort (Reverse)", "Zandvoort (reverse)"),
        new(42, "Madring", "Madring"),
    }.ToDictionary(t => t.Id);

    public static TrackInfo Get(sbyte id) => ById.TryGetValue(id, out var info) ? info : new TrackInfo(id, $"Track {id}", null);

    public static IEnumerable<TrackInfo> All => ById.Values;
}
