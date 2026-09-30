using F1Telemetry.App.Infrastructure;
using F1Telemetry.Core;
using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Recording;
using F1Telemetry.Core.Tracks;
using F1Telemetry.Ingest;
using F1Telemetry.Protocol;
using F1Telemetry.Simulation;
using F1Telemetry.Storage;
using Microsoft.Extensions.Logging;

namespace F1Telemetry.App.Services;

public enum SourceKind
{
    Udp,
    Demo,
    Replay,
}

/// <summary>
/// Composition root for the non-UI runtime: store, engine, recording coordinator and ingest pipeline.
/// Switches between live UDP, the built-in simulator (demo) and <c>.f1rec</c> replays, and applies the
/// telemetry mode (F1 25 / F1 26) that decides which packet format is accepted.
/// </summary>
public sealed class TelemetryRuntime : IAsyncDisposable
{
    private readonly SettingsService _settings;
    private readonly AppPaths _paths;
    private readonly ILogger _log;

    public TelemetryRuntime(SettingsService settings, AppPaths paths, ILoggerFactory loggers)
    {
        _settings = settings;
        _paths = paths;
        _log = loggers.CreateLogger<TelemetryRuntime>();

        var strategy = new StrategyOptions { TyreWearLimitPercent = settings.Current.TyreWearLimitPercent };
        Store = new DuckDbTelemetryStore(paths.DatabasePath, loggers.CreateLogger<DuckDbTelemetryStore>());
        Engine = new SessionEngine(strategy, loggers.CreateLogger<SessionEngine>());
        Recorder = new RecordingCoordinator(Engine, Store, loggers.CreateLogger<RecordingCoordinator>());
        Statistics = new PacketStatistics();
        Pipeline = new TelemetryPipeline(Engine, Statistics, loggers.CreateLogger<TelemetryPipeline>());
        Analysis = new SessionAnalysisService(Store, strategy);
        Tracks = new TrackLibrary(paths.TrackMapsDirectory, paths.TracksDirectory);

        Recorder.StateChanged += OnRecordingStateChanged;
    }

    public ITelemetryStore Store { get; }
    public SessionEngine Engine { get; }
    public RecordingCoordinator Recorder { get; }
    public PacketStatistics Statistics { get; }
    public TelemetryPipeline Pipeline { get; }
    public SessionAnalysisService Analysis { get; }
    public TrackLibrary Tracks { get; }
    public SourceKind? ActiveSourceKind { get; private set; }

    public GameFormat Mode => _settings.Current.TelemetryMode;

    public event Action<GameFormat>? ModeChanged;

    /// <summary>Switches telemetry mode; a running demo restarts in the new format.</summary>
    public async Task SetModeAsync(GameFormat mode)
    {
        if (mode == Mode)
        {
            return;
        }

        _settings.Current.TelemetryMode = mode;
        _settings.Save();
        Pipeline.Mode = mode;
        _log.LogInformation("Telemetry mode set to {Mode}", mode);
        if (ActiveSourceKind == SourceKind.Demo)
        {
            await StartSourceAsync(SourceKind.Demo);
        }

        ModeChanged?.Invoke(mode);
    }

    public async Task InitializeAsync()
    {
        Pipeline.Mode = Mode;
        await Store.InitializeAsync();
        if (_settings.Current.ListenOnStartup)
        {
            await StartSourceAsync(SourceKind.Udp);
        }
    }

    public async Task StartSourceAsync(SourceKind kind, string? replayPath = null, double replaySpeed = 1.0)
    {
        IPacketSource source = kind switch
        {
            SourceKind.Udp => new UdpPacketSource(_settings.Current.UdpPort),
            SourceKind.Demo => new SimulatorPacketSource(() => DemoOptions(Mode)),
            SourceKind.Replay => new ReplayPacketSource(replayPath ?? throw new ArgumentNullException(nameof(replayPath)), replaySpeed),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        await Pipeline.StartAsync(source);
        ActiveSourceKind = kind;
    }

    public async Task StopSourceAsync()
    {
        await Pipeline.StopAsync();
        ActiveSourceKind = null;
    }

    private SimulationOptions DemoOptions(GameFormat format) => new()
    {
        Format = format,
        Track = Tracks.ForTrackId(11),
        TrackId = 11,
        Laps = 12,
        PitOnLap = 6,
        SessionUid = (ulong)Random.Shared.NextInt64(1, long.MaxValue),
        Seed = Random.Shared.Next(),
    };

    private void OnRecordingStateChanged(RecordingState state)
    {
        if (!_settings.Current.CaptureRawPackets)
        {
            return;
        }

        if (state.Change == RecordingChange.Stopped)
        {
            Pipeline.StopRawCapture();
        }
        else
        {
            var path = Path.Combine(_paths.CapturesDirectory, $"recording-{state.RecordingId}-{DateTime.Now:yyyyMMdd-HHmmss}{PacketFile.Extension}");
            Pipeline.StartRawCapture(path);
            _log.LogInformation("Raw capture → {Path}", path);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Recorder.Stop();
        await Pipeline.DisposeAsync();
        Recorder.Dispose();
        await Store.DisposeAsync();
    }
}
