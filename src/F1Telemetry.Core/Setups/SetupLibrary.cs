using F1Telemetry.Core.Models;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.Core.Setups;

public enum SetupSource
{
    /// <summary>Driven in a recorded session.</summary>
    Recorded,

    /// <summary>Read from an exported setup file.</summary>
    Imported,
}

/// <summary>
/// A setup in the library: one per distinct setup (fuel load aside) for a track and game format.
/// </summary>
/// <param name="Name">The name given to it; null until renamed (see <see cref="SetupLibrary.DisplayName"/>).</param>
public sealed record SetupEntry(
    long Id,
    GameFormat Format,
    int TrackId,
    string TrackName,
    string? Name,
    string Notes,
    bool Favourite,
    SetupSource Source,
    DateTimeOffset CreatedAt,
    CarSetup Setup);

/// <summary>
/// A stretch of a recording driven on a setup: from the lap it arrived on to the lap before the next change.
/// </summary>
/// <param name="Laps">Laps with a time in the stretch.</param>
/// <param name="BestLapMs">Fastest valid lap in the stretch; 0 when there is none.</param>
/// <param name="TopSpeed">Highest speed in the stretch, km/h.</param>
public sealed record SetupRun(
    long SetupId,
    long RecordingId,
    DateTimeOffset RecordingStart,
    int SessionType,
    int FromLap,
    int ToLap,
    SessionConditions? Conditions,
    int Laps,
    uint BestLapMs,
    int TopSpeed);

/// <summary>A setup to add to the library from a file.</summary>
public sealed record NewSetup(GameFormat Format, int TrackId, string TrackName, string Name, string Notes, CarSetup Setup);

/// <param name="Added">Setups that are new to the library (or were removed from it and are back).</param>
/// <param name="AlreadyThere">Setups the library already had, fuel load aside.</param>
public sealed record SetupImportResult(int Added, int AlreadyThere);

public enum WeatherBucket
{
    Dry,
    Wet,
    VeryWet,
}

/// <summary>Naming, versions and weather for the setup library.</summary>
public static class SetupLibrary
{
    /// <summary>Clear, light cloud and overcast are dry; light rain wet; heavy rain and storm very wet.</summary>
    public static WeatherBucket Bucket(byte weather) => weather switch
    {
        <= 2 => WeatherBucket.Dry,
        3 => WeatherBucket.Wet,
        _ => WeatherBucket.VeryWet,
    };

    public static string BucketName(WeatherBucket bucket) => bucket switch
    {
        WeatherBucket.Dry => "Dry",
        WeatherBucket.Wet => "Wet",
        _ => "Very wet",
    };

    /// <summary>
    /// Version of each setup within its track and format: 1 for the first one seen there, 2 for the next, and so on.
    /// </summary>
    public static IReadOnlyDictionary<long, int> Versions(IEnumerable<SetupEntry> setups) =>
        setups.GroupBy(s => (s.Format, s.TrackId))
            .SelectMany(g => g.OrderBy(s => s.CreatedAt).ThenBy(s => s.Id).Select((s, i) => (s.Id, Version: i + 1)))
            .ToDictionary(x => x.Id, x => x.Version);

    /// <summary>The name given to the setup, or "Spa · v3".</summary>
    public static string DisplayName(SetupEntry setup, int version) =>
        string.IsNullOrWhiteSpace(setup.Name) ? $"{setup.TrackName} · v{version}" : setup.Name.Trim();

    /// <summary>The setup before this one at the same track and format, to compare against by default.</summary>
    public static SetupEntry? Previous(SetupEntry setup, IEnumerable<SetupEntry> library) =>
        library.Where(s => s.Format == setup.Format && s.TrackId == setup.TrackId && s.Id != setup.Id
                           && (s.CreatedAt < setup.CreatedAt || (s.CreatedAt == setup.CreatedAt && s.Id < setup.Id)))
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
            .FirstOrDefault();
}
