using F1Telemetry.Core.Models;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Lookups;
using F1Telemetry.Protocol.Packets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace F1Telemetry.Core.Engine;

/// <summary>
/// Live state machine for the player's session. Consumes decoded packets (single-threaded, in arrival
/// order) and publishes typed events. It never touches storage or UI: persistence is handled by
/// <see cref="Recording.RecordingCoordinator"/>, presentation by whoever subscribes.
/// </summary>
public sealed class SessionEngine
{
    private const double TimingIntervalSeconds = 0.05;

    private readonly ILogger _log;
    private readonly LapTypeTracker _lapTypes = new();
    private readonly ConsumptionTracker _consumption;
    private readonly DeltaCalculator _delta = new();
    private readonly FieldTracker _field = new();
    private readonly SectorTimer _sectors = new();
    private readonly DamageMonitor _damageMonitor = new();
    private readonly Dictionary<int, int> _positionByLap = [];

    // Laps on which a new tyre stint began, seen live from the pit-stop counter (see TrackPitStops).
    private readonly List<int> _observedStintStarts = [];
    private int? _pitStops;

    private ulong? _sessionUid;
    private SessionData? _sessionData;
    private LapDataPacket? _lapData;
    private CarStatus? _status;
    private CarDamage? _damage;
    private CarMotion? _motion;
    private CarTelemetry2? _telemetry2;
    private int _currentLap;
    private bool _currentLapClean = true;
    private double _lastTimingEmit = double.NegativeInfinity;
    private bool _lastBoxFinished;
    private int? _lastBoxSplit;
    private uint _buttons;

    public SessionEngine(StrategyOptions? options = null, ILogger<SessionEngine>? log = null)
    {
        _consumption = new ConsumptionTracker(options ?? new StrategyOptions());
        _log = log ?? NullLogger<SessionEngine>.Instance;
    }

    /// <summary>Raised when the session UID changes (new session loaded in game). Args: previous, current.</summary>
    public event Action<SessionInfo?, SessionInfo>? SessionChanged;

    /// <summary>Raised when session identity details (track, type) become known or change.</summary>
    public event Action<SessionInfo>? SessionInfoUpdated;

    public event Action<TrackConditions>? ConditionsUpdated;
    public event Action<RadarFrame>? RadarUpdated;
    public event Action<StrategySnapshot>? StrategyUpdated;
    public event Action<TelemetrySample>? SampleCaptured;

    /// <summary>
    /// Player pedal inputs on every car-telemetry packet (ingest thread, before any other processing). Unlike
    /// <see cref="SampleCaptured"/> it needs no status/damage packets and allocates nothing: it feeds the input
    /// overlay, which must track the pedals with minimal latency.
    /// </summary>
    public event Action<InputSample>? InputsCaptured;

    /// <summary>All player laps with engine-resolved lap types; raised on every player history packet.</summary>
    public event Action<IReadOnlyList<LapRecord>>? LapsUpdated;

    public event Action<LapTimingSnapshot>? LapTimingUpdated;
    public event Action<DeltaUpdate>? DeltaUpdated;

    /// <summary>Raised when the player crosses the line. Arg: the lap number just completed.</summary>
    public event Action<int>? LapCompleted;

    public event Action<string>? GameEvent;

    /// <summary>
    /// Wheel / pad buttons that went down in a <c>BUTN</c> event (bit flags, see <see cref="EventPacket.UdpActionMask"/>).
    /// Only presses are reported: a button held down, or released, raises nothing.
    /// </summary>
    public event Action<uint>? ButtonsPressed;

    /// <summary>Every running car in position order (≤ 20 Hz, from lap-data packets).</summary>
    public event Action<FieldSnapshot>? FieldUpdated;

    /// <summary>The player's sector box (≤ 20 Hz, plus every sector split and lap completion).</summary>
    public event Action<SectorBoxSnapshot>? SectorBoxUpdated;

    /// <summary>Latest damage of the player's car, on every damage packet.</summary>
    public event Action<CarDamage>? DamageUpdated;

    /// <summary>The player's car took new bodywork / gearbox / engine damage or a new fault (see <see cref="DamageMonitor"/>).</summary>
    public event Action<CarDamage>? DamageTaken;

    public SessionInfo? Session { get; private set; }
    public int CurrentLap => _currentLap;
    public byte SafetyCarStatus => _sessionData?.SafetyCarStatus ?? 0;
    public StrategyOptions StrategyOptions => _consumption.Options;

    public void Process(Packet packet)
    {
        var header = packet.Header;

        // Session UID 0 means "no session": the game sends a burst of these while leaving a session for the menus.
        // Treating them as a session switch made real -> 0 -> same real session look like a new session, which
        // reset the engine and auto-split the recording into an empty one.
        if (header.SessionUid == 0)
        {
            return;
        }

        if (_sessionUid != header.SessionUid)
        {
            StartNewSession(header);
        }

        switch (packet)
        {
            case MotionPacket motion:
                OnMotion(motion);
                break;
            case SessionPacket session:
                OnSession(session);
                break;
            case LapDataPacket lapData:
                OnLapData(lapData);
                break;
            case CarStatusPacket status:
                _status = status.Player;
                _field.OnStatus(status);
                break;
            case CarDamagePacket damage:
                OnDamage(damage.Player);
                break;
            case ParticipantsPacket participants:
                _field.OnParticipants(participants);
                break;
            case CarTelemetry2Packet telemetry2:
                _telemetry2 = telemetry2.Player;
                break;
            case CarTelemetryPacket telemetry:
                OnCarTelemetry(telemetry);
                break;
            case SessionHistoryPacket history:
                _field.OnHistory(history);
                if (history.IsPlayer)
                {
                    OnPlayerHistory(history);
                }

                break;
            case EventPacket evt:
                GameEvent?.Invoke(evt.Code);
                if (evt.ButtonStatus is { } buttons)
                {
                    OnButtons(buttons);
                }

                break;
        }
    }

    private void StartNewSession(PacketHeader header)
    {
        var previous = Session;
        _sessionUid = header.SessionUid;
        _sessionData = null;
        _lapData = null;
        _status = null;
        _damage = null;
        _motion = null;
        _telemetry2 = null;
        _currentLap = 0;
        _currentLapClean = true;
        _positionByLap.Clear();
        _observedStintStarts.Clear();
        _pitStops = null;
        _lapTypes.Reset();
        _consumption.Reset();
        _delta.Reset();
        _field.Reset();
        _sectors.Reset();
        _damageMonitor.Reset();
        _lastTimingEmit = double.NegativeInfinity;
        _lastBoxFinished = false;
        _lastBoxSplit = null;
        _buttons = 0;

        Session = new SessionInfo(header.SessionUid, header.Format, -1, "Unknown", 0, SessionTypes.Name(0), "", 0, header.PlayerCarIndex);
        _log.LogInformation("New session {Uid} ({Format})", header.SessionUid, header.Format);
        SessionChanged?.Invoke(previous, Session);
    }

    private void OnMotion(MotionPacket motion)
    {
        _motion = motion.Player;
        RadarUpdated?.Invoke(RadarCalculator.Compute(motion, _lapData));
    }

    private void OnSession(SessionPacket packet)
    {
        var data = packet.Data;
        _sessionData = data;
        _field.OnSession(data);
        var track = Protocol.Lookups.Tracks.Get(data.TrackId);

        var info = Session! with
        {
            TrackId = data.TrackId,
            TrackName = track.Name,
            SessionType = data.SessionType,
            SessionTypeName = SessionTypes.Name(data.SessionType),
            FormulaName = Formulas.Name(data.Formula),
            TotalLaps = data.TotalLaps,
            PlayerCarIndex = packet.Header.PlayerCarIndex,
        };

        if (info != Session)
        {
            Session = info;
            SessionInfoUpdated?.Invoke(info);
        }

        if (_currentLap > 0)
        {
            _lapTypes.OnSafetyCar(_currentLap, data.SafetyCarStatus);
        }

        ConditionsUpdated?.Invoke(new TrackConditions(
            track.Name, data.Weather, data.TrackTemperature, data.AirTemperature, data.TotalLaps,
            data.SafetyCarStatus, data.WeatherForecast));
    }

    private void OnLapData(LapDataPacket packet)
    {
        _lapData = packet;
        _field.OnLapData(packet);
        var lap = packet.Player;
        if (lap.CurrentLapNum == 0)
        {
            return;
        }

        EmitTiming(packet.Header.SessionTime, lap);

        var lapNum = (int)lap.CurrentLapNum;
        _lapTypes.OnPitStatus(lapNum, lap.PitStatus);
        TrackPitStops(lapNum, lap);
        _consumption.EnsureStarted(_status, _damage);

        if (_currentLap != 0 && lapNum > _currentLap)
        {
            var ended = _currentLap;
            var countsTowardsAverages = _currentLapClean && _lapTypes.Get(ended) == LapType.Regular;
            _consumption.OnLapCompleted(CurrentCompound(), countsTowardsAverages, _status, _damage);
            _currentLapClean = true;
            _currentLap = lapNum;
            LapCompleted?.Invoke(ended);
        }

        _currentLap = lapNum;
        if (!IsCleanDrivingState(lap))
        {
            _currentLapClean = false;
        }

        _positionByLap[lapNum] = lap.CarPosition;
    }

    /// <summary>
    /// Records where each pit stop really happened. The pit-stop counter goes up as the car leaves its box, and the lap
    /// distance at that moment tells which lap the tyres changed on: late in the lap (box before the line) means this
    /// is the in-lap and the new stint starts next lap; early in the lap (box after the line) means this is already
    /// the out-lap. The game's stint history is not exact about this: it can end a stint a lap early, which used to
    /// turn the lap before the in-lap into a third PIT lap with the new compound.
    /// </summary>
    private void TrackPitStops(int lapNum, LapData lap)
    {
        if (_pitStops is { } previous && lap.NumPitStops > previous)
        {
            var length = _sessionData?.TrackLength ?? 0;
            var start = length > 0 && lap.LapDistance > length / 2f ? lapNum + 1 : lapNum;
            if (!_observedStintStarts.Contains(start))
            {
                _observedStintStarts.Add(start);
            }
        }
        else if (_pitStops is { } before && lap.NumPitStops < before)
        {
            _observedStintStarts.RemoveAll(start => start > lapNum); // flashback to before the stop
        }

        _pitStops = lap.NumPitStops;
    }

    /// <summary>
    /// Last lap of each stint (255 for the running one): the game's value, unless a stop was seen live within a lap or
    /// two of it, in which case that stop's in-lap.
    /// </summary>
    private int[] StintEndLaps(SessionHistoryPacket history)
    {
        var ends = new int[history.Stints.Length];
        for (var i = 0; i < ends.Length; i++)
        {
            int end = history.Stints[i].EndLap;
            if (end != TyreStint.Current)
            {
                var inLaps = _observedStintStarts.Select(start => start - 1).Where(inLap => inLap >= end - 1 && inLap <= end + 2);
                if (inLaps.OrderBy(inLap => Math.Abs(inLap - end)).FirstOrDefault() is > 0 and var observed)
                {
                    end = observed;
                }
            }

            ends[i] = end;
        }

        return ends;
    }

    private static int StintIndexForLap(int[] stintEnds, int lapNumber)
    {
        for (var i = 0; i < stintEnds.Length; i++)
        {
            if (stintEnds[i] == TyreStint.Current || lapNumber <= stintEnds[i])
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Field and sector box at most every 50 ms of session time, but always on the packet where the player crosses a
    /// split or the line, so the sector box never misses a split.
    /// </summary>
    private void EmitTiming(double time, LapData lap)
    {
        var box = _sectors.Update(lap, time, _field);
        var changed = box.IsFinished != _lastBoxFinished || box.LastSplit?.Sector != _lastBoxSplit;
        _lastBoxFinished = box.IsFinished;
        _lastBoxSplit = box.LastSplit?.Sector;
        if (!changed && time - _lastTimingEmit is >= 0 and < TimingIntervalSeconds)
        {
            return;
        }

        _lastTimingEmit = time;
        SectorBoxUpdated?.Invoke(box);
        FieldUpdated?.Invoke(_field.Snapshot());
    }

    private void OnButtons(uint status)
    {
        var pressed = status & ~_buttons;
        _buttons = status;
        if (pressed != 0)
        {
            ButtonsPressed?.Invoke(pressed);
        }
    }

    private void OnDamage(CarDamage damage)
    {
        _damage = damage;
        var isNew = _damageMonitor.Update(damage);
        DamageUpdated?.Invoke(damage);
        if (isNew)
        {
            DamageTaken?.Invoke(damage);
        }
    }

    private void OnCarTelemetry(CarTelemetryPacket packet)
    {
        var telemetry = packet.Player;
        InputsCaptured?.Invoke(new InputSample(packet.Header.SessionTime, telemetry.Throttle, telemetry.Brake));

        if (_status is { } status && _damage is { } damage)
        {
            StrategyUpdated?.Invoke(BuildStrategy(status, damage));
            SampleCaptured?.Invoke(BuildSample(packet.Header, telemetry, status, damage));
        }
    }

    private void OnPlayerHistory(SessionHistoryPacket history)
    {
        var isRace = SessionTypes.IsRace(Session?.SessionType ?? 0);

        // In races, stint boundaries are pit stops: in-lap = stint end lap, out-lap = next lap. This also covers stops
        // made before the app was listening; stops seen live correct the game's end laps (see TrackPitStops).
        // Recomputed on every packet (not stored in the sticky tracker), so a later correction also clears a lap.
        var stintEnds = StintEndLaps(history);
        var stintPitLaps = new HashSet<int>();
        if (isRace)
        {
            foreach (var end in stintEnds)
            {
                if (end is not TyreStint.Current and > 0)
                {
                    stintPitLaps.Add(end);
                    stintPitLaps.Add(end + 1);
                }
            }
        }

        var laps = new List<LapRecord>(history.Laps.Length);
        for (var i = 0; i < history.Laps.Length; i++)
        {
            var h = history.Laps[i];
            var lapNumber = i + 1;
            var stintIdx = StintIndexForLap(stintEnds, lapNumber);
            var stint = stintIdx >= 0 ? history.Stints[stintIdx] : default;

            laps.Add(new LapRecord
            {
                LapNumber = lapNumber,
                LapTimeMs = h.LapTimeMs,
                Sector1Ms = h.Sector1Ms,
                Sector2Ms = h.Sector2Ms,
                Sector3Ms = h.Sector3Ms,
                IsValid = h.IsLapValid,
                IsBestLap = history.BestLapNum == lapNumber,
                IsBestSector1 = history.BestSector1LapNum == lapNumber,
                IsBestSector2 = history.BestSector2LapNum == lapNumber,
                IsBestSector3 = history.BestSector3LapNum == lapNumber,
                Compound = stintIdx >= 0 ? Compounds.Visual(stint.VisualCompound).ToString() : "Unknown",
                ActualCompound = stintIdx >= 0 ? Compounds.Actual(stint.ActualCompound) : "Unknown",
                StintIndex = stintIdx,
                CarPosition = _positionByLap.TryGetValue(lapNumber, out var pos) ? pos : null,
                LapType = stintPitLaps.Contains(lapNumber) ? LapType.Pit : _lapTypes.Get(lapNumber),
            });
        }

        LapsUpdated?.Invoke(laps);

        var recent = laps.AsEnumerable().Reverse().Take(5).ToList();
        var best = laps.Where(l => l.HasTime).MinBy(l => l.LapTimeMs);
        LapTimingUpdated?.Invoke(new LapTimingSnapshot(recent, best));

        if (_delta.Update(laps) is { } delta)
        {
            DeltaUpdated?.Invoke(delta);
        }
    }

    private StrategySnapshot BuildStrategy(CarStatus status, CarDamage damage)
    {
        var compound = CurrentCompound();
        var wear = ConsumptionTracker.ToWheels(damage);
        var worst = Enumerable.Range(0, 4).MaxBy(i => wear[i]);
        var lap = _lapData?.Player;

        return new StrategySnapshot(
            Compound: compound,
            TyresAgeLaps: status.TyresAgeLaps,
            TyreWear: wear,
            HighestWearTyre: WheelValues.ShortNames[worst],
            HighestWear: wear[worst],
            TyreLapsRemaining: _consumption.TyreLapsRemaining(compound, wear),
            FuelInTank: status.FuelInTank,
            FuelLapsRemaining: _consumption.FuelLapsRemaining(compound, status.FuelInTank),
            GameFuelRemainingLaps: status.FuelRemainingLaps,
            AverageFuelPerLap: _consumption.AverageFuelPerLap(compound),
            PitStatus: lap?.PitStatus ?? PitStatus.None,
            DriverStatus: lap?.DriverStatus ?? DriverStatus.OnTrack,
            CurrentLapClean: _currentLapClean,
            CurrentLapInvalidated: lap?.CurrentLapInvalid ?? false);
    }

    private TelemetrySample BuildSample(PacketHeader header, CarTelemetry t, CarStatus s, CarDamage d)
    {
        var lap = _lapData?.Player;
        var m = _motion;
        return new TelemetrySample
        {
            LapNumber = _currentLap,
            SessionTime = header.SessionTime,
            LapDistance = lap?.LapDistance ?? 0,
            Position = lap?.CarPosition ?? 0,
            Speed = t.Speed,
            Throttle = t.Throttle,
            Steer = t.Steer,
            Brake = t.Brake,
            Clutch = t.Clutch,
            Gear = t.Gear,
            Rpm = t.EngineRpm,
            Drs = t.Drs ? 1 : 0,
            FuelInTank = s.FuelInTank,
            FuelRemainingLaps = s.FuelRemainingLaps,
            ErsStoreEnergy = s.ErsStoreEnergy,
            ErsDeployMode = s.ErsDeployMode,
            ErsHarvestedMguk = s.ErsHarvestedThisLapMguk,
            ErsHarvestedMguh = s.ErsHarvestedThisLapMguh,
            ErsDeployed = s.ErsDeployedThisLap,
            BrakesTempFl = t.BrakesTemperature.FrontLeft,
            BrakesTempFr = t.BrakesTemperature.FrontRight,
            BrakesTempRl = t.BrakesTemperature.RearLeft,
            BrakesTempRr = t.BrakesTemperature.RearRight,
            TyresSurfaceTempFl = t.TyresSurfaceTemperature.FrontLeft,
            TyresSurfaceTempFr = t.TyresSurfaceTemperature.FrontRight,
            TyresSurfaceTempRl = t.TyresSurfaceTemperature.RearLeft,
            TyresSurfaceTempRr = t.TyresSurfaceTemperature.RearRight,
            TyresInnerTempFl = t.TyresInnerTemperature.FrontLeft,
            TyresInnerTempFr = t.TyresInnerTemperature.FrontRight,
            TyresInnerTempRl = t.TyresInnerTemperature.RearLeft,
            TyresInnerTempRr = t.TyresInnerTemperature.RearRight,
            EngineTemp = t.EngineTemperature,
            TyresPressureFl = t.TyresPressure.FrontLeft,
            TyresPressureFr = t.TyresPressure.FrontRight,
            TyresPressureRl = t.TyresPressure.RearLeft,
            TyresPressureRr = t.TyresPressure.RearRight,
            ActualCompound = s.ActualTyreCompound,
            VisualCompound = s.VisualTyreCompound,
            TyresAgeLaps = s.TyresAgeLaps,
            TyreWearFl = d.TyresWear.FrontLeft,
            TyreWearFr = d.TyresWear.FrontRight,
            TyreWearRl = d.TyresWear.RearLeft,
            TyreWearRr = d.TyresWear.RearRight,
            TyreDamageFl = d.TyresDamage.FrontLeft,
            TyreDamageFr = d.TyresDamage.FrontRight,
            TyreDamageRl = d.TyresDamage.RearLeft,
            TyreDamageRr = d.TyresDamage.RearRight,
            TyreBlistersFl = d.TyreBlisters.FrontLeft,
            TyreBlistersFr = d.TyreBlisters.FrontRight,
            TyreBlistersRl = d.TyreBlisters.RearLeft,
            TyreBlistersRr = d.TyreBlisters.RearRight,
            BrakesDamageFl = d.BrakesDamage.FrontLeft,
            BrakesDamageFr = d.BrakesDamage.FrontRight,
            BrakesDamageRl = d.BrakesDamage.RearLeft,
            BrakesDamageRr = d.BrakesDamage.RearRight,
            FrontLeftWingDamage = d.FrontLeftWingDamage,
            FrontRightWingDamage = d.FrontRightWingDamage,
            RearWingDamage = d.RearWingDamage,
            FloorDamage = d.FloorDamage,
            DiffuserDamage = d.DiffuserDamage,
            SidepodDamage = d.SidepodDamage,
            GearBoxDamage = d.GearBoxDamage,
            EngineDamage = d.EngineDamage,
            EngineMguhWear = d.EngineMguhWear,
            EngineEsWear = d.EngineEsWear,
            EngineCeWear = d.EngineCeWear,
            EngineIceWear = d.EngineIceWear,
            EngineMgukWear = d.EngineMgukWear,
            EngineTcWear = d.EngineTcWear,
            WorldPosX = m?.WorldPositionX ?? 0,
            WorldPosY = m?.WorldPositionY ?? 0,
            WorldPosZ = m?.WorldPositionZ ?? 0,
            GForceLat = m?.GForceLateral ?? 0,
            GForceLon = m?.GForceLongitudinal ?? 0,
            GForceVert = m?.GForceVertical ?? 0,
            ActiveAeroMode = _telemetry2?.ActiveAeroMode ?? 0,
            OvertakeActive = _telemetry2?.OvertakeActive == true ? 1 : 0,
        };
    }

    private string CurrentCompound() =>
        _status is { } s ? Compounds.Visual(s.VisualTyreCompound).ToString() : "Unknown";

    /// <summary>A lap only counts for consumption averages if the car stayed out of the pits and on a flying/on-track state.</summary>
    private static bool IsCleanDrivingState(LapData lap) =>
        lap.PitStatus == PitStatus.None && lap.DriverStatus is DriverStatus.FlyingLap or DriverStatus.OnTrack;
}
