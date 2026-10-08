using F1Telemetry.Core.Energy;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Lookups;
using Microsoft.Extensions.Logging;

namespace F1Telemetry.App.Services;

/// <summary>
/// The plan behind the live ERS PLAN overlay. When a session at a track starts, it learns the car model from every lap
/// recorded there and takes the fastest clean lap with power data as the reference; then it plans every lap at the line
/// from the battery the car actually has. Qualifying sessions and time trial plan qualifying laps, everything else race
/// laps; <see cref="NextMode"/> steps through race (normal, attack, recover) and qualifying. UI thread.
/// </summary>
public sealed class ErsPlanService
{
    private static readonly (PlanKind Kind, RaceStance Stance)[] Modes =
    [
        (PlanKind.Race, RaceStance.Normal),
        (PlanKind.Race, RaceStance.Attack),
        (PlanKind.Race, RaceStance.Recover),
        (PlanKind.Qualifying, RaceStance.Normal),
    ];

    private readonly TelemetryRuntime _runtime;
    private readonly LiveDataHub _hub;
    private readonly ILogger _log;
    private (GameFormat Format, int TrackId)? _track;
    private int _sessionType = -1;
    private ErsPlanner? _planner;
    private double _steadyStart;
    private int _version;

    public ErsPlanService(TelemetryRuntime runtime, LiveDataHub hub, ILogger<ErsPlanService> log)
    {
        _runtime = runtime;
        _hub = hub;
        _log = log;
        hub.SessionChanged += OnSession;
        hub.LapCompleted += lap => _ = ReplanAsync();
    }

    public PlanKind Kind { get; private set; } = PlanKind.Race;
    public RaceStance Stance { get; private set; } = RaceStance.Normal;

    /// <summary>This lap's plan; null until a reference lap and car model are ready.</summary>
    public LapPlan? Plan { get; private set; }

    /// <summary>Why there is no plan, or what the plan is based on.</summary>
    public string Status { get; private set; } = "Waiting for a session.";

    /// <summary>The plan, mode or status changed.</summary>
    public event Action? Changed;

    /// <summary>Hotkey / controller: race normal → attack → recover → qualifying → race normal.</summary>
    public void NextMode()
    {
        var index = Array.IndexOf(Modes, (Kind, Stance));
        (Kind, Stance) = Modes[(index + 1) % Modes.Length];
        Changed?.Invoke();
        _ = ReplanAsync();
    }

    private void OnSession(SessionInfo session)
    {
        if (session.TrackId < 0)
        {
            return;
        }

        if (session.SessionType != _sessionType)
        {
            _sessionType = session.SessionType;
            (Kind, Stance) = SessionTypes.IsQualifying(session.SessionType) || session.SessionType == SessionTypes.TimeTrial
                ? (PlanKind.Qualifying, RaceStance.Normal)
                : (PlanKind.Race, RaceStance.Normal);
            Changed?.Invoke();
        }

        var track = (session.Format, (int)session.TrackId);
        if (_track != track)
        {
            _track = track;
            _ = LoadAsync(track);
        }
        else
        {
            _ = ReplanAsync();
        }
    }

    private async Task LoadAsync((GameFormat Format, int TrackId) track)
    {
        var version = ++_version;
        _planner = null;
        Plan = null;
        SetStatus("Learning the car model for this track…");
        try
        {
            var store = _runtime.Store;
            await store.FlushAsync();
            var model = await Task.Run(async () => ErsModelBuilder.Build(track.Format, track.TrackId, await store.GetModelSamplesAsync(track.Format, track.TrackId)));
            if (version != _version)
            {
                return;
            }

            if (!model.HasPowerData || model.StraightLine.Count == 0 || model.Deploy.Curves.Count == 0)
            {
                SetStatus("No laps with ICE and MGU-K power at this track yet. Record a few laps here and the plan follows.");
                return;
            }

            var reference = await FindReferenceAsync(track);
            if (version != _version)
            {
                return;
            }

            if (reference is not { } found || LapSimulator.Create(found.Lap, model) is not { } simulator)
            {
                SetStatus("No clean lap with power data at this track yet.");
                return;
            }

            var planner = await Task.Run(() => new ErsPlanner(simulator));
            var steady = await Task.Run(() => planner.Plan(PlanKind.Race).Plan.StartStore);
            if (version != _version)
            {
                return;
            }

            (_planner, _steadyStart) = (planner, steady);
            Status = $"Based on {found.Label}.";
            await ReplanAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Live ERS plan failed");
            if (version == _version)
            {
                SetStatus($"Could not plan: {ex.Message}");
            }
        }
    }

    /// <summary>The fastest clean lap with power data at the track, over every recording there (newest first on a tie).</summary>
    private async Task<(LapProfile Lap, string Label)?> FindReferenceAsync((GameFormat Format, int TrackId) track)
    {
        var candidates = new List<(RecordingInfo Recording, LapRecord Lap)>();
        foreach (var recording in (await _runtime.Store.GetRecordingsAsync()).Where(r => r.Format == track.Format && r.TrackId == track.TrackId))
        {
            var laps = await _runtime.Analysis.GetClassifiedLapsAsync(recording);
            candidates.AddRange(laps.Where(l => l.HasTime && l.IsValid && l.LapType == LapType.Regular).Select(l => (recording, l)));
        }

        // The fastest few: an older lap may be faster but recorded before power was.
        foreach (var (recording, lap) in candidates.OrderBy(c => c.Lap.LapTimeMs).ThenByDescending(c => c.Recording.StartTime).Take(8))
        {
            var samples = await _runtime.Store.GetLapSamplesAsync(recording.Id, lap.LapNumber);
            if (LapProfile.From(samples, track.Format, lap.LapTimeMs / 1000.0) is { } profile)
            {
                return (profile, $"lap {lap.LapNumber} of #{recording.Id} ({Core.Formatting.TimeFormat.Lap(lap.LapTimeMs)})");
            }
        }

        return null;
    }

    /// <summary>Plans the lap now starting from the battery the car has (the line was just crossed).</summary>
    private async Task ReplanAsync()
    {
        if (_planner is not { } planner)
        {
            return;
        }

        var version = _version;
        var store = _hub.LastSample?.ErsStoreEnergy is > 0 and var live ? live : _steadyStart;
        var (kind, stance, steady) = (Kind, Stance, _steadyStart);
        var target = steady + stance switch
        {
            RaceStance.Attack => -ErsOptimizer.OneMj,
            RaceStance.Recover => ErsOptimizer.OneMj,
            _ => 0,
        };

        var plan = await Task.Run(() => kind == PlanKind.Qualifying
            ? planner.PlanFrom(PlanKind.Qualifying, store, 0)
            : planner.PlanFrom(PlanKind.Race, store, Math.Clamp(target, 0, LapSimulator.Capacity)));
        if (version == _version && _planner == planner)
        {
            Plan = plan;
            Changed?.Invoke();
        }
    }

    private void SetStatus(string status)
    {
        Status = status;
        Plan = null;
        Changed?.Invoke();
    }
}
