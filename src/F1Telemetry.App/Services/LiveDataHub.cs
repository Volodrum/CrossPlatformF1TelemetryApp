using System.Collections.Concurrent;
using Avalonia.Threading;
using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Models;
using F1Telemetry.Core.Recording;
using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.App.Services;

/// <summary>
/// Bridge between the 60 Hz ingest thread and the UI thread. High-rate state (radar, strategy, samples) is
/// kept as "latest value" and published by a 20 Hz UI timer; discrete events (delta, lap completed,
/// session / recording changes) are posted to the UI thread immediately. This replaces the old
/// 500 ms database polling.
/// </summary>
public sealed class LiveDataHub : IDisposable
{
    private readonly TelemetryRuntime _runtime;
    private readonly DispatcherTimer _timer;
    private readonly ConcurrentQueue<TelemetrySample> _samples = new();
    private volatile bool _dirty;

    public LiveDataHub(TelemetryRuntime runtime)
    {
        _runtime = runtime;
        var engine = runtime.Engine;
        // Straight from the ingest thread into the ring buffer: the input overlay reads it every frame.
        engine.InputsCaptured += Inputs.Add;
        engine.RadarUpdated += f => { Radar = f; _dirty = true; };
        engine.StrategyUpdated += s => { Strategy = s; _dirty = true; };
        engine.ConditionsUpdated += c => { Conditions = c; _dirty = true; };
        engine.FieldUpdated += f => { Field = f; _dirty = true; };
        engine.SectorBoxUpdated += b => { SectorBox = b; _dirty = true; };
        engine.DamageUpdated += d => { Damage = d; _dirty = true; };
        engine.DamageTaken += _ => Post(() => DamageTaken?.Invoke());
        engine.ButtonsPressed += b => Post(() => ButtonsPressed?.Invoke(b));
        engine.SampleCaptured += s =>
        {
            LastSample = s;
            _samples.Enqueue(s);
            while (_samples.Count > 20_000 && _samples.TryDequeue(out _))
            {
            }

            _dirty = true;
        };
        engine.LapTimingUpdated += t => Post(() =>
        {
            LapTiming = t;
            LapTimingUpdated?.Invoke(t);
        });
        engine.DeltaUpdated += d => Post(() => DeltaUpdated?.Invoke(d));
        engine.LapCompleted += lap => Post(() => LapCompleted?.Invoke(lap));
        engine.SessionChanged += (_, s) =>
        {
            // The previous session's field and sector box must not linger on the new session's overlays.
            Field = null;
            SectorBox = null;
            Damage = null;
            Post(() => SessionChanged?.Invoke(s));
        };
        engine.SessionInfoUpdated += s => Post(() => SessionChanged?.Invoke(s));
        runtime.Recorder.StateChanged += s => Post(() => RecordingChanged?.Invoke(s));

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) =>
        {
            if (_dirty)
            {
                _dirty = false;
                Tick?.Invoke();
            }
        });
        _timer.Start();
    }

    /// <summary>Recent pedal inputs for the input overlay (written at packet rate, bypasses the 20 Hz tick).</summary>
    public InputHistory Inputs { get; } = new();

    public RadarFrame? Radar { get; private set; }
    public StrategySnapshot? Strategy { get; private set; }
    public TrackConditions? Conditions { get; private set; }
    public FieldSnapshot? Field { get; private set; }
    public SectorBoxSnapshot? SectorBox { get; private set; }
    public CarDamage? Damage { get; private set; }

    /// <summary>Latest timing board (UI thread).</summary>
    public LapTimingSnapshot? LapTiming { get; private set; }
    public TelemetrySample? LastSample { get; private set; }
    public SessionInfo? Session => _runtime.Engine.Session;

    /// <summary>UI thread, ≤ 20 Hz, only when new high-rate data arrived.</summary>
    public event Action? Tick;

    public event Action<LapTimingSnapshot>? LapTimingUpdated;
    public event Action<DeltaUpdate>? DeltaUpdated;

    /// <summary>The player's car took new damage (UI thread).</summary>
    public event Action? DamageTaken;

    /// <summary>Wheel / pad buttons just pressed, as reported by the game (UI thread).</summary>
    public event Action<uint>? ButtonsPressed;
    public event Action<int>? LapCompleted;
    public event Action<SessionInfo>? SessionChanged;
    public event Action<RecordingState>? RecordingChanged;

    /// <summary>Takes all samples captured since the last call (UI thread consumer).</summary>
    public List<TelemetrySample> DrainSamples()
    {
        var list = new List<TelemetrySample>(_samples.Count);
        while (_samples.TryDequeue(out var sample))
        {
            list.Add(sample);
        }

        return list;
    }

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);

    public void Dispose() => _timer.Stop();
}
