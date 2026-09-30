namespace F1Telemetry.App.Infrastructure;

/// <summary>
/// Per-user data locations, resolved by .NET to the platform convention:
/// Windows %LOCALAPPDATA%\F1Telemetry, macOS ~/Library/Application Support/F1Telemetry, Linux ~/.local/share/F1Telemetry.
/// Override with the F1TELEMETRY_DATA_DIR environment variable (portable installs, tests).
/// </summary>
public sealed class AppPaths
{
    public AppPaths()
    {
        DataDirectory = Environment.GetEnvironmentVariable("F1TELEMETRY_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "F1Telemetry");
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(CapturesDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    public string DataDirectory { get; }
    public string DatabasePath => Path.Combine(DataDirectory, "telemetry.duckdb");
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string CapturesDirectory => Path.Combine(DataDirectory, "captures");
    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
    public string LockFilePath => Path.Combine(DataDirectory, "instance.lock");
    public string TrackMapsDirectory => Path.Combine(AppContext.BaseDirectory, "track_maps");
    public string TracksDirectory => Path.Combine(AppContext.BaseDirectory, "tracks");
}
