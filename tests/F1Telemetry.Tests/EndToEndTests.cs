using F1Telemetry.Core;
using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Models;
using F1Telemetry.Core.Recording;
using F1Telemetry.Core.Tracks;
using F1Telemetry.Ingest;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Lookups;
using F1Telemetry.Simulation;
using F1Telemetry.Storage;

namespace F1Telemetry.Tests;

/// <summary>Simulator → parser → engine → coordinator → DuckDB → analytics, for both UDP formats.</summary>
public sealed class EndToEndTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "f1tel-tests-" + Guid.NewGuid().ToString("N"));

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public void Srtt_track_outline_loads()
    {
        var monza = SrttReader.Read(Path.Combine(AppContext.BaseDirectory, "tracks", "Monza.srtt"));
        Assert.True(monza.Left.Count > 100);
        Assert.True(monza.Right.Count > 100);
        Assert.InRange(monza.MaxX - monza.MinX, 500, 3000);
    }

    [Fact]
    public void Curated_json_maps_cover_every_track_with_a_map_and_match_srtt()
    {
        var library = new TrackLibrary(Path.Combine(AppContext.BaseDirectory, "track_maps"));
        foreach (var track in Tracks.All.Where(t => t.MapFile is not null))
        {
            var outline = library.Get(track.MapFile);
            Assert.True(outline is { Left.Count: > 100, Right.Count: > 100 }, $"No JSON map for {track.Name} ({track.MapFile})");
        }

        var json = library.Get("Monza")!;
        var srtt = SrttReader.Read(Path.Combine(AppContext.BaseDirectory, "tracks", "Monza.srtt"));
        Assert.Equal(srtt.Left.Count, json.Left.Count);
        Assert.Equal(srtt.Left[10].X, json.Left[10].X, 3);
        Assert.Equal(srtt.Right[10].Z, json.Right[10].Z, 3);
    }

    [Theory]
    [InlineData(GameFormat.F1_25)]
    [InlineData(GameFormat.F1_26)]
    public async Task Simulated_race_is_recorded_and_analysed(GameFormat format)
    {
        var track = new TrackLibrary(Path.Combine(AppContext.BaseDirectory, "track_maps")).Get("Monza");
        var options = new SimulationOptions { Format = format, Track = track, Laps = 5, PitOnLap = 2, TickRateHz = 20 };

        await using var store = new DuckDbTelemetryStore(Path.Combine(_dir, $"{format}.duckdb"));
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        var engine = new SessionEngine();
        using var coordinator = new RecordingCoordinator(engine, store);
        var stats = new PacketStatistics();
        var deltas = new List<DeltaUpdate>();
        var radarDanger = false;
        StrategySnapshot? lastStrategy = null;
        FieldSnapshot? lastField = null;
        var damageHits = 0;
        var finishedLaps = new HashSet<uint>();
        engine.FieldUpdated += f => lastField = f;
        engine.DamageTaken += _ => damageHits++;
        engine.SectorBoxUpdated += b =>
        {
            if (b.IsFinished)
            {
                finishedLaps.Add(b.LapTimeMs);
            }
        };
        engine.DeltaUpdated += deltas.Add;
        engine.RadarUpdated += f => radarDanger |= f.RightDanger;
        engine.StrategyUpdated += s => lastStrategy = s;

        var started = false;
        foreach (var datagram in new SessionSimulator(options).Generate(TestContext.Current.CancellationToken))
        {
            var result = PacketParser.Parse(datagram.Data);
            stats.Record(result);
            Assert.True(result.IsSuccess, $"{result.Status} for packet {result.Header?.PacketId}");
            engine.Process(result.Packet!);
            if (!started && engine.Session is { TrackId: >= 0 })
            {
                coordinator.Start("E2E");
                started = true;
            }
        }

        coordinator.Stop();
        await store.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, stats.RejectedSize);
        Assert.Equal(format, stats.LastFormat);
        Assert.True(radarDanger, "AI car sweeping alongside should trigger side danger");
        Assert.Contains(deltas, d => d.SectorNumber == 3);
        Assert.NotNull(lastStrategy);
        Assert.NotNull(lastStrategy.FuelLapsRemaining);

        // Overlays: the whole simulated field with names and battery, one front-wing hit, and flying laps held in the sector box.
        Assert.NotNull(lastField);
        Assert.Equal(12, lastField.Cars.Count);
        Assert.Equal(Enumerable.Range(1, 12), lastField.Cars.Select(c => c.Position));
        Assert.Equal("YOU", lastField.Player!.Code);
        Assert.Contains(lastField.Cars, c => c.Code == "VER" && c.ErsPercent is > 0 and <= 100);
        Assert.Equal(1, damageHits);
        Assert.NotEmpty(finishedLaps);
        var tower = TimingTower.Build(lastField, TowerGapMode.GapToMe);
        Assert.Equal(6, tower.Rows.Count);
        Assert.Contains(tower.Rows, r => r.IsPlayer);

        var recording = Assert.Single(await store.GetRecordingsAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Monza", recording.TrackName);
        Assert.Equal(15, recording.SessionType);
        Assert.Equal(format, recording.Format);
        Assert.NotNull(recording.EndTime);

        var analysis = new SessionAnalysisService(store);
        var laps = await analysis.GetClassifiedLapsAsync(recording, TestContext.Current.CancellationToken);
        Assert.Equal([1, 2, 3, 4, 5], laps.Select(l => l.LapNumber));
        Assert.All(laps, l => Assert.True(l.HasTime));
        Assert.All(laps, l => Assert.Equal(l.LapTimeMs, l.Sector1Ms + l.Sector2Ms + l.Sector3Ms)); // incl. the final lap, missing from the game's history
        Assert.Equal([LapType.Regular, LapType.Pit, LapType.Pit, LapType.Regular, LapType.Regular], laps.Select(l => l.LapType));
        Assert.Equal(VisualCompound.Hard.ToString(), laps[^1].Compound);
        Assert.Single(laps, l => l.IsBestLap);
        Assert.Contains(laps[^1].LapTimeMs, finishedLaps); // the sector box finished the final lap at the flag

        var finalLap = await store.GetLapSamplesAsync(recording.Id, 5, TestContext.Current.CancellationToken);
        Assert.True(finalLap.MaxBy(s => s.SessionTime)!.LapDistance > 1000); // nothing from past the flag

        var samples = await store.GetLapSamplesAsync(recording.Id, 3, TestContext.Current.CancellationToken);
        Assert.True(samples.Count > 100);
        Assert.All(samples, s => Assert.Equal(recording.Id, s.RecordingId));
        Assert.Contains(samples, s => s.Speed > 250);
        Assert.Contains(samples, s => s.Brake > 0.5);
        if (format == GameFormat.F1_26)
        {
            Assert.Contains(samples, s => s.OvertakeActive == 1);
        }

        var result2 = (await analysis.AnalyzeAsync(recording, laps, TestContext.Current.CancellationToken))!;
        Assert.Equal(2, result2.Stints.Count);
        Assert.InRange(result2.Stints[0].AverageFuelPerLap, 1.5, 2.0);
        Assert.InRange(result2.Stints[0].AverageWearPerLap.Fr, 2.3, 2.9);
        Assert.InRange(result2.Stints[1].AverageWearPerLap.Fr, 1.6, 2.1); // hard compound wears slower
        Assert.NotNull(result2.Stints[1].LimitingTyre);

        var positions = await store.GetPositionHistoryAsync(recording.Id, 100, TestContext.Current.CancellationToken);
        Assert.InRange(positions.Count, 50, 110);
        Assert.Contains(positions, p => p.Position == 7); // dropped after the stop
    }

    [Theory]
    [InlineData(GameFormat.F1_26, GameFormat.F1_25)]
    [InlineData(GameFormat.F1_25, GameFormat.F1_26)]
    public async Task Pipeline_only_processes_packets_of_the_selected_mode(GameFormat mode, GameFormat sent)
    {
        var engine = new SessionEngine();
        var stats = new PacketStatistics();
        var processed = 0;
        engine.SampleCaptured += _ => Interlocked.Increment(ref processed);
        await using var pipeline = new TelemetryPipeline(engine, stats) { Mode = mode };

        var expected = new SessionSimulator(new SimulationOptions { Format = sent, Laps = 1, PitOnLap = 0, TickRateHz = 5, Seed = 1 }).Generate(TestContext.Current.CancellationToken).Count();
        await pipeline.StartAsync(new SimulatorPacketSource(() => new SimulationOptions { Format = sent, Laps = 1, PitOnLap = 0, TickRateHz = 5, Seed = 1 }, speed: 0, loop: false));
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (stats.Total < expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Equal(expected, stats.Total);
        Assert.Equal(expected, stats.RejectedMode);
        Assert.Equal(sent, stats.LastMismatchedFormat);
        Assert.Equal(0, processed);
        Assert.Null(engine.Session);
    }

    [Fact]
    public async Task Raw_capture_round_trips_through_replay()
    {
        var path = Path.Combine(_dir, "capture.f1rec");
        var sim = new SessionSimulator(new SimulationOptions { Laps = 1, PitOnLap = 0, TickRateHz = 10 });
        var written = 0;
        await using (var writer = PacketFileWriter.Create(path))
        {
            foreach (var d in sim.Generate(TestContext.Current.CancellationToken))
            {
                writer.Write(new RawPacket(d.Time, d.Data));
                written++;
            }
        }

        var replayed = 0;
        await foreach (var packet in new ReplayPacketSource(path, speed: 0).ReadAllAsync(TestContext.Current.CancellationToken))
        {
            Assert.True(PacketParser.Parse(packet.Data).IsSuccess);
            replayed++;
        }

        Assert.Equal(written, replayed);
    }

    [Fact]
    public async Task Session_change_during_recording_auto_splits()
    {
        await using var store = new DuckDbTelemetryStore(Path.Combine(_dir, "split.duckdb"));
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var engine = new SessionEngine();
        using var coordinator = new RecordingCoordinator(engine, store);
        var changes = new List<RecordingChange>();
        coordinator.StateChanged += s => changes.Add(s.Change);

        void Run(ulong uid)
        {
            foreach (var d in new SessionSimulator(new SimulationOptions { Laps = 1, PitOnLap = 0, TickRateHz = 10, SessionUid = uid }).Generate())
            {
                engine.Process(PacketParser.Parse(d.Data).Packet!);
                if (!coordinator.IsRecording)
                {
                    coordinator.Start("Manual Session");
                }
            }
        }

        Run(111);
        Run(222);
        coordinator.Stop();
        await store.FlushAsync(TestContext.Current.CancellationToken);

        var recordings = await store.GetRecordingsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, recordings.Count);
        Assert.Contains(recordings, r => r.Description == "Manual Session (Auto-Split)" && r.SessionUid == "222");
        Assert.Equal([RecordingChange.Started, RecordingChange.AutoSplit, RecordingChange.Stopped], changes);
    }

    /// <summary>
    /// Leaving a session, the game sends a burst of packets with session UID 0 and then the old UID again. That used
    /// to count as two session changes and auto-split the recording into an extra, empty one.
    /// </summary>
    [Fact]
    public async Task Session_uid_zero_burst_does_not_split_the_recording()
    {
        await using var store = new DuckDbTelemetryStore(Path.Combine(_dir, "nosplit.duckdb"));
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var engine = new SessionEngine();
        using var coordinator = new RecordingCoordinator(engine, store);
        var changes = new List<RecordingChange>();
        coordinator.StateChanged += s => changes.Add(s.Change);
        var sessionChanges = 0;
        engine.SessionChanged += (_, _) => sessionChanges++;

        IEnumerable<byte[]> Packets(ulong uid) =>
            new SessionSimulator(new SimulationOptions { Laps = 1, PitOnLap = 0, TickRateHz = 10, SessionUid = uid }).Generate().Select(d => d.Data);

        foreach (var data in Packets(111))
        {
            engine.Process(PacketParser.Parse(data).Packet!);
            if (!coordinator.IsRecording)
            {
                coordinator.Start("Manual Session");
            }
        }

        foreach (var data in Packets(0).Take(50))
        {
            engine.Process(PacketParser.Parse(data).Packet!);
        }

        foreach (var data in Packets(111).Take(50))
        {
            engine.Process(PacketParser.Parse(data).Packet!);
        }

        coordinator.Stop();
        await store.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, sessionChanges);
        Assert.Single(await store.GetRecordingsAsync(TestContext.Current.CancellationToken));
        Assert.Equal([RecordingChange.Started, RecordingChange.Stopped], changes);
    }
}
