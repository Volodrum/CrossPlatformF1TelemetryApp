namespace F1Telemetry.Protocol.Lookups;

/// <summary>Team ids from the UDP spec (2025 base teams, the 2024 set and the 2026 Season Pack set) and their colours.</summary>
public static class Teams
{
    private static readonly Dictionary<ushort, (string Name, uint Rgb)> Known = new()
    {
        [0] = ("Mercedes", 0x27F4D2),
        [1] = ("Ferrari", 0xE8002D),
        [2] = ("Red Bull Racing", 0x3671C6),
        [3] = ("Williams", 0x64C4FF),
        [4] = ("Aston Martin", 0x229971),
        [5] = ("Alpine", 0x0093CC),
        [6] = ("Racing Bulls", 0x6692FF),
        [7] = ("Haas", 0xB6BABD),
        [8] = ("McLaren", 0xFF8000),
        [9] = ("Sauber", 0x52E252),
        [185] = ("Mercedes", 0x27F4D2),
        [186] = ("Ferrari", 0xE8002D),
        [187] = ("Red Bull Racing", 0x3671C6),
        [188] = ("Williams", 0x64C4FF),
        [189] = ("Aston Martin", 0x229971),
        [190] = ("Alpine", 0x0093CC),
        [191] = ("Racing Bulls", 0x6692FF),
        [192] = ("Haas", 0xB6BABD),
        [193] = ("McLaren", 0xFF8000),
        [194] = ("Sauber", 0x52E252),
        [476] = ("Mercedes", 0x27F4D2),
        [477] = ("Ferrari", 0xE8002D),
        [478] = ("Red Bull Racing", 0x3671C6),
        [479] = ("Williams", 0x64C4FF),
        [480] = ("Aston Martin", 0x229971),
        [481] = ("Alpine", 0x0093CC),
        [482] = ("Racing Bulls", 0x6692FF),
        [483] = ("Haas", 0xB6BABD),
        [484] = ("McLaren", 0xFF8000),
        [485] = ("Audi", 0xF50537),
        [486] = ("Cadillac", 0xC9CED6),
    };

    /// <summary>Team colour as 0xRRGGBB, or null for teams without a fixed colour (My Team, F2, custom).</summary>
    public static uint? Colour(ushort teamId) => Known.TryGetValue(teamId, out var team) ? team.Rgb : null;

    public static string Name(ushort teamId) => Known.TryGetValue(teamId, out var team) ? team.Name : $"Team {teamId}";
}
