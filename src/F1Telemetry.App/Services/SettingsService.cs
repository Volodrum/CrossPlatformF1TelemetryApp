using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using F1Telemetry.App.Infrastructure;
using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Models;
using F1Telemetry.Ingest;
using F1Telemetry.Protocol;
using Microsoft.Extensions.Logging;

namespace F1Telemetry.App.Services;

public enum OverlayVisibility
{
    Never,
    Session,
    Always,
}

public enum OverlayKind
{
    LapTiming,
    Delta,
    Radar,
    TrackConditions,
    Inputs,
    SectorBox,
    TimingTower,
    ErsPlan,
}

public sealed class OverlaySettings
{
    public const double MinScale = 0.5;
    public const double MaxScale = 2.5;
    public const double MinOpacity = 0.2;

    public OverlayVisibility Mode { get; set; } = OverlayVisibility.Session;

    /// <summary>Size multiplier for the whole overlay (1 = design size).</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>Opacity of the overlay content, 0.2–1.</summary>
    public double Opacity { get; set; } = 1.0;

    public int? X { get; set; }
    public int? Y { get; set; }
}

public sealed class AppSettings
{
    /// <summary>F1 25 or F1 26 (2026 Season Pack). Must match the game's Settings → Telemetry → UDP Format.</summary>
    public GameFormat TelemetryMode { get; set; } = GameFormat.F1_25;

    public int UdpPort { get; set; } = UdpPacketSource.DefaultPort;
    public bool ListenOnStartup { get; set; } = true;
    public string StartStopHotkey { get; set; } = "Ctrl+Alt+S";
    public string ToggleOverlaysHotkey { get; set; } = "Ctrl+Alt+O";

    /// <summary>Switches the conditions &amp; strategy overlay between its strategy and car damage pages.</summary>
    public string StrategyPageHotkey { get; set; } = "Ctrl+Alt+D";

    /// <summary>
    /// Wheel / pad button for the same switch: the game's "UDP Action" number (1–12) the player has bound to a button,
    /// or 0 for none. Works on every OS (it arrives with the telemetry, not through a keyboard hook).
    /// </summary>
    public int StrategyPageUdpAction { get; set; }

    /// <summary>Steps the ERS PLAN overlay through race (normal, attack, recover) and qualifying plans.</summary>
    public string ErsPlanHotkey { get; set; } = "Ctrl+Alt+E";

    /// <summary>Controller buttons read directly from the pad (DualSense, Xbox, …), e.g. "Touchpad+DPadRight". Empty = none.</summary>
    public string StartStopPadButton { get; set; } = "";
    public string ToggleOverlaysPadButton { get; set; } = "";
    public string StrategyPagePadButton { get; set; } = "";
    public string ErsPlanPadButton { get; set; } = "";

    /// <summary>Charge the ERS PLAN overlay's race plans never deploy below, MJ.</summary>
    public double ErsReserveMj { get; set; } = 0.5;

    /// <summary>What the sector box's gap chips compare against.</summary>
    public SectorReference SectorBoxReference { get; set; } = SectorReference.SessionBest;

    /// <summary>Race gap column of the timing tower.</summary>
    public TowerGapMode TowerGap { get; set; } = TowerGapMode.GapToMe;

    /// <summary>Jump to the damage page when the car takes new damage (tyre wear is ignored).</summary>
    public bool DamagePopup { get; set; } = true;

    /// <summary>How long the damage pop-up stays before the overlay returns to the page it was on.</summary>
    public double DamagePopupSeconds { get; set; } = 8;
    public double TyreWearLimitPercent { get; set; } = 75;

    /// <summary>How many seconds of pedal history the input overlay shows.</summary>
    public double InputTraceSeconds { get; set; } = 6;

    public bool CaptureRawPackets { get; set; }
    public Dictionary<OverlayKind, OverlaySettings> Overlays { get; set; } = Enum.GetValues<OverlayKind>().ToDictionary(k => k, _ => new OverlaySettings());

    public OverlaySettings Overlay(OverlayKind kind)
    {
        if (!Overlays.TryGetValue(kind, out var settings))
        {
            Overlays[kind] = settings = new OverlaySettings();
        }

        return settings;
    }
}

/// <summary>JSON-file settings (single source of truth; the old app kept them in both DuckDB and JSON).</summary>
public sealed class SettingsService(AppPaths paths, ILogger<SettingsService> log)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public AppSettings Current { get; private set; } = new();

    private DispatcherTimer? _saveTimer;

    public event Action<AppSettings>? Changed;

    /// <summary>Saves shortly after the last call (for sliders that change many times per second). UI thread.</summary>
    public void SaveSoon()
    {
        _saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
        {
            _saveTimer!.Stop();
            Save();
        });
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(paths.SettingsPath))
            {
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(paths.SettingsPath), Json) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            log.LogWarning(ex, "Settings file unreadable, using defaults");
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            var temp = paths.SettingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, Json));
            File.Move(temp, paths.SettingsPath, overwrite: true);
        }
        catch (IOException ex)
        {
            log.LogError(ex, "Failed to save settings");
        }

        Changed?.Invoke(Current);
    }
}
