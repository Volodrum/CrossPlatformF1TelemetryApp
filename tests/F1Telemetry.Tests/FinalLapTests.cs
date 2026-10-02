using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Packets;
using F1Telemetry.Simulation;

namespace F1Telemetry.Tests;

/// <summary>
/// At the flag the game stops updating the player's session history, so the final lap has S1 and S2 but no lap time or
/// S3. The engine fills it in from the lap data (the lap number stays, the car is classified) or the final classification.
/// </summary>
public class FinalLapTests
{
    private const byte Player = 0;
    private static readonly PacketWriter Writer = new(FormatLayout.F1_25);

    private static readonly LapHistory[] RaceHistory =
    [
        new(93_127, 29_925, 40_576, 22_626, 0x0F),
        new(92_884, 29_666, 40_317, 22_901, 0x0F),
        new(0, 29_507, 40_121, 0, 0x0F), // final lap, never completed in the history
    ];

    private readonly SessionEngine _engine = new();
    private IReadOnlyList<LapRecord> _laps = [];

    public FinalLapTests() => _engine.LapsUpdated += laps => _laps = laps;

    [Fact]
    public void Final_lap_is_timed_from_the_lap_data_at_the_flag()
    {
        LapData(lapNum: 3, lastLapMs: 92_884, ResultStatus.Active);
        History(RaceHistory);
        Assert.Equal(0u, _laps[2].LapTimeMs);

        LapData(lapNum: 3, lastLapMs: 91_731, ResultStatus.Finished);

        Assert.Equal(91_731u, _laps[2].LapTimeMs);
        Assert.Equal(91_731u - 29_507 - 40_121, _laps[2].Sector3Ms);
        Assert.True(_laps[2].IsBestLap);
        Assert.False(_laps[1].IsBestLap);
        Assert.Equal(92_884u, _laps[1].LapTimeMs);
    }

    [Fact]
    public void Final_lap_as_fast_as_the_one_before_is_timed_when_the_lap_timer_restarts()
    {
        SectorBoxSnapshot? box = null;
        _engine.SectorBoxUpdated += b => box = b;
        LapData(lapNum: 3, lastLapMs: 92_884, ResultStatus.Active, time: 100, currentMs: 30_000, sector: 1, s1: 29_507);
        LapData(lapNum: 3, lastLapMs: 92_884, ResultStatus.Active, time: 160, currentMs: 90_000, sector: 2, s1: 29_507, s2: 40_121);
        History(RaceHistory);

        LapData(lapNum: 3, lastLapMs: 92_884, ResultStatus.Finished, time: 163, currentMs: 50);

        Assert.Equal(92_884u, _laps[2].LapTimeMs);
        Assert.True(box!.IsFinished);
        Assert.Equal(92_884u, box.LapTimeMs);
    }

    [Fact]
    public void Final_lap_is_timed_from_the_final_classification()
    {
        LapData(lapNum: 3, lastLapMs: 92_884, ResultStatus.Active);
        History(RaceHistory);

        var packet = Writer.Create(PacketId.FinalClassification, 1, 300, 0, Player);
        Writer.WriteFinalClassification(packet, Player, new FinalClassification(1, 3, 1, 25, 0, ResultStatus.Finished, 92_884, 278.7431, 0));
        _engine.Process(PacketParser.Parse(packet).Packet!);

        Assert.Equal(278_743u - 93_127 - 92_884, _laps[2].LapTimeMs);
        Assert.Equal(_laps[2].LapTimeMs, _laps[2].Sector1Ms + _laps[2].Sector2Ms + _laps[2].Sector3Ms);
    }

    [Fact]
    public void Lap_in_progress_never_takes_a_time_undone_by_a_flashback()
    {
        LapData(lapNum: 3, lastLapMs: 92_884, ResultStatus.Active);
        LapData(lapNum: 4, lastLapMs: 92_500, ResultStatus.Active); // lap 3 done...
        LapData(lapNum: 3, lastLapMs: 92_884, ResultStatus.Active); // ...and flashed back
        History(RaceHistory);

        Assert.Equal(0u, _laps[2].LapTimeMs);
        Assert.Equal(0u, _laps[2].Sector3Ms);
    }

    [Fact]
    public void Sector_box_shows_the_final_lap_at_the_flag_and_keeps_it()
    {
        SectorBoxSnapshot? box = null;
        _engine.SectorBoxUpdated += b => box = b;

        LapData(lapNum: 3, lastLapMs: 92_884, ResultStatus.Active, time: 100);
        LapData(lapNum: 3, lastLapMs: 92_884, ResultStatus.Active, time: 130, sector: 1, s1: 29_507);
        LapData(lapNum: 3, lastLapMs: 92_884, ResultStatus.Active, time: 170, sector: 2, s1: 29_507, s2: 40_121);
        LapData(lapNum: 3, lastLapMs: 92_884, ResultStatus.Finished, time: 192); // classified a packet before the time is in
        Assert.False(box!.IsFinished);

        LapData(lapNum: 3, lastLapMs: 91_731, ResultStatus.Finished, time: 192.05);
        Assert.True(box.IsFinished);
        Assert.Equal(91_731u, box.LapTimeMs);
        Assert.Equal(91_731u - 29_507 - 40_121, box.LastSplit!.SectorMs);

        LapData(lapNum: 3, lastLapMs: 91_731, ResultStatus.Finished, time: 230, sector: 1, s1: 35_000); // cool-down lap
        Assert.True(box.IsFinished);
        Assert.Equal(91_731u, box.LapTimeMs);
    }

    private void LapData(int lapNum, uint lastLapMs, ResultStatus result, double time = 0, uint currentMs = 0, byte sector = 0, uint s1 = 0, uint s2 = 0)
    {
        var packet = Writer.Create(PacketId.LapData, 1, (float)time, 0, Player);
        Writer.WriteLapData(packet, Player, new LapData(lastLapMs, currentMs, s1, s2, 0, 0, 10, 0, 0, 1, (byte)lapNum, PitStatus.None, 0, sector,
            false, 0, 0, 0, 1, DriverStatus.OnTrack, result, false, 0, 0, 0));
        _engine.Process(PacketParser.Parse(packet).Packet!);
    }

    private void History(LapHistory[] laps)
    {
        var packet = Writer.Create(PacketId.SessionHistory, 1, 0, 0, Player);
        Writer.WriteSessionHistory(packet, Player, laps, [new TyreStint(TyreStint.Current, 18, 17)], 2, 2, 2, 1);
        _engine.Process(PacketParser.Parse(packet).Packet!);
    }
}
