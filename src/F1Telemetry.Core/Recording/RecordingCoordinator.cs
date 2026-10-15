using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol.Lookups;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace F1Telemetry.Core.Recording;

public enum RecordingChange
{
    Started,
    Stopped,
    AutoSplit,
}

public sealed record RecordingState(RecordingChange Change, long? RecordingId);

/// <summary>
/// Persistence policy on top of <see cref="SessionEngine"/> events:
/// <list type="bullet">
/// <item>samples are stored only while recording, never on a formation lap, and only when session time
/// moves forward; a flashback (time going back) discards what was stored after the point it rewound to, so the
/// driving that replaces it is what gets kept;</item>
/// <item>outside races, driving that belongs to no lap is kept off the laps (see <see cref="LapRestarts"/>): the first
/// out-lap before the line is not stored, and when a lap number crosses the line again its samples so far move to lap 0;</item>
/// <item>a new game session during an active recording auto-splits it (or re-tags an empty recording);</item>
/// <item>lap summaries are upserted only when they actually change;</item>
/// <item>the player's setup is stored when a recording starts and whenever it changes.</item>
/// </list>
/// Engine events arrive on the ingest thread; all store calls here are non-blocking enqueues.
/// </summary>
public sealed class RecordingCoordinator : IDisposable
{
    /// <summary>Session time going back by at least this much (s) is a flashback; less is a late or repeated packet.</summary>
    public const double FlashbackSeconds = 0.25;

    private readonly SessionEngine _engine;
    private readonly ITelemetryStore _store;
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, LapRecord> _writtenLaps = [];

    private long? _recordingId;
    private string _description = "";
    private double _lastSampleTime = -1;
    private TelemetrySample? _lastSample;
    private bool _trackTagged;

    // Game session the active recording belongs to (0 = not known yet).
    private ulong _recordingSessionUid;

    public RecordingCoordinator(SessionEngine engine, ITelemetryStore store, ILogger<RecordingCoordinator>? log = null)
    {
        _engine = engine;
        _store = store;
        _log = log ?? NullLogger<RecordingCoordinator>.Instance;

        _engine.SessionChanged += OnSessionChanged;
        _engine.SessionInfoUpdated += OnSessionInfoUpdated;
        _engine.SampleCaptured += OnSample;
        _engine.LapsUpdated += OnLapsUpdated;
        _engine.SetupChanged += OnSetupChanged;
    }

    public event Action<RecordingState>? StateChanged;

    public long? ActiveRecordingId
    {
        get
        {
            lock (_gate)
            {
                return _recordingId;
            }
        }
    }

    public bool IsRecording => ActiveRecordingId is not null;

    public long Start(string description = "Manual Session")
    {
        long id;
        lock (_gate)
        {
            if (_recordingId is { } existing)
            {
                return existing;
            }

            id = BeginLocked(description);
        }

        StateChanged?.Invoke(new RecordingState(RecordingChange.Started, id));
        return id;
    }

    public void Stop()
    {
        long? id;
        lock (_gate)
        {
            id = _recordingId;
            if (id is null)
            {
                return;
            }

            _store.EndRecording(id.Value);
            _recordingId = null;
        }

        _log.LogInformation("Recording {Id} stopped", id);
        StateChanged?.Invoke(new RecordingState(RecordingChange.Stopped, id));
    }

    public void Toggle()
    {
        if (IsRecording)
        {
            Stop();
        }
        else
        {
            Start();
        }
    }

    private long BeginLocked(string description)
    {
        var session = _engine.Session;
        var id = _store.BeginRecording(new NewRecording(
            SessionUid: session?.SessionUid.ToString() ?? "0",
            Description: description,
            TrackId: session?.TrackId ?? -1,
            TrackName: session?.TrackName ?? "Unknown",
            SessionType: session?.SessionType ?? 0,
            Format: session?.Format ?? Protocol.GameFormat.F1_25));

        _recordingId = id;
        _description = description;
        _lastSampleTime = -1;
        _lastSample = null;
        _trackTagged = session is { TrackId: >= 0 };
        _recordingSessionUid = session?.SessionUid ?? 0;
        _writtenLaps.Clear();
        if (_engine.CurrentSetup is { } setup)
        {
            _store.AppendSetup(id, setup);
        }

        _log.LogInformation("Recording {Id} started ({Description})", id, description);
        return id;
    }

    private void OnSessionChanged(SessionInfo? previous, SessionInfo current)
    {
        RecordingState? state = null;
        lock (_gate)
        {
            // Only a genuinely different game session splits the recording (never a return to the same one).
            if (_recordingId is not { } id || current.SessionUid == 0 || current.SessionUid == _recordingSessionUid)
            {
                return;
            }

            if (previous is null || _recordingSessionUid == 0 || _lastSampleTime < 0)
            {
                // Nothing captured yet (or its session was unknown): keep the recording, just re-tag it. Its game format and
                // track were guessed or belong to the old session, so they come from this one (the track with its session packet).
                _store.UpdateRecording(id, sessionUid: current.SessionUid.ToString(), format: current.Format);
                _recordingSessionUid = current.SessionUid;
                _trackTagged = false;
                _writtenLaps.Clear();
                return;
            }

            _store.EndRecording(id);
            var description = _description.EndsWith("(Auto-Split)", StringComparison.Ordinal) ? _description : $"{_description} (Auto-Split)";
            var newId = BeginLocked(description);
            state = new RecordingState(RecordingChange.AutoSplit, newId);
            _log.LogInformation("Auto-split recording {Old} -> {New} on session change", id, newId);
        }

        if (state is not null)
        {
            StateChanged?.Invoke(state);
        }
    }

    private void OnSessionInfoUpdated(SessionInfo info)
    {
        lock (_gate)
        {
            if (_recordingId is not { } id)
            {
                return;
            }

            _store.UpdateRecording(id, sessionType: info.SessionType);
            if (!_trackTagged && info.TrackId >= 0)
            {
                _store.UpdateRecording(id, trackId: info.TrackId, trackName: info.TrackName);
                _trackTagged = true;
            }
        }
    }

    private void OnSample(TelemetrySample sample)
    {
        lock (_gate)
        {
            if (_recordingId is not { } id
                || sample.LapNumber <= 0
                || _engine.SafetyCarStatus == SafetyCarStatus.FormationLap)
            {
                return;
            }

            if (sample.SessionTime <= _lastSampleTime)
            {
                if (_lastSampleTime - sample.SessionTime < FlashbackSeconds)
                {
                    return; // a repeated or late packet
                }

                // Flashback: the game rewound to here, so what was stored from here on was driven in a timeline it has
                // undone. Drop it and record the driving that replaces it.
                _store.DiscardFrom(id, sample.SessionTime, sample.LapNumber);
                foreach (var undone in _writtenLaps.Keys.Where(lap => lap > sample.LapNumber).ToList())
                {
                    _writtenLaps.Remove(undone);
                }

                _lastSample = null;
                _log.LogInformation("Flashback in recording {Id}: {From:F1} s -> {To:F1} s", id, _lastSampleTime, sample.SessionTime);
            }

            if (LapRestarts.Applies(_engine.Session?.SessionType ?? 0))
            {
                if (sample.LapDistance < 0)
                {
                    return;
                }

                if (_lastSample is { } previous && LapRestarts.IsRestart(previous, sample))
                {
                    _store.RestartLap(id, sample.LapNumber, sample.SessionTime);
                }
            }

            _lastSampleTime = sample.SessionTime;
            _lastSample = sample;
            sample.RecordingId = id;
            _store.AppendSample(sample);
        }
    }

    private void OnSetupChanged(SetupChange setup)
    {
        lock (_gate)
        {
            if (_recordingId is { } id)
            {
                _store.AppendSetup(id, setup);
            }
        }
    }

    private void OnLapsUpdated(IReadOnlyList<LapRecord> laps)
    {
        lock (_gate)
        {
            if (_recordingId is not { } id)
            {
                return;
            }

            foreach (var lap in laps)
            {
                if (_writtenLaps.TryGetValue(lap.LapNumber, out var written) && written == lap)
                {
                    continue;
                }

                _writtenLaps[lap.LapNumber] = lap;
                _store.UpsertLap(id, lap);
            }
        }
    }

    public void Dispose()
    {
        _engine.SessionChanged -= OnSessionChanged;
        _engine.SessionInfoUpdated -= OnSessionInfoUpdated;
        _engine.SampleCaptured -= OnSample;
        _engine.LapsUpdated -= OnLapsUpdated;
        _engine.SetupChanged -= OnSetupChanged;
    }
}
