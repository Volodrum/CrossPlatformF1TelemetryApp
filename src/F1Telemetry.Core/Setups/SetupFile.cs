using System.Text.Json;
using System.Text.Json.Serialization;
using F1Telemetry.Protocol;

namespace F1Telemetry.Core.Setups;

/// <summary>
/// The <c>.f1setups</c> file: the setups someone chose to export, as JSON. Each one keeps its name, notes, game
/// format, track and every setting by <see cref="SetupSetting.Key"/>; best lap and laps are there for whoever reads
/// the file and are not imported.
/// </summary>
public static class SetupFile
{
    public const string Extension = ".f1setups";
    public const int Schema = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>What an exported setup says about how it went, for whoever reads the file.</summary>
    public sealed record Stats(uint? BestLapMs, int Laps);

    public static string Write(IEnumerable<(SetupEntry Setup, string Name, Stats? Stats)> setups, DateTimeOffset exported) =>
        JsonSerializer.Serialize(new FileDto
        {
            Schema = Schema,
            Exported = exported,
            Setups =
            [
                .. setups.Select(s => new SetupDto
                {
                    Name = s.Name,
                    Notes = string.IsNullOrEmpty(s.Setup.Notes) ? null : s.Setup.Notes,
                    Format = (int)s.Setup.Format,
                    TrackId = s.Setup.TrackId,
                    Track = s.Setup.TrackName,
                    Values = new Dictionary<string, double>(SetupSettings.ToValues(s.Setup.Setup)),
                    BestLapMs = s.Stats?.BestLapMs is > 0 ? s.Stats.BestLapMs : null,
                    Laps = s.Stats?.Laps is > 0 ? s.Stats.Laps : null,
                }),
            ],
        }, Json);

    /// <summary>The setups in a file. Throws <see cref="FormatException"/> when it isn't a setup file this version can read.</summary>
    public static IReadOnlyList<NewSetup> Read(string json)
    {
        FileDto? file;
        try
        {
            file = JsonSerializer.Deserialize<FileDto>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("This is not a setup file.", ex);
        }

        if (file is null || file.Schema is not > 0 || file.Setups is null)
        {
            throw new FormatException("This is not a setup file.");
        }

        if (file.Schema > Schema)
        {
            throw new FormatException("This setup file was written by a newer version of the app.");
        }

        return
        [
            .. file.Setups.Select((s, i) =>
            {
                if (!FormatLayout.TryGet((ushort)s.Format, out _) || s.TrackId < 0 || s.Values is null)
                {
                    throw new FormatException($"Setup {i + 1} has no game format, track or settings.");
                }

                return new NewSetup((GameFormat)s.Format, s.TrackId, s.Track ?? "Unknown", s.Name ?? "", s.Notes ?? "", SetupSettings.FromValues(s.Values));
            }),
        ];
    }

    private sealed class FileDto
    {
        public int Schema { get; set; }
        public DateTimeOffset Exported { get; set; }
        public List<SetupDto>? Setups { get; set; }
    }

    private sealed class SetupDto
    {
        public string? Name { get; set; }
        public string? Notes { get; set; }
        public int Format { get; set; }
        public int TrackId { get; set; } = -1;
        public string? Track { get; set; }
        public Dictionary<string, double>? Values { get; set; }
        public uint? BestLapMs { get; set; }
        public int? Laps { get; set; }
    }
}
