using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using F1Telemetry.App.Services;
using F1Telemetry.Core.Games;
using F1Telemetry.Protocol;

namespace F1Telemetry.App.ViewModels;

/// <summary>
/// The toolbar's view of the telemetry feed: the session being received, the packet rate, the LIVE badge, and a
/// warning when the game sends a different UDP format from the mode the app is in.
/// </summary>
public sealed partial class ConnectionViewModel : ObservableObject
{
    /// <summary>The feed counts as live this long after the last telemetry arrived.</summary>
    private static readonly TimeSpan ReceivingFor = TimeSpan.FromSeconds(2);

    private readonly TelemetryRuntime _runtime;
    private readonly DispatcherTimer _statsTimer;
    private DateTime _lastDataAt = DateTime.MinValue;
    private long _lastTotal;
    private long _lastRejectedMode;
    private DateTime _lastStats = DateTime.UtcNow;

    public ConnectionViewModel(TelemetryRuntime runtime, LiveDataHub hub)
    {
        _runtime = runtime;
        hub.Tick += () => _lastDataAt = DateTime.UtcNow;
        hub.SessionChanged += s => Session = $"{s.TrackName} · {s.SessionTypeName} · {GameCatalog.FormatName(s.Format)}";

        // Independent of incoming data: rejected packets (wrong mode / size) never reach the engine.
        _statsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStats(DateTime.UtcNow));
        _statsTimer.Start();
    }

    [ObservableProperty] public partial string Session { get; set; } = "Waiting for telemetry…";
    [ObservableProperty] public partial bool IsReceiving { get; set; }
    [ObservableProperty] public partial string IngestStatus { get; set; } = "";
    [ObservableProperty] public partial string ModeWarning { get; set; } = "";
    [ObservableProperty] public partial bool HasModeWarning { get; set; }

    public void UpdateStats(DateTime now)
    {
        var stats = _runtime.Statistics;
        var rate = (stats.Total - _lastTotal) / Math.Max(0.001, (now - _lastStats).TotalSeconds);
        _lastTotal = stats.Total;
        _lastStats = now;
        IsReceiving = now - _lastDataAt < ReceivingFor;
        IngestStatus = $"{_runtime.Pipeline.ActiveSource?.Name ?? "No source"} · {rate:0} pkt/s · total {stats.Total:N0}"
                       + (stats.RejectedSize > 0 ? $" · {stats.RejectedSize:N0} wrong-size" : "")
                       + (stats.RejectedFormat > 0 ? $" · {stats.RejectedFormat:N0} unsupported format ({stats.LastUnsupportedFormat})" : "")
                       + (_runtime.Pipeline.IsCapturingRaw ? " · raw capture ON" : "");

        var mismatched = stats.RejectedMode - _lastRejectedMode;
        _lastRejectedMode = stats.RejectedMode;
        HasModeWarning = mismatched > 0;
        ModeWarning = mismatched > 0
            ? $"The game is sending {GameCatalog.FormatName(stats.LastMismatchedFormat ?? GameFormat.F1_25)} telemetry but the app is in {GameCatalog.FormatName(_runtime.Mode)} mode. Switch the mode in the toolbar."
            : "";
    }
}
