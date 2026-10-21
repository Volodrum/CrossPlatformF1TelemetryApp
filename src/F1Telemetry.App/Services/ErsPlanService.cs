using F1Telemetry.Core.Energy;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Lookups;
using Microsoft.Extensions.Logging;

namespace F1Telemetry.App.Services;

/// <summary>
/// The plan behind the live ERS PLAN overlay. It plans on a session's energy map, as the energy tab's ERS PLAN shows
/// it: races on the session's typical lap, qualifying on its fastest lap, with the car model learned from every lap at
/// the track. When sessions at the track are picked on the energy tab, it plans on their laps together, as the tab does.
/// Otherwise the session is the newest earlier one at the track with <see cref="MinLaps"/> typical laps, or else the
/// one being recorded once it has driven that many: the battery over a lap or two is a poor guide (out-laps, traffic,
/// a car model learned from too little). Until then the overlay waits, and the plan is re-learned after every lap while
/// it rests on the session being driven. Every lap at the line it plans from the battery the car actually has, and
/// entering every deploy zone it re-plans the rest of the lap from the battery and harvest the car has there: a lap
/// that went off plan gets the best plan from where it is, never a mode the battery can't pay for.
/// Qualifying sessions and time trial plan qualifying laps, everything else race laps; <see cref="NextMode"/> steps
/// through race (normal, attack, recover) and qualifying. UI thread.
/// </summary>
public sealed class ErsPlanService
{
    /// <summary>Typical laps a session needs before the overlay plans on it.</summary>
    public const int MinLaps = 3;

    // Planning on the session being driven, the plan is re-learned after every lap until it averages this many laps.
    private const int SettledLaps = 10;

    // Earlier sessions at the track looked at for one to plan on, newest first.
    private const int SessionsToTry = 8;

    // The time of the lap just completed reaches the store a moment after the line.
    private static readonly TimeSpan LapTimeDelay = TimeSpan.FromSeconds(2);

    // A zone starting this close to the line is planned at the line: the game's per-lap counters may not have reset yet.
    private const double ReplanFrom = 2 * LapProfile.Step;

    private static readonly (PlanKind Kind, RaceStance Stance)[] Modes =
    [
        (PlanKind.Race, RaceStance.Normal),
        (PlanKind.Race, RaceStance.Attack),
        (PlanKind.Race, RaceStance.Recover),
        (PlanKind.Qualifying, RaceStance.Normal),
    ];

    private readonly TelemetryRuntime _runtime;
    private readonly LiveDataHub _hub;
    private readonly SettingsService _settings;
    private readonly ILogger _log;
    private (GameFormat Format, int TrackId)? _track;
    private int _sessionType = -1;
    private ErsPlanner? _race;
    private ErsPlanner? _qualifying;
    private double _steadyStart;
    private int _version;
    private bool _loading;

    // What this lap's plan aims to cross the line with, J, and the zone it was last re-planned at (0: at the line).
    private double _target;
    private int _zone;

    // The plan rests on the session being driven (or on an earlier one with too few laps): re-learn after every lap.
    private bool _learning;

    public ErsPlanService(TelemetryRuntime runtime, LiveDataHub hub, SettingsService settings, ILogger<ErsPlanService> log)
    {
        _runtime = runtime;
        _hub = hub;
        _settings = settings;
        _log = log;
        hub.SessionChanged += OnSession;
        hub.LapCompleted += _ => OnLapCompleted();
        hub.Tick += OnTick;
    }

    public PlanKind Kind { get; private set; } = PlanKind.Race;
    public RaceStance Stance { get; private set; } = RaceStance.Normal;

    /// <summary>This lap's plan; null until a session to plan on and a car model are ready.</summary>
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
            _ = LoadAsync(track, fresh: true);
        }
        else
        {
            _ = ReplanAsync();
        }
    }

    /// <summary>The energy tab's sessions for a track changed: plan on them, if that is the track being driven.</summary>
    public void PoolChanged(GameFormat format, int trackId)
    {
        if (_track is { } track && track == (format, trackId) && !_loading)
        {
            _ = LoadAsync(track, fresh: false);
        }
    }

    /// <summary>At the line: plan the new lap, and learn again while the plan is still settling (or there is none yet).</summary>
    private void OnLapCompleted()
    {
        if (_track is { } track && (_race is null || _learning) && !_loading)
        {
            _ = LoadAsync(track, fresh: false);
        }

        _ = ReplanAsync();
    }

    /// <summary>
    /// Learns the car model and picks the session to plan on. A <paramref name="fresh"/> load (a new track) drops the
    /// plan first; otherwise the plan on show stays until a new one is ready.
    /// </summary>
    private async Task LoadAsync((GameFormat Format, int TrackId) track, bool fresh)
    {
        var version = ++_version;
        _loading = true;
        if (fresh)
        {
            (_race, _qualifying, _learning) = (null, null, false);
            SetStatus("Learning the car model for this track…");
        }

        try
        {
            if (!fresh)
            {
                await Task.Delay(LapTimeDelay);
            }

            var store = _runtime.Store;
            await store.FlushAsync();
            var model = await Task.Run(async () => ErsModelBuilder.Build(track.Format, track.TrackId, await store.GetModelSamplesAsync(track.Format, track.TrackId)));
            if (version != _version)
            {
                return;
            }

            if (!model.HasPowerData || model.StraightLine.Count == 0 || model.Deploy.Curves.Count == 0)
            {
                Waiting($"No laps with ICE and MGU-K power at this track yet. Record here: the plan starts after {MinLaps} laps.");
                return;
            }

            var (session, waiting) = await FindSessionAsync(track, model);
            if (version != _version)
            {
                return;
            }

            if (session is not { } found)
            {
                Waiting(waiting);
                return;
            }

            if (LapSimulator.Create(found.Inputs.Race, model, LapSimulator.RaceReserve) is not { } raceLap || LapSimulator.Create(found.Inputs.Qualifying, model) is not { } qualifyingLap)
            {
                Waiting("The laps at this track can't be simulated yet. Record a few more.");
                return;
            }

            var (race, qualifying, steady) = await Task.Run(() =>
            {
                var race = new ErsPlanner(raceLap);
                return (race, new ErsPlanner(qualifyingLap), race.Plan(PlanKind.Race).Plan.StartStore);
            });
            if (version != _version)
            {
                return;
            }

            (_race, _qualifying, _steadyStart, _learning) = (race, qualifying, steady, found.Learning);
            Status = $"Based on {found.Label}.";
            await ReplanAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Live ERS plan failed");
            if (version == _version)
            {
                Waiting($"Could not plan: {ex.Message}");
            }
        }
        finally
        {
            if (version == _version)
            {
                _loading = false;
            }
        }
    }

    /// <summary>
    /// The session to plan on: the newest earlier one at the track with <see cref="MinLaps"/> typical laps, else the
    /// one being recorded once it has that many, else the earlier one with the most. Null, with what the overlay is
    /// waiting for, when there is none.
    /// </summary>
    private async Task<((PlanInputs Inputs, string Label, bool Learning)? Session, string Waiting)> FindSessionAsync(
        (GameFormat Format, int TrackId) track, ErsCarModel model)
    {
        var live = _runtime.Recorder.ActiveRecordingId;
        var recordings = (await _runtime.Store.GetRecordingsAsync())
            .Where(r => r.Format == track.Format && r.TrackId == track.TrackId)
            .OrderByDescending(r => r.StartTime)
            .ToList();

        // Sessions picked on the energy tab come first: the overlay plans on the same laps the tab shows.
        var picked = _settings.Current.EnergyPool(track.Format, track.TrackId).ToHashSet();
        var pool = recordings.Where(r => picked.Contains(r.Id) && r.Id != live).ToList();
        if (pool.Count > 0)
        {
            var sessions = new List<PlanSession>();
            foreach (var recording in pool)
            {
                var laps = await _runtime.Analysis.GetClassifiedLapsAsync(recording);
                sessions.Add(new PlanSession(recording, SessionLaps.Representative(laps), SessionLaps.Fastest(laps)));
            }

            if ((await PlanInputs.LoadAsync(_runtime, sessions, model)).Inputs is { RaceLaps: >= MinLaps } pooled)
            {
                return ((pooled, $"{(pool.Count == 1 ? "1 session" : $"{pool.Count} sessions")} picked on the energy tab ({pooled.RaceLaps} typical laps)", false), "");
            }
        }

        (PlanInputs Inputs, RecordingInfo Recording)? fewer = null;
        foreach (var recording in recordings.Where(r => r.Id != live).Take(SessionsToTry))
        {
            if (await InputsAsync(recording, model) is not { } inputs)
            {
                continue;
            }

            if (inputs.RaceLaps >= MinLaps)
            {
                return ((inputs, Label(recording, inputs), false), "");
            }

            if (fewer is null || inputs.RaceLaps > fewer.Value.Inputs.RaceLaps)
            {
                fewer = (inputs, recording);
            }
        }

        var drivenLaps = 0;
        if (recordings.FirstOrDefault(r => r.Id == live) is { } current && await InputsAsync(current, model) is { } own)
        {
            drivenLaps = own.RaceLaps;
            if (own.RaceLaps >= MinLaps)
            {
                var settled = own.RaceLaps >= SettledLaps;
                return ((own, $"this session ({own.RaceLaps} typical laps{(settled ? "" : ", still learning")})", !settled), "");
            }
        }

        if (fewer is { } earlier)
        {
            return ((earlier.Inputs, Label(earlier.Recording, earlier.Inputs), true), "");
        }

        return (null, live is null
            ? $"No earlier session at this track to plan on. Record this one: the plan starts after {MinLaps} typical laps."
            : $"Learning the battery: {drivenLaps} of {MinLaps} typical laps in this session.");
    }

    /// <summary>What <paramref name="recording"/> gives the planner, as its energy tab plans it; null when it can't be planned.</summary>
    private async Task<PlanInputs?> InputsAsync(RecordingInfo recording, ErsCarModel model)
    {
        var laps = await _runtime.Analysis.GetClassifiedLapsAsync(recording);
        var (inputs, _) = await PlanInputs.LoadAsync(_runtime, recording, model, SessionLaps.Representative(laps), SessionLaps.Fastest(laps));
        return inputs;
    }

    private static string Label(RecordingInfo recording, PlanInputs inputs) =>
        $"{SessionTypes.Name(recording.SessionType)} #{recording.Id} ({(inputs.RaceLaps == 1 ? "1 typical lap" : $"{inputs.RaceLaps} typical laps")})";

    /// <summary>Plans the lap now starting from the battery the car has (the line was just crossed).</summary>
    private async Task ReplanAsync()
    {
        var kind = Kind;
        if ((kind == PlanKind.Qualifying ? _qualifying : _race) is not { } planner)
        {
            return;
        }

        var version = _version;
        var store = _hub.LastSample?.ErsStoreEnergy is > 0 and var live ? live : _steadyStart;
        var target = _steadyStart + Stance switch
        {
            RaceStance.Attack => -ErsOptimizer.OneMj,
            RaceStance.Recover => ErsOptimizer.OneMj,
            _ => 0,
        };

        var end = kind == PlanKind.Qualifying ? 0 : Math.Clamp(target, 0, LapSimulator.Capacity);
        var plan = await Task.Run(() => planner.PlanFrom(kind, store, end));
        if (version == _version && (Kind == PlanKind.Qualifying ? _qualifying : _race) == planner)
        {
            (Plan, _target, _zone) = (plan, end, 0);
            Changed?.Invoke();
        }
    }

    /// <summary>Entering a deploy zone: re-plan the rest of the lap from the battery and harvest the car really has.</summary>
    private void OnTick()
    {
        if (Plan is not { } plan || _hub.LastSample is not { } sample || (Kind == PlanKind.Qualifying ? _qualifying : _race) is not { } planner)
        {
            return;
        }

        var zone = plan.Zones.FirstOrDefault(z => sample.LapDistance >= z.From && sample.LapDistance < z.To);
        if (zone is null || zone.Number <= _zone || zone.From < ReplanFrom)
        {
            return;
        }

        _zone = zone.Number;
        _ = ContinueAsync(planner, plan, zone.Number, sample.ErsStoreEnergy, sample.ErsHarvestedMguk);
    }

    private async Task ContinueAsync(ErsPlanner planner, LapPlan plan, int zone, double store, double harvested)
    {
        var version = _version;
        var target = _target;
        try
        {
            var next = await Task.Run(() => planner.Continue(plan, zone, store, harvested, target));
            if (next is not null && version == _version && Plan == plan)
            {
                Plan = next;
                Changed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Live ERS re-plan failed");
        }
    }

    /// <summary>Shows why there is no plan, unless there already is one (a re-learn that came to nothing keeps it).</summary>
    private void Waiting(string status)
    {
        if (_race is null)
        {
            SetStatus(status);
        }
    }

    private void SetStatus(string status)
    {
        Status = status;
        Plan = null;
        Changed?.Invoke();
    }
}
