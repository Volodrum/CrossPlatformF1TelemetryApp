using F1Telemetry.Core.Tracks;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.Simulation;

public sealed record SimulationOptions
{
    public GameFormat Format { get; init; } = GameFormat.F1_25;

    /// <summary>Track outline to drive on; a 4.2 km oval is generated when null.</summary>
    public TrackOutline? Track { get; init; }

    public sbyte TrackId { get; init; } = 11;
    public byte SessionType { get; init; } = 15;
    public int Laps { get; init; } = 6;

    /// <summary>Lap at whose end the player pits (0 = no stop).</summary>
    public int PitOnLap { get; init; } = 3;

    /// <summary>Laps at whose end the player pits, for multi-stop races; replaces <see cref="PitOnLap"/> when set.
    /// Compounds alternate medium, hard, medium…</summary>
    public IReadOnlyList<int>? PitLaps { get; init; }

    /// <summary>Time stationary in the box at each stop.</summary>
    public double PitStopSeconds { get; init; } = 2.5;

    /// <summary>Lap time cost of fuel mass, seconds per kg.</summary>
    public double FuelEffectSecondsPerKg { get; init; } = 0.03;

    public int AiCars { get; init; } = 3;

    /// <summary>
    /// Further AI cars spread around the lap, well away from the player (so they fill the timing tower without showing
    /// on the radar). They get names, teams, tyres, battery charge and lap history like real cars.
    /// </summary>
    public int FieldCars { get; init; } = 8;
    public int TickRateHz { get; init; } = 60;
    public ulong? SessionUid { get; init; }
    public int Seed { get; init; } = 42;
}

public readonly record struct SimulatedDatagram(TimeSpan Time, byte[] Data);

/// <summary>
/// Drives a player car (plus a few AI cars for the radar and a spread-out field for the timing tower) around a
/// track and emits the same packet stream the game would: motion, lap data, telemetry, status, damage, session,
/// participants, session history and events. Includes fuel burn, tyre wear, pace degradation, a pit stop with a
/// compound change, a front-wing hit and a field with gaps, a penalty and a car in the pits, which exercises every
/// downstream calculation.
/// </summary>
public sealed class SessionSimulator
{
    private const byte PlayerIndex = 0;
    private const double PitLaneSpeed = 22.2; // 80 km/h
    private const double PitZoneFraction = 0.04;

    // Speed used to turn the distance between cars into a time gap.
    private const double GapSpeed = 70;

    private static readonly CarSetup BaseSetup = new(
        FrontWing: 24, RearWing: 18, OnThrottle: 100, OffThrottle: 25, FrontCamber: -3.4f, RearCamber: -1.9f, FrontToe: 0.03f,
        RearToe: 0.13f, FrontSuspension: 32, RearSuspension: 10, FrontAntiRollBar: 5, RearAntiRollBar: 15, FrontSuspensionHeight: 22,
        RearSuspensionHeight: 49, BrakePressure: 99, BrakeBias: 56, EngineBraking: 50,
        TyresPressure: new Tyres<float>(24.1f, 24.1f, 28.2f, 28.2f), Ballast: 6, FuelLoad: 10);

    private static readonly (string Name, ushort Team)[] Drivers =
    [
        ("RUSSELL", 0), ("LECLERC", 1), ("PIASTRI", 8), ("ALONSO", 4), ("VERSTAPPEN", 2), ("NORRIS", 8), ("HAMILTON", 1),
        ("GASLY", 5), ("ALBON", 3), ("TSUNODA", 6), ("HULKENBERG", 9), ("OCON", 7), ("ANTONELLI", 0), ("STROLL", 4),
        ("SAINZ", 3), ("LAWSON", 6), ("BEARMAN", 7), ("BORTOLETO", 9), ("HADJAR", 6), ("COLAPINTO", 5), ("DOOHAN", 5),
    ];

    private readonly SimulationOptions _o;
    private readonly PacketWriter _writer;
    private readonly SpeedProfile _profile;
    private readonly Random _rng;
    private readonly int[] _pitLaps;

    // Lap time on fresh tyres with an empty tank, which the fuel effect is added to.
    private readonly double _baseLapSeconds;

    public SessionSimulator(SimulationOptions options)
    {
        _o = options;
        _writer = new PacketWriter(FormatLayout.For(options.Format));
        _profile = new SpeedProfile(options.Track?.Centerline ?? Oval());
        _rng = new Random(options.Seed);
        _pitLaps = options.PitLaps is { } laps ? laps.Where(l => l > 0).Distinct().Order().ToArray()
            : options.PitOnLap > 0 ? [options.PitOnLap] : [];
        for (var d = 0.0; d < _profile.Length; d += 1)
        {
            _baseLapSeconds += 1 / Math.Max(1, _profile.SpeedAt(d));
        }
    }

    public double LapLength => _profile.Length;

    private int CarCount => Math.Min(1 + _o.AiCars + _o.FieldCars, _writer.Layout.MaxCars);

    public IEnumerable<SimulatedDatagram> Generate(CancellationToken cancellationToken = default)
    {
        var uid = _o.SessionUid ?? (ulong)_rng.NextInt64(1, long.MaxValue);
        var dt = 1.0 / _o.TickRateHz;
        var L = _profile.Length;

        double t = 0, s = 0, lapStart = 0, prevV = 0, paceScale = 1;
        double stationaryLeft = 0, pitLaneTime = 0, pitStopTime = 0;
        double fuel = 5 + 1.75 * _o.Laps;
        var wear = new double[4]; // FL, FR, RL, RR
        int lap = 1, position = 4, pitStops = 0;
        uint s1 = 0, s2 = 0;
        var completed = new List<LapHistory>();
        var stints = new List<TyreStint> { new(TyreStint.Current, 18, 17) }; // C3 medium
        uint frame = 0;
        var historyCar = 0;
        var ers = new ErsModel(_writer.Layout.Format);
        var setup = BaseSetup;

        yield return Emit(t, EventPacket(uid, t, frame, "SSTA"));

        for (var tick = 0L; ; tick++, frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fraction = s / L;
            var isInLap = _pitLaps.Contains(lap) && fraction > 1 - PitZoneFraction;
            var isOutLap = _pitLaps.Contains(lap - 1) && fraction < PitZoneFraction;
            var inPitLane = isInLap || isOutLap;

            // Fuel mass adds FuelEffectSecondsPerKg × kg to the lap: scale the speed by base / (base + that).
            var fuelScale = _baseLapSeconds / (_baseLapSeconds + _o.FuelEffectSecondsPerKg * fuel);
            var v = _profile.SpeedAt(s) * paceScale * fuelScale;
            if (inPitLane)
            {
                v = Math.Min(v, PitLaneSpeed);
            }

            // The box is at the line: the car stands still there at the start of the out-lap.
            if (stationaryLeft > 0)
            {
                v = 0;
                stationaryLeft -= dt;
                pitStopTime += dt;
            }

            pitLaneTime = inPitLane ? pitLaneTime + dt : 0;
            if (!inPitLane)
            {
                pitStopTime = 0;
            }

            var accel = (v - prevV) / dt;
            prevV = v;
            var ds = v * dt;
            var (throttle, brake) = Pedals(accel);
            ers.Step(dt, v * 3.6, throttle, brake, fraction);

            // Consumption
            fuel = Math.Max(0, fuel - ds / L * 1.75);
            var visual = stints[^1].VisualCompound;
            var wearScale = visual == 18 ? 0.7 : 1.0;
            double[] rates = [2.2, 2.6, 1.8, 2.0];
            for (var w = 0; w < 4; w++)
            {
                wear[w] += ds / L * rates[w] * wearScale;
            }

            var sessionTime = (float)t;
            var status = inPitLane ? PitStatus.Pitting : PitStatus.None;
            var driverStatus = isInLap ? DriverStatus.InLap : isOutLap ? DriverStatus.OutLap : DriverStatus.FlyingLap;

            if (tick % 30 == 0)
            {
                yield return Emit(t, SessionPacket(uid, sessionTime, frame, L));
            }

            if (tick % (5 * _o.TickRateHz) == 0)
            {
                yield return Emit(t, ParticipantsPacket(uid, sessionTime, frame));
            }

            // Like the game: the setups twice a second, the next front wing being the one the stop will fit.
            if (tick % Math.Max(1, _o.TickRateHz / 2) == 0)
            {
                yield return Emit(t, SetupsPacket(uid, sessionTime, frame, setup));
            }

            if (tick % 6 == 0)
            {
                yield return Emit(t, StatusPacket(uid, sessionTime, frame, fuel, stints[^1], lap - StintStartLap(stints), lap, ers, throttle));
                yield return Emit(t, DamagePacket(uid, sessionTime, frame, wear, lap, fraction));
            }

            yield return Emit(t, MotionPacket(uid, sessionTime, frame, s, v, accel, t));
            yield return Emit(t, LapDataPacket(uid, sessionTime, frame, s, lap, position, status, driverStatus, pitStops,
                (uint)((t - lapStart) * 1000), completed.LastOrDefault().LapTimeMs, s1, s2, L, t,
                (ushort)Math.Min(ushort.MaxValue, pitLaneTime * 1000), (ushort)Math.Min(ushort.MaxValue, pitStopTime * 1000)));
            yield return Emit(t, TelemetryPacket(uid, sessionTime, frame, s, v, throttle, brake, lap));
            if (_writer.Layout.Format == GameFormat.F1_26)
            {
                yield return Emit(t, Telemetry2Packet(uid, sessionTime, frame, v, lap));
            }

            if (tick % _o.TickRateHz == 0)
            {
                var current = new LapHistory(0, s1, s2, 0, 0x0F);
                yield return Emit(t, HistoryPacket(uid, sessionTime, frame, PlayerIndex, [.. completed, current], stints));
            }

            // The game cycles through the other cars' histories; here a few per second.
            if (CarCount > 1 && tick % Math.Max(1, _o.TickRateHz / 4) == 0)
            {
                var car = 1 + historyCar++ % (CarCount - 1);
                yield return Emit(t, HistoryPacket(uid, sessionTime, frame, (byte)car, AiLaps(completed, car), [new TyreStint(TyreStint.Current, 18, 17)]));
            }

            // Advance
            var before = s;
            s += ds;
            t += dt;
            if (before < L / 3 && s >= L / 3)
            {
                s1 = (uint)((t - lapStart) * 1000);
            }

            if (before < 2 * L / 3 && s >= 2 * L / 3)
            {
                s2 = (uint)((t - lapStart) * 1000) - s1;
            }

            if (s < L)
            {
                continue;
            }

            // Lap complete
            var lapTime = (uint)((t - lapStart) * 1000);
            completed.Add(new LapHistory(lapTime, s1, s2, lapTime - s1 - s2, 0x0F));
            s -= L;
            lapStart = t;
            s1 = s2 = 0;
            ers.StartLap();

            if (_pitLaps.Contains(lap))
            {
                stints[^1] = stints[^1] with { EndLap = (byte)lap };
                stints.Add(stints.Count % 2 == 1 ? new TyreStint(TyreStint.Current, 19, 18) : new TyreStint(TyreStint.Current, 18, 17)); // C2 hard / C3 medium
                Array.Clear(wear);
                setup = setup with { FrontWing = NextFrontWing(setup) };
                pitStops++;
                position = 7;
                stationaryLeft = _o.PitStopSeconds;
            }
            else if (position > 3)
            {
                position--;
            }

            lap++;
            paceScale = (1 - 0.0012 * wear.Average()) * (0.996 + 0.004 * _rng.NextDouble());

            if (lap > _o.Laps)
            {
                // Like the game at the flag: the lap data times the final lap and classifies the car, but the lap
                // number stays (one more telemetry packet arrives past the line) and the player's history only gets
                // the final lap's time and S3 with the results, if the app is still listening by then.
                var final = completed[^1];
                yield return Emit(t, LapDataPacket(uid, (float)t, frame, 0, _o.Laps, position, PitStatus.None, DriverStatus.OnTrack,
                    pitStops, 0, final.LapTimeMs, 0, 0, L, t, 0, 0, ResultStatus.Finished));
                yield return Emit(t, TelemetryPacket(uid, (float)t, frame, 0, prevV, 0.35, 0, _o.Laps));
                yield return Emit(t, HistoryPacket(uid, (float)t, frame, PlayerIndex,
                    [.. completed[..^1], final with { LapTimeMs = 0, Sector3Ms = 0 }], stints));
                yield return Emit(t, EventPacket(uid, t, frame, "SEND"));
                yield return Emit(t, FinalClassificationPacket(uid, (float)t, frame, position, pitStops, completed, t));
                yield break;
            }
        }
    }

    private static int StintStartLap(List<TyreStint> stints) =>
        stints.Count < 2 ? 1 : stints[^2].EndLap + 1;

    private static SimulatedDatagram Emit(double t, byte[] data) => new(TimeSpan.FromSeconds(t), data);

    private byte[] EventPacket(ulong uid, double t, uint frame, string code)
    {
        var p = _writer.Create(PacketId.Event, uid, (float)t, frame, PlayerIndex);
        _writer.WriteEventCode(p, code);
        return p;
    }

    private byte[] SessionPacket(ulong uid, float t, uint frame, double lapLength)
    {
        var p = _writer.Create(PacketId.Session, uid, t, frame, PlayerIndex);
        _writer.WriteSession(p, new SessionData(
            Weather: 1, TrackTemperature: 32, AirTemperature: 24, TotalLaps: (byte)_o.Laps, TrackLength: (ushort)lapLength,
            SessionType: _o.SessionType, TrackId: _o.TrackId, Formula: _writer.Layout.Format == GameFormat.F1_26 ? (byte)13 : (byte)0,
            SessionTimeLeft: 3600, SessionDuration: 3600, PitSpeedLimit: 80, GamePaused: false, SafetyCarStatus: 0, IsNetworkGame: false,
            WeatherForecast:
            [
                new WeatherForecastSample(_o.SessionType, 0, 1, 32, 0, 24, 0, 5),
                new WeatherForecastSample(_o.SessionType, 5, 1, 32, 0, 24, 0, 10),
                new WeatherForecastSample(_o.SessionType, 10, 2, 31, 1, 23, 1, 20),
                new WeatherForecastSample(_o.SessionType, 15, 3, 29, 1, 22, 1, 45),
            ],
            ForecastAccuracy: 0, AiDifficulty: 90, PitStopWindowIdealLap: (byte)_pitLaps.FirstOrDefault(), PitStopWindowLatestLap: (byte)(_pitLaps.FirstOrDefault() + 2),
            PitStopRejoinPosition: 7, NumSafetyCarPeriods: 0, NumVirtualSafetyCarPeriods: 0, NumRedFlagPeriods: 0,
            Sector2LapDistanceStart: (float)(lapLength / 3), Sector3LapDistanceStart: (float)(2 * lapLength / 3),
            Aero: new SessionAeroInfo(1, [], [], [], 0.2f)));
        return p;
    }

    private byte[] StatusPacket(ulong uid, float t, uint frame, double fuel, TyreStint stint, int tyreAge, int lap, ErsModel ers, double throttle)
    {
        var p = _writer.Create(PacketId.CarStatus, uid, t, frame, PlayerIndex);
        for (var i = 1; i < CarCount; i++)
        {
            var compound = (byte)(16 + i % 3); // soft / medium / hard
            _writer.WriteCarStatus(p, i, new CarStatus(
                TractionControl: 0, AntiLockBrakes: 0, FuelMix: 1, FrontBrakeBias: 56, PitLimiter: false,
                FuelInTank: 20, FuelCapacity: 110, FuelRemainingLaps: 2,
                MaxRpm: 12500, IdleRpm: 4000, MaxGears: 8, DrsAllowed: true, DrsActivationDistance: 0,
                ActualTyreCompound: compound, VisualTyreCompound: compound, TyresAgeLaps: (byte)(lap - 1 + i % 5),
                VehicleFiaFlags: 0, EnginePowerIce: 600_000, EnginePowerMguk: 120_000,
                ErsStoreEnergy: (float)(4_000_000 * (0.5 + 0.45 * Math.Sin(t / 15 + i))), ErsDeployMode: 1,
                ErsHarvestedThisLapMguk: 500_000, ErsHarvestedThisLapMguh: 250_000, ErsHarvestLimitPerLap: 6_000_000,
                ErsDeployedThisLap: 400_000, NetworkPaused: false));
        }

        _writer.WriteCarStatus(p, PlayerIndex, new CarStatus(
            TractionControl: 0, AntiLockBrakes: 0, FuelMix: 1, FrontBrakeBias: 56, PitLimiter: false,
            FuelInTank: (float)fuel, FuelCapacity: 110, FuelRemainingLaps: (float)(fuel / 1.75 - 1),
            MaxRpm: 12500, IdleRpm: 4000, MaxGears: 8, DrsAllowed: true, DrsActivationDistance: 0,
            ActualTyreCompound: stint.ActualCompound, VisualTyreCompound: stint.VisualCompound, TyresAgeLaps: (byte)Math.Max(0, tyreAge),
            VehicleFiaFlags: 0, EnginePowerIce: (float)(ers.IcePower * throttle), EnginePowerMguk: (float)ers.MgukPower,
            ErsStoreEnergy: (float)ers.Store, ErsDeployMode: ers.Mode,
            ErsHarvestedThisLapMguk: (float)ers.HarvestedThisLap, ErsHarvestedThisLapMguh: 0,
            ErsHarvestLimitPerLap: _writer.Layout.HasErsHarvestLimit ? (float)ers.HarvestLimit : null,
            ErsDeployedThisLap: (float)ers.DeployedThisLap, NetworkPaused: false));
        return p;
    }

    private byte[] DamagePacket(ulong uid, float t, uint frame, double[] wear, int lap, double fraction)
    {
        // A front-wing hit halfway round lap 2, fixed by the new nose at the pit stop.
        var firstStop = _pitLaps.FirstOrDefault();
        var hit = lap == 2 ? fraction > 0.5 : lap > 2 && (firstStop < 2 || lap <= firstStop);
        var p = _writer.Create(PacketId.CarDamage, uid, t, frame, PlayerIndex);
        _writer.WriteCarDamage(p, PlayerIndex, new CarDamage(
            TyresWear: new Tyres<float>((float)wear[2], (float)wear[3], (float)wear[0], (float)wear[1]),
            TyresDamage: default, BrakesDamage: new Tyres<byte>(2, 3, 4, 6), TyreBlisters: default,
            FrontLeftWingDamage: hit ? (byte)24 : (byte)0, FrontRightWingDamage: 0, RearWingDamage: 0, FloorDamage: hit ? (byte)8 : (byte)0,
            DiffuserDamage: 0, SidepodDamage: 0,
            DrsFault: false, ErsFault: false, GearBoxDamage: 3, EngineDamage: 2, EngineMguhWear: 4, EngineEsWear: 3,
            EngineCeWear: 3, EngineIceWear: 5, EngineMgukWear: 4, EngineTcWear: 4, EngineBlown: false, EngineSeized: false));
        return p;
    }

    private byte[] MotionPacket(ulong uid, float t, uint frame, double s, double v, double accel, double time)
    {
        var p = _writer.Create(PacketId.Motion, uid, t, frame, PlayerIndex);
        _writer.WriteMotion(p, PlayerIndex, CarAt(s, 0, v, accel));

        for (var i = 1; i < CarCount; i++)
        {
            var (offset, lateral) = AiOffset(i, time);
            _writer.WriteMotion(p, i, CarAt(s + offset, lateral, v, 0));
        }

        return p;
    }

    private (double Offset, double Lateral) AiOffset(int car, double time)
    {
        if (car > _o.AiCars)
        {
            // Field cars alternate ahead / behind, further out each time, with slowly changing gaps.
            var k = car - _o.AiCars;
            var side = k % 2 == 1 ? 1 : -1;
            return (side * (70 + 65 * ((k + 1) / 2)) + 6 * Math.Sin(time * 0.05 * k), 0);
        }

        return car switch
        {
            1 => (6 + 9 * Math.Sin(time * 0.25), 2.2),    // sweeps alongside on the right
            2 => (-14 + 5 * Math.Sin(time * 0.4), -1.5),  // behind, slightly left
            3 => (22, 0),                                 // car ahead
            _ => (40 * car, 0),
        };
    }

    /// <summary>
    /// Race order: the player keeps its scripted position and the AI cars fill the other places by distance.
    /// Returns each car's position and its offset along the track from the player.
    /// </summary>
    private (int[] Positions, double[] Offsets) Order(int playerPosition, double time)
    {
        var positions = new int[CarCount];
        var offsets = new double[CarCount];
        positions[PlayerIndex] = Math.Clamp(playerPosition, 1, CarCount);
        var next = 1;
        foreach (var car in Enumerable.Range(1, CarCount - 1).OrderByDescending(c => AiOffset(c, time).Offset))
        {
            offsets[car] = AiOffset(car, time).Offset;
            if (next == positions[PlayerIndex])
            {
                next++;
            }

            positions[car] = next++;
        }

        return (positions, offsets);
    }

    /// <summary>A field car's laps: the player's laps scaled by that car's pace.</summary>
    private static List<LapHistory> AiLaps(IReadOnlyList<LapHistory> playerLaps, int car)
    {
        var pace = 1 + 0.004 * (car - 3);
        var laps = new List<LapHistory>(playerLaps.Count);
        foreach (var l in playerLaps)
        {
            var lapMs = (uint)(l.LapTimeMs * pace);
            var s1 = (uint)(l.Sector1Ms * pace);
            var s2 = (uint)(l.Sector2Ms * pace);
            laps.Add(new LapHistory(lapMs, s1, s2, lapMs - s1 - s2, 0x0F));
        }

        return laps;
    }

    private CarMotion CarAt(double s, double lateral, double v, double accel)
    {
        var (x, z, fx, fz, curvature) = _profile.Sample(s);
        var rx = fz;
        var rz = -fx;
        x += rx * lateral;
        z += rz * lateral;
        var gLat = (float)(v * v * curvature / 9.81);
        return new CarMotion((float)x, 0, (float)z, (float)(fx * v), 0, (float)(fz * v), (float)fx, 0, (float)fz, (float)rx, 0, (float)rz,
            gLat, (float)(accel / 9.81), 1, (float)Math.Atan2(fx, fz), 0, 0);
    }

    private byte[] LapDataPacket(ulong uid, float t, uint frame, double s, int lap, int position, PitStatus pit, DriverStatus driver,
        int pitStops, uint currentLapMs, uint lastLapMs, uint s1, uint s2, double L, double time, ushort pitLaneMs, ushort pitStopMs,
        ResultStatus result = ResultStatus.Active)
    {
        var p = _writer.Create(PacketId.LapData, uid, t, frame, PlayerIndex);
        var sector = SectorAt(s, L);

        // Gaps from the distance between consecutive cars in race order.
        var (positions, offsets) = Order(position, time);
        var byPosition = Enumerable.Range(0, CarCount).OrderBy(c => positions[c]).ToArray();
        var toFront = new uint[CarCount];
        var toLeader = new uint[CarCount];
        for (var k = 1; k < byPosition.Length; k++)
        {
            var car = byPosition[k];
            toFront[car] = (uint)(Math.Abs(offsets[byPosition[k - 1]] - offsets[car]) / GapSpeed * 1000);
            toLeader[car] = toLeader[byPosition[k - 1]] + toFront[car];
        }

        _writer.WriteLapData(p, PlayerIndex, new LapData(lastLapMs, currentLapMs, s1, s2, toFront[PlayerIndex], toLeader[PlayerIndex],
            (float)s, (float)((lap - 1) * L + s), 0, (byte)positions[PlayerIndex], (byte)lap, pit, (byte)pitStops, sector, false, 0, 0, 0, 4,
            driver, result, pit != PitStatus.None, pitLaneMs, pitStopMs, 330));

        for (var i = 1; i < CarCount; i++)
        {
            var aiS = s + offsets[i];
            var aiLapDistance = (aiS % L + L) % L;
            var k = i - _o.AiCars;
            var aiPit = k == 3 && lap % 3 == 2 && s / L is > 0.4 and < 0.5 ? PitStatus.Pitting : PitStatus.None;
            _writer.WriteLapData(p, i, new LapData(lastLapMs, currentLapMs, 0, 0, toFront[i], toLeader[i], (float)aiLapDistance,
                (float)((lap - 1) * L + aiS), 0, (byte)positions[i], (byte)lap, aiPit, 0, SectorAt(aiLapDistance, L), false,
                Penalties: k == 2 && lap >= 2 ? (byte)5 : (byte)0, 0, CornerCuttingWarnings: i == 2 ? (byte)2 : (byte)0, (byte)i,
                DriverStatus.FlyingLap, ResultStatus.Active, aiPit != PitStatus.None, 0, 0, 320));
        }

        return p;
    }

    private static byte SectorAt(double s, double L) => s < L / 3 ? (byte)0 : s < 2 * L / 3 ? (byte)1 : (byte)2;

    private byte[] ParticipantsPacket(ulong uid, float t, uint frame)
    {
        var p = _writer.Create(PacketId.Participants, uid, t, frame, PlayerIndex);
        var teamBase = _writer.Layout.Format == GameFormat.F1_26 ? (ushort)476 : (ushort)0;
        _writer.WriteNumActiveCars(p, (byte)CarCount);
        for (var i = 0; i < CarCount; i++)
        {
            var (name, team) = i == PlayerIndex ? ("Player", (ushort)1) : Drivers[(i - 1) % Drivers.Length];
            _writer.WriteParticipant(p, i, new Participant(
                AiControlled: i != PlayerIndex, DriverId: (ushort)i, NetworkId: 255, TeamId: (ushort)(teamBase + team), MyTeam: false,
                RaceNumber: (byte)(i + 2), Nationality: 1, Name: name, TelemetryPublic: true, ShowOnlineNames: true, TechLevel: 0,
                Platform: 1, LiveryColours: []));
        }

        return p;
    }

    private static (double Throttle, double Brake) Pedals(double accel) =>
        accel < -3 ? (0, Math.Min(1, -accel / 35)) : (accel > 0.3 ? 1 : 0.35, 0);

    private byte[] TelemetryPacket(ulong uid, float t, uint frame, double s, double v, double throttle, double brake, int lap)
    {
        var p = _writer.Create(PacketId.CarTelemetry, uid, t, frame, PlayerIndex);
        var kmh = v * 3.6;
        var gear = (sbyte)Math.Clamp(1 + (int)(kmh / 42), 1, 8);
        var rpm = (ushort)(9500 + kmh % 42 / 42 * 2500);
        var steer = (float)Math.Clamp(_profile.Sample(s).Curvature * 60, -1, 1);
        var brakeTemp = (ushort)(450 + brake * 500);

        _writer.WriteCarTelemetry(p, PlayerIndex, new CarTelemetry(
            (ushort)kmh, (float)throttle, steer, (float)brake, 0, gear, rpm, lap > 1 && v > 80, (byte)(rpm / 125),
            new Tyres<ushort>(brakeTemp, brakeTemp, (ushort)(brakeTemp + 40), (ushort)(brakeTemp + 40)),
            new Tyres<byte>(92, 93, 96, 97), new Tyres<byte>(101, 101, 104, 104), 108,
            new Tyres<float>(22.4f, 22.4f, 23.1f, 23.1f), default));
        _writer.WriteCarTelemetryTrailer(p, 255, 0);
        return p;
    }

    private byte[] Telemetry2Packet(ulong uid, float t, uint frame, double v, int lap)
    {
        var p = _writer.Create(PacketId.CarTelemetry2, uid, t, frame, PlayerIndex);
        _writer.WriteCarTelemetry2(p, PlayerIndex, new CarTelemetry2(
            ActiveAeroMode: v > 75 ? (byte)1 : (byte)0, ActiveAeroAvailable: true, ActiveAeroActivationDistance: 0,
            OvertakeAvailable: lap > 1, OvertakeActive: lap > 1 && v > 80, OvertakeActivationDistance: 0,
            Regulations2026Applicable: true, IsDrivingWrongWay: false));
        return p;
    }

    private static byte NextFrontWing(CarSetup setup) => (byte)(setup.FrontWing + 1);

    private byte[] SetupsPacket(ulong uid, float t, uint frame, CarSetup player)
    {
        var p = _writer.Create(PacketId.CarSetups, uid, t, frame, PlayerIndex);
        _writer.WriteCarSetup(p, PlayerIndex, player);
        for (var i = 1; i < CarCount; i++)
        {
            _writer.WriteCarSetup(p, i, BaseSetup with { RearWing = (byte)(BaseSetup.RearWing + i % 3) });
        }

        _writer.WriteNextFrontWingValue(p, NextFrontWing(player));
        return p;
    }

    private byte[] FinalClassificationPacket(ulong uid, float t, uint frame, int position, int pitStops, IReadOnlyList<LapHistory> laps, double raceTime)
    {
        var p = _writer.Create(PacketId.FinalClassification, uid, t, frame, PlayerIndex);
        _writer.WriteFinalClassification(p, PlayerIndex, new FinalClassification((byte)position, (byte)laps.Count, 4, 0, (byte)pitStops,
            ResultStatus.Finished, laps.Min(l => l.LapTimeMs), raceTime, 0));
        return p;
    }

    private byte[] HistoryPacket(ulong uid, float t, uint frame, byte carIndex, IReadOnlyList<LapHistory> laps, IReadOnlyList<TyreStint> stints)
    {
        var p = _writer.Create(PacketId.SessionHistory, uid, t, frame, PlayerIndex);
        byte Best(Func<LapHistory, uint> selector)
        {
            var best = laps.Select((l, i) => (Value: selector(l), Lap: i + 1)).Where(x => x.Value > 0).OrderBy(x => x.Value).FirstOrDefault();
            return (byte)best.Lap;
        }

        _writer.WriteSessionHistory(p, carIndex, laps, stints, Best(l => l.LapTimeMs), Best(l => l.Sector1Ms), Best(l => l.Sector2Ms), Best(l => l.Sector3Ms));
        return p;
    }

    private static List<TrackPoint> Oval()
    {
        const double straight = 1200, radius = 300;
        var points = new List<TrackPoint>();
        for (double d = 0; d < straight; d += 5) points.Add(new TrackPoint(d, 0));
        for (double a = 0; a < Math.PI; a += 5 / radius) points.Add(new TrackPoint(straight + radius * Math.Sin(a), radius - radius * Math.Cos(a)));
        for (double d = straight; d > 0; d -= 5) points.Add(new TrackPoint(d, 2 * radius));
        for (double a = 0; a < Math.PI; a += 5 / radius) points.Add(new TrackPoint(-radius * Math.Sin(a), radius + radius * Math.Cos(a)));
        return points;
    }
}
