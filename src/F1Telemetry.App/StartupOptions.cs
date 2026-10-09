using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using F1Telemetry.App.Services;
using F1Telemetry.App.ViewModels;
using F1Telemetry.Protocol;

namespace F1Telemetry.App;

/// <summary>
/// Command-line switches:
/// <c>--mode f1-25|f1-26</c>, <c>--source udp|demo|replay=&lt;file&gt;</c>, <c>--record</c>, <c>--tab live|laps|lapdetail|strategy|energy|compare|setups|settings</c>
/// (<c>settings</c> opens the settings page; <c>position</c> still works and opens strategy, where the position chart now lives),
/// <c>--select-latest</c> (open the newest recording), <c>--lap &lt;n&gt;</c> (open that lap of it),
/// <c>--preview-overlays</c> (with <c>--tab settings</c>: overlay preview on), <c>--exit-after &lt;seconds&gt;</c> (unattended smoke
/// tests; exits cleanly so the store is flushed).
/// </summary>
public sealed record StartupOptions(GameFormat? Mode, SourceKind? Source, string? ReplayPath, bool Record, MainTab? Tab, double? ExitAfterSeconds, bool SelectLatest, int? OpenLap, bool PreviewOverlays = false, bool OpenSettings = false)
{
    public static StartupOptions Parse(string[] args)
    {
        GameFormat? mode = null;
        SourceKind? source = null;
        string? replay = null;
        MainTab? tab = null;
        double? exitAfter = null;
        var record = false;
        var selectLatest = false;
        int? openLap = null;
        var previewOverlays = false;
        var openSettings = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (arg)
            {
                case "--source":
                    var value = Next() ?? "";
                    (source, replay) = value.ToLowerInvariant() switch
                    {
                        "udp" => (SourceKind.Udp, null),
                        "demo" => (SourceKind.Demo, null),
                        _ when value.StartsWith("replay=", StringComparison.OrdinalIgnoreCase) => (SourceKind.Replay, value[7..]),
                        _ => ((SourceKind?)null, (string?)null),
                    };
                    break;
                case "--mode":
                    mode = (Next() ?? "").Replace("-", "").Replace("_", "").ToLowerInvariant() switch
                    {
                        "f125" or "25" or "2025" => GameFormat.F1_25,
                        "f126" or "26" or "2026" => GameFormat.F1_26,
                        _ => null,
                    };
                    break;
                case "--record":
                    record = true;
                    break;
                case "--tab":
                    var name = Next() ?? "";
                    openSettings = name.Equals("settings", StringComparison.OrdinalIgnoreCase);
                    tab = name.Equals("position", StringComparison.OrdinalIgnoreCase) ? MainTab.Strategy
                        : Enum.TryParse<MainTab>(name, ignoreCase: true, out var t) ? t : null;
                    break;
                case "--preview-overlays":
                    previewOverlays = true;
                    break;
                case "--select-latest":
                    selectLatest = true;
                    break;
                case "--lap":
                    openLap = int.TryParse(Next(), out var n) ? n : null;
                    selectLatest = true;
                    break;
                case "--exit-after":
                    exitAfter = double.TryParse(Next(), System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : null;
                    break;
            }
        }

        return new StartupOptions(mode, source, replay, record, tab, exitAfter, selectLatest, openLap, previewOverlays, openSettings);
    }

    public async Task ApplyAsync(TelemetryRuntime runtime, MainWindowViewModel viewModel, IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (Mode is { } mode)
        {
            await runtime.SetModeAsync(mode);
        }

        if (Source is { } source)
        {
            await runtime.StartSourceAsync(source, ReplayPath);
        }

        if (Record)
        {
            // Give the source a moment to deliver the session packet so the recording is tagged with the track.
            await Task.Delay(TimeSpan.FromSeconds(1));
            runtime.Recorder.Start("Started from command line");
        }

        if (SelectLatest && viewModel.Recordings.Count > 0)
        {
            viewModel.SelectedRecording = viewModel.Recordings[0];
            await viewModel.Session.RefreshAsync();
            if (OpenLap is { } lapNumber && viewModel.Session.Laps.FirstOrDefault(l => l.Lap.LapNumber == lapNumber) is { } lap)
            {
                viewModel.Session.SelectedLap = lap;
            }
        }

        if (Tab is { } tab)
        {
            viewModel.SelectedTab = tab;
        }

        if (OpenSettings)
        {
            viewModel.IsSettingsOpen = true;
            viewModel.Settings.PreviewOverlays = PreviewOverlays;
        }

        if (ExitAfterSeconds is { } seconds)
        {
            DispatcherTimer.RunOnce(() => desktop.Shutdown(), TimeSpan.FromSeconds(seconds));
        }
    }
}
