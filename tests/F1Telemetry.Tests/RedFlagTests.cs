using F1Telemetry.Core.Analytics;
using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Packets;
using F1Telemetry.Simulation;

namespace F1Telemetry.Tests;

/// <summary>
/// Under a red flag the field drives into the pit lane and may change tyres there for free. That set change used to make
/// the red-flag lap and the restart a pit stop costing minutes, which went into the pit-loss median of the race plans.
/// </summary>
public class RedFlagTests
{
    private const byte Player = 0;
    private const byte Medium = 17;
    private const byte Hard = 18;
    private static readonly PacketWriter Writer = new(FormatLayout.F1_25);

    private readonly SessionEngine _engine = new();
    private IReadOnlyList<LapRecord> _laps = [];

    public RedFlagTests() => _engine.LapsUpdated += laps => _laps = laps;

    [Fact]
    public void Standing_restart_after_a_red_flag_marks_the_stopped_lap_and_the_restart_lap()
    {
        Race(redFlags: 0);
        LapData(1);
        LapData(2);
        LapData(3);
        Event(EventPacket.RedFlag);
        LapData(3, PitStatus.InPitArea);
        LapData(3, PitStatus.None, DriverStatus.InGarage); // waiting, hards fitted
        LapData(4); // put on the grid
        Event(EventPacket.LightsOut);
        LapData(4);
        LapData(5);
        LapData(6);
        History(6, stintEnd: 3);

        Assert.Equal(
            [LapType.Regular, LapType.Regular, LapType.RedFlag, LapType.RedFlag, LapType.Regular, LapType.Regular],
            _laps.Select(l => l.LapType));
        Assert.Equal(["Medium", "Medium", "Medium", "Hard", "Hard", "Hard"], _laps.Select(l => l.Compound));
        Assert.Empty(PitStopAnalyzer.Analyze(LapClassifier.Classify(_laps, 15), 15));
    }

    [Fact]
    public void Red_flag_count_in_the_session_packet_stands_in_for_a_lost_event()
    {
        Race(redFlags: 0);
        LapData(1);
        LapData(2);
        Race(redFlags: 1);
        LapData(2, PitStatus.InPitArea);
        LapData(2, PitStatus.Pitting); // restart from the pit lane
        LapData(2); // back on track
        LapData(3);
        LapData(4);
        History(4, stintEnd: 2);

        Assert.Equal([LapType.Regular, LapType.RedFlag, LapType.Regular, LapType.Regular], _laps.Select(l => l.LapType));
    }

    [Fact]
    public void Real_stop_after_a_red_flag_is_still_a_pit_stop()
    {
        Race(redFlags: 0);
        LapData(1);
        Event(EventPacket.RedFlag);
        LapData(1, PitStatus.InPitArea);
        LapData(1);
        LapData(2);
        LapData(3);
        LapData(4, PitStatus.Pitting);
        LapData(5);
        LapData(6);
        History(6, stintEnd: 4);

        Assert.Equal(
            [LapType.RedFlag, LapType.Regular, LapType.Regular, LapType.Pit, LapType.Pit, LapType.Regular],
            _laps.Select(l => l.LapType));
    }

    private void LapData(int lapNum, PitStatus pit = PitStatus.None, DriverStatus driver = DriverStatus.OnTrack)
    {
        var packet = Writer.Create(PacketId.LapData, 1, lapNum * 90, 0, Player);
        Writer.WriteLapData(packet, Player, new LapData(0, 1_000, 0, 0, 0, 0, 10, 0, 0, 1, (byte)lapNum, pit, 0, 0,
            false, 0, 0, 0, 0, 0, 1, driver, ResultStatus.Active, false, 0, 0, 0));
        _engine.Process(PacketParser.Parse(packet).Packet!);
    }

    private void Event(string code)
    {
        var packet = Writer.Create(PacketId.Event, 1, 0, 0, Player);
        Writer.WriteEventCode(packet, code);
        _engine.Process(PacketParser.Parse(packet).Packet!);
    }

    private void Race(byte redFlags)
    {
        var packet = Writer.Create(PacketId.Session, 1, 0, 0, Player);
        Writer.WriteSession(packet, new SessionData(0, 30, 20, 10, 5000, 15, 11, 0, 3600, 3600, 80, false, 0, false, [], 0, 0, 0, 0, 0, 0, 0,
            redFlags, 1600, 3300, null));
        _engine.Process(PacketParser.Parse(packet).Packet!);
    }

    private void History(int laps, byte stintEnd)
    {
        var packet = Writer.Create(PacketId.SessionHistory, 1, 0, 0, Player);
        var history = Enumerable.Range(1, laps).Select(_ => new LapHistory(90_000, 30_000, 30_000, 30_000, 0x0F)).ToArray();
        Writer.WriteSessionHistory(packet, Player, history, [new TyreStint(stintEnd, Medium, Medium), new TyreStint(TyreStint.Current, Hard, Hard)],
            1, 1, 1, 1);
        _engine.Process(PacketParser.Parse(packet).Packet!);
    }
}
