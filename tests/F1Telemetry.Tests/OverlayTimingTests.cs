using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Lookups;
using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.Tests;

/// <summary>Sector box, timing tower and damage pop-up logic.</summary>
public class OverlayTimingTests
{
    private static PacketHeader Header(PacketId id, byte player = 0) => new(2025, 25, 1, 0, 1, id, 1, 0, 0, 0, player, 255);

    private static SessionHistoryPacket History(byte car, params (uint S1, uint S2, uint S3)[] laps)
    {
        var history = laps.Select(l => new LapHistory(l.S1 + l.S2 + l.S3, l.S1, l.S2, l.S3, 0x0F)).ToArray();
        byte Best(Func<LapHistory, uint> pick) => (byte)(Array.IndexOf(history, history.MinBy(pick)) + 1);
        return new SessionHistoryPacket(Header(PacketId.SessionHistory), car, Best(l => l.LapTimeMs), Best(l => l.Sector1Ms),
            Best(l => l.Sector2Ms), Best(l => l.Sector3Ms), history, []);
    }

    private static ParticipantsPacket Participants(params (string Name, ushort Team, bool Ai, bool Public)[] cars)
    {
        var all = new Participant[FormatLayout.F1_25.MaxCars];
        for (var i = 0; i < cars.Length; i++)
        {
            all[i] = new Participant(cars[i].Ai, 0, 0, cars[i].Team, false, (byte)(i + 1), 0, cars[i].Name, cars[i].Public, true, 0, 1, []);
        }

        return new ParticipantsPacket(Header(PacketId.Participants), (byte)cars.Length, all);
    }

    /// <summary>Player (car 0) PB 1:12.604; NORRIS (car 1) holds the session best, 1:12.310.</summary>
    private static FieldTracker QualifyingField()
    {
        var field = new FieldTracker();
        field.OnParticipants(Participants(("Player", 1, false, true), ("NORRIS", 8, true, true)));
        field.OnSession(new SessionData(0, 30, 20, 0, 5000, 7, 11, 0, 600, 3600, 80, false, 0, false, [], 0, 0, 0, 0, 0, 0, 0, 0, 1600, 3300, null));
        field.OnHistory(History(0, (23_450, 27_880, 21_274)));
        field.OnHistory(History(1, (23_386, 27_771, 21_153)));
        return field;
    }

    private static LapData Lap(int lapNum, byte sector, uint current, uint s1 = 0, uint s2 = 0, uint last = 0, float distance = 0,
        bool invalid = false, DriverStatus status = DriverStatus.FlyingLap) => default(LapData) with
    {
        CurrentLapNum = (byte)lapNum,
        Sector = sector,
        CurrentLapTimeMs = current,
        Sector1TimeMs = s1,
        Sector2TimeMs = s2,
        LastLapTimeMs = last,
        LapDistance = distance,
        CurrentLapInvalid = invalid,
        DriverStatus = status,
        CarPosition = 3,
        ResultStatus = ResultStatus.Active,
    };

    private static void Feed(FieldTracker field, LapData player)
    {
        var cars = new LapData[FormatLayout.F1_25.MaxCars];
        cars[0] = player;
        field.OnLapData(new LapDataPacket(Header(PacketId.LapData), cars, 255, 255));
    }

    [Fact]
    public void Sector_box_colours_splits_and_holds_the_finished_lap()
    {
        var field = QualifyingField();
        var timer = new SectorTimer();
        SectorBoxSnapshot Step(double t, LapData lap)
        {
            Feed(field, lap);
            return timer.Update(lap, t, field);
        }

        var start = Step(1, Lap(2, 0, 800, distance: 800));
        Assert.Equal([SectorMark.Live, SectorMark.Pending, SectorMark.Pending], start.Sectors);
        Assert.Equal(0.5, start.LiveProgress, 3); // 800 m of a 1600 m sector 1
        Assert.Null(start.LastSplit);
        Assert.Equal("NOR", start.SessionBest!.Code);

        var afterS1 = Step(24, Lap(2, 1, 23_400, s1: 23_302, distance: 1700));
        Assert.Equal(SectorMark.SessionBest, afterS1.Sectors[0]); // quicker than NORRIS's 23.386
        Assert.Equal(new SectorSplit(1, 23_302, -84, -148), afterS1.LastSplit);
        Assert.NotNull(Step(27.9, Lap(2, 1, 27_300, s1: 23_302, distance: 2000)).LastSplit);
        var laterInS2 = Step(28, Lap(2, 1, 27_400, s1: 23_302, distance: 2010));
        Assert.Null(laterInS2.LastSplit); // shown for 4 s, then the timer is back
        Assert.Equal(SectorMark.SessionBest, laterInS2.Sectors[0]);

        var afterS2 = Step(52, Lap(2, 2, 51_300, s1: 23_302, s2: 27_905, distance: 3400));
        Assert.Equal([SectorMark.SessionBest, SectorMark.Slower, SectorMark.Live], afterS2.Sectors);
        Assert.Equal(new SectorSplit(2, 27_905, 50, -123), afterS2.LastSplit); // cumulative gaps at the S2 split

        var finished = Step(73, Lap(3, 0, 100, last: 72_308, distance: 10));
        Assert.True(finished.IsFinished);
        Assert.Equal(72_308u, finished.LapTimeMs);
        Assert.True(finished.LapIsSessionBest);
        Assert.True(finished.LapIsPersonalBest);
        Assert.Equal(new SectorSplit(3, 21_101, -2, -296), finished.LastSplit);

        // The history now includes the new lap, but the held frame keeps the colours and gaps from the line.
        field.OnHistory(History(0, (23_450, 27_880, 21_274), (23_302, 27_905, 21_101)));
        Assert.True(Step(76.5, Lap(3, 0, 3600, distance: 300)).IsFinished);

        var next = Step(77, Lap(3, 0, 4100, distance: 400));
        Assert.False(next.IsFinished);
        Assert.Equal([SectorMark.Live, SectorMark.Pending, SectorMark.Pending], next.Sectors);
        Assert.Equal("YOU", next.SessionBest!.Code);
    }

    [Fact]
    public void Sector_box_greys_out_an_invalid_lap_and_ignores_out_laps()
    {
        var field = QualifyingField();
        var timer = new SectorTimer();

        timer.Update(Lap(1, 0, 60_000, status: DriverStatus.OutLap), 1, field);
        var outLap = timer.Update(Lap(1, 2, 90_000, s1: 30_000, s2: 31_000, status: DriverStatus.OutLap), 2, field);
        Assert.True(outLap.IsOutOrInLap);
        Assert.All(outLap.Sectors, m => Assert.Equal(SectorMark.Pending, m));
        Assert.False(timer.Update(Lap(2, 0, 100, last: 95_000), 3, field).IsFinished); // out lap is not held

        timer.Update(Lap(2, 1, 23_500, s1: 23_400), 4, field);
        var invalid = timer.Update(Lap(2, 1, 24_000, s1: 23_400, invalid: true), 5, field);
        Assert.True(invalid.IsInvalid);
        Assert.Equal(SectorMark.Invalid, invalid.Sectors[0]);
        Assert.Null(invalid.LastSplit);
    }

    private static CarTiming Car(int position, string code, long gapToLeaderMs, uint interval = 500, bool player = false,
        PitStatus pit = PitStatus.None, float distance = 20_000, uint best = 0, int penalty = 0, int warnings = 0) => new()
    {
        CarIndex = position,
        Position = position,
        Code = code,
        IsPlayer = player,
        DeltaToLeaderMs = (uint)gapToLeaderMs,
        DeltaToCarInFrontMs = interval,
        TotalDistance = distance,
        CurrentLap = 5,
        PitStatus = pit,
        ResultStatus = ResultStatus.Active,
        BestLapMs = best,
        PenaltySeconds = penalty,
        TrackLimitWarnings = warnings,
    };

    [Fact]
    public void Tower_shows_three_ahead_and_two_behind_with_gaps_to_the_player()
    {
        var cars = Enumerable.Range(1, 10).Select(p => Car(p, $"C{p:00}", (p - 1) * 1_500L)).ToList();
        cars[6] = Car(7, "YOU", 9_000, player: true);
        cars[5] = Car(6, "RUS", 8_356, penalty: 5);
        cars[7] = Car(8, "ALO", 9_812, warnings: 2);
        cars[8] = Car(9, "GAS", 12_000, pit: PitStatus.Pitting);
        var board = TimingTower.Build(new FieldSnapshot(cars, 7, 15, 30, 0, 5000), TowerGapMode.GapToMe);

        Assert.Equal(("RACE · LAP", "5", "/ 30"), (board.Title, board.Counter, board.CounterTotal));
        Assert.Equal([4, 5, 6, 7, 8, 9], board.Rows.Select(r => r.Position));
        Assert.Equal(["−3.000", "−0.644", "—", "+0.812", "PIT"], board.Rows.Skip(1).Select(r => r.Gap));
        Assert.Equal([GapTone.Normal, GapTone.Ahead, GapTone.Muted, GapTone.Threat, GapTone.Pit], board.Rows.Skip(1).Select(r => r.Tone));
        Assert.Equal("+5s", board.Rows[2].Penalty);
        Assert.Equal("2⚠", board.Rows[4].Warnings);
    }

    [Fact]
    public void Tower_window_shifts_at_the_front_and_supports_intervals_and_lapped_cars()
    {
        var cars = Enumerable.Range(1, 8).Select(p => Car(p, $"C{p}", (p - 1) * 700L, interval: 700, distance: 20_000 - p * 50)).ToList();
        cars[1] = Car(2, "YOU", 700, interval: 700, player: true, distance: 19_900);
        cars[7] = Car(8, "LAP", 30_000, interval: 25_000, distance: 12_000); // more than a lap (5 km) behind
        var field = new FieldSnapshot(cars, 2, 15, 30, 0, 5000);

        var interval = TimingTower.Build(field, TowerGapMode.Interval);
        Assert.Equal([1, 2, 3, 4, 5, 6], interval.Rows.Select(r => r.Position));
        Assert.Equal("INTERVAL", interval.Mode);
        Assert.Equal(("LEADER", GapTone.Muted), (interval.Rows[0].Gap, interval.Rows[0].Tone));
        Assert.Equal(("+0.700", GapTone.Ahead), (interval.Rows[1].Gap, interval.Rows[1].Tone));   // player within DRS range
        Assert.Equal(("+0.700", GapTone.Threat), (interval.Rows[2].Gap, interval.Rows[2].Tone));  // car right behind the player

        var gaps = TimingTower.Build(field with { Cars = [.. cars.Take(5), cars[7]] }, TowerGapMode.GapToMe);
        Assert.Equal([1, 2, 3, 4, 5, 8], gaps.Rows.Select(r => r.Position));
        Assert.Equal("+1L", gaps.Rows[^1].Gap);
    }

    [Fact]
    public void Qualifying_tower_compares_best_laps_and_marks_the_cut_off()
    {
        var cars = Enumerable.Range(1, 20).Select(p => Car(p, $"C{p}", 0, best: 70_000 + (uint)p * 100)).ToList();
        cars[14] = Car(15, "YOU", 0, player: true, best: 71_500);
        cars[15] = Car(16, "HUL", 0, best: 71_534);
        cars[19] = Car(20, "NOT", 0);
        var board = TimingTower.Build(new FieldSnapshot(cars, 15, 5, 0, 522, 5000), TowerGapMode.GapToMe);

        Assert.Equal(("QUALIFYING · Q1", "8:42", ""), (board.Title, board.Counter, board.CounterTotal));
        Assert.Equal("CUT-OFF P15", board.Mode);
        Assert.Equal([12, 13, 14, 15, 16, 17], board.Rows.Select(r => r.Position));
        Assert.Equal("1:11.500", board.Rows[3].Gap);
        Assert.True(board.Rows[3].CutoffBelow);
        Assert.Equal(("+0.034", GapTone.Threat), (board.Rows[4].Gap, board.Rows[4].Tone));
        Assert.Equal("−0.200", board.Rows[1].Gap);
    }

    [Fact]
    public void Field_tracker_merges_participants_status_and_restricted_telemetry()
    {
        var field = new FieldTracker();
        field.OnParticipants(Participants(("Player", 1, false, true), ("Max VERSTAPPEN", 2, true, true), ("xX_Racer_Xx", 104, false, false)));
        var laps = new LapData[FormatLayout.F1_25.MaxCars];
        laps[0] = Lap(3, 1, 1000) with { CarPosition = 2 };
        laps[1] = Lap(3, 1, 1000) with { CarPosition = 1 };
        laps[2] = Lap(3, 1, 1000) with { CarPosition = 3, Penalties = 10 };
        laps[3] = Lap(3, 1, 1000) with { CarPosition = 4, ResultStatus = ResultStatus.Inactive };
        field.OnLapData(new LapDataPacket(Header(PacketId.LapData), laps, 255, 255));
        var status = new CarStatus[FormatLayout.F1_25.MaxCars];
        status[1] = default(CarStatus) with { ErsStoreEnergy = 3_000_000, VisualTyreCompound = 16, TyresAgeLaps = 4 };
        status[2] = default(CarStatus) with { ErsStoreEnergy = 0, VisualTyreCompound = 18 };
        field.OnStatus(new CarStatusPacket(Header(PacketId.CarStatus), status));

        var snapshot = field.Snapshot();
        Assert.Equal(["VER", "YOU", "XXR"], snapshot.Cars.Select(c => c.Code));
        var ver = snapshot.Cars[0];
        Assert.Equal((75.0, VisualCompound.Soft, 4, 0x3671C6u), (ver.ErsPercent!.Value, ver.Compound, ver.TyreAgeLaps, ver.TeamColour!.Value));
        var online = snapshot.Cars[2];
        Assert.True(online.TelemetryRestricted);
        Assert.Null(online.ErsPercent);
        Assert.Equal(10, online.PenaltySeconds);
        Assert.True(snapshot.Player!.IsPlayer);
    }

    [Fact]
    public void Damage_monitor_reports_new_bodywork_damage_only()
    {
        var monitor = new DamageMonitor();
        var clean = default(CarDamage);
        Assert.False(monitor.Update(clean)); // baseline
        Assert.False(monitor.Update(clean with { TyresWear = new(10, 10, 10, 10), BrakesDamage = new(5, 5, 5, 5), EngineIceWear = 20 }));
        Assert.True(monitor.Update(clean with { FrontLeftWingDamage = 24 }));
        Assert.False(monitor.Update(clean with { FrontLeftWingDamage = 24 }));
        Assert.False(monitor.Update(clean)); // repaired / flashback
        Assert.True(monitor.Update(clean with { DrsFault = true }));
    }

    [Fact]
    public void Pit_stop_seen_live_fixes_the_stint_end_lap_from_the_game()
    {
        // The recording from the bug report: pit entry late in lap 10, box after the line (stop early in lap 11), but the
        // game's stint history ends the medium stint on lap 9. Laps 9, 10 and 11 used to all come out as PIT.
        var engine = new SessionEngine();
        IReadOnlyList<LapRecord> laps = [];
        engine.LapsUpdated += l => laps = l;
        engine.Process(new SessionPacket(Header(PacketId.Session), new SessionData(0, 30, 20, 20, 5000, 15, 11, 0, 3600, 3600, 80,
            false, 0, false, [], 0, 0, 0, 0, 0, 0, 0, 0, 1600, 3300, null)));

        void Drive(int lap, float distance, PitStatus pit = PitStatus.None, byte stops = 0)
        {
            var cars = new LapData[FormatLayout.F1_25.MaxCars];
            cars[0] = Lap(lap, 0, 1000, distance: distance) with { PitStatus = pit, NumPitStops = stops };
            engine.Process(new LapDataPacket(Header(PacketId.LapData), cars, 255, 255));
        }

        for (var lap = 1; lap <= 12; lap++)
        {
            Drive(lap, 100, lap == 11 ? PitStatus.Pitting : PitStatus.None, lap > 11 ? (byte)1 : (byte)0);
            if (lap == 11)
            {
                Drive(11, 250, PitStatus.InPitArea);
                Drive(11, 300, PitStatus.Pitting, stops: 1); // leaves the box: counter goes up early in lap 11
            }

            Drive(lap, 4900, lap == 10 ? PitStatus.Pitting : PitStatus.None, lap >= 11 ? (byte)1 : (byte)0);
        }

        var history = Enumerable.Range(1, 12).Select(_ => new LapHistory(84_000, 27_000, 28_000, 29_000, 0x0F)).ToArray();
        engine.Process(new SessionHistoryPacket(Header(PacketId.SessionHistory), 0, 1, 1, 1, 1, history,
            [new TyreStint(9, 17, 17), new TyreStint(TyreStint.Current, 18, 18)]));

        Assert.Equal([LapType.Regular, LapType.Pit, LapType.Pit, LapType.Regular], laps.Skip(8).Take(4).Select(l => l.LapType));
        Assert.Equal(["Medium", "Medium", "Hard", "Hard"], laps.Skip(8).Take(4).Select(l => l.Compound));
        Assert.Equal([0, 0, 1, 1], laps.Skip(8).Take(4).Select(l => l.StintIndex));
    }

    [Fact]
    public void Engine_reports_button_presses_but_not_holds_or_releases()
    {
        var engine = new SessionEngine();
        var pressed = new List<uint>();
        engine.ButtonsPressed += pressed.Add;
        var action2 = EventPacket.UdpActionMask(2);
        var action5 = EventPacket.UdpActionMask(5);
        void Buttons(uint status) => engine.Process(new EventPacket(Header(PacketId.Event), EventPacket.Buttons, status));

        Buttons(action2);           // press
        Buttons(action2);           // still held
        Buttons(action2 | action5); // second button while holding the first
        Buttons(0);                 // release both
        Buttons(action2);           // press again
        engine.Process(new EventPacket(Header(PacketId.Event), EventPacket.Flashback));

        Assert.Equal([action2, action5, action2], pressed);
    }

    [Theory]
    [InlineData("VERSTAPPEN", "VER")]
    [InlineData("Lando Norris", "NOR")]
    [InlineData("Zhou", "ZHO")]
    [InlineData("Li", "LI")]
    public void Driver_codes_come_from_the_surname(string name, string code) => Assert.Equal(code, FieldTracker.DriverCode(name));
}
