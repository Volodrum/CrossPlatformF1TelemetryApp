using DuckDB.NET.Data;
using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Models;
using F1Telemetry.Core.Recording;
using F1Telemetry.Ingest;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Packets;
using F1Telemetry.Simulation;
using F1Telemetry.Storage;

namespace F1Telemetry.Tests;

/// <summary>What reaches the database, and what happens when writing it goes wrong.</summary>
public sealed class RecordingWriteTests : IAsyncLifetime
{
    private const byte Player = 0;
    private const int Race = 15;
    private static readonly PacketWriter Writer = new(FormatLayout.F1_25);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "f1tel-tests-" + Guid.NewGuid().ToString("N"));

    private sealed record Point(double Time, int Lap, float Distance);

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

    /// <summary>
    /// A flashback from lap 2 back into lap 1: the driving after the point it rewound to is driven again, and only the
    /// second time counts. It used to be the other way round: the undone driving stayed and the new one was skipped.
    /// </summary>
    [Fact]
    public async Task Flashback_keeps_the_driving_that_replaced_the_undone_one()
    {
        var undone = new List<Point>();
        for (var i = 0; i < 50; i++)
        {
            undone.Add(new Point(i * 0.5, 1, i * 100));
        }

        for (var i = 0; i < 20; i++)
        {
            undone.Add(new Point(25 + i * 0.5, 2, i * 100));
        }

        // Back to 15 s (lap 1, 3000 m), then a different line: 3050 m on.
        var redriven = new List<Point>();
        for (var i = 0; i < 20; i++)
        {
            redriven.Add(new Point(15 + i * 0.5, 1, 3050 + i * 100));
        }

        for (var i = 0; i < 10; i++)
        {
            redriven.Add(new Point(25 + i * 0.5, 2, 50 + i * 100));
        }

        var kept = undone.Where(p => p.Time < 15).Concat(redriven).ToList();
        await using var store = await RecordAsync("flashback.duckdb", Race, [.. undone, .. redriven]);
        var id = (await store.GetRecordingsAsync(TestContext.Current.CancellationToken)).Single().Id;

        foreach (var lap in new[] { 1, 2 })
        {
            var samples = await store.GetLapSamplesAsync(id, lap, TestContext.Current.CancellationToken);
            Assert.Equal(kept.Where(p => p.Lap == lap).Select(p => (p.Time, p.Distance)),
                samples.Select(s => (s.SessionTime, (float)s.LapDistance)));
        }
    }

    /// <summary>A packet arriving a frame late is not a flashback: it is dropped, and nothing stored is.</summary>
    [Fact]
    public async Task Late_packet_is_not_a_flashback()
    {
        List<Point> points = [new(0, 1, 0), new(0.1, 1, 10), new(0.2, 1, 20), new(0.15, 1, 15), new(0.3, 1, 30)];
        await using var store = await RecordAsync("late.duckdb", Race, points);
        var id = (await store.GetRecordingsAsync(TestContext.Current.CancellationToken)).Single().Id;

        var samples = await store.GetLapSamplesAsync(id, 1, TestContext.Current.CancellationToken);
        Assert.Equal([0, 0.1, 0.2, 0.3], samples.Select(s => Math.Round(s.SessionTime, 2)));
    }

    /// <summary>
    /// Recording started before the game sends anything (or from the command line): the game format and track were
    /// unknown, and the format used to stay F1 25 for good, hiding F1 26 sessions from the ERS model and setup library.
    /// </summary>
    [Fact]
    public async Task Recording_started_before_the_game_takes_its_format_and_track()
    {
        await using var store = new DuckDbTelemetryStore(Path.Combine(_dir, "early.duckdb"));
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var engine = new SessionEngine();
        using (var coordinator = new RecordingCoordinator(engine, store))
        {
            coordinator.Start();
            var options = new SimulationOptions { Format = GameFormat.F1_26, Laps = 1, PitOnLap = 0, TickRateHz = 10, TrackId = 11, SessionUid = 42 };
            foreach (var d in new SessionSimulator(options).Generate(TestContext.Current.CancellationToken))
            {
                engine.Process(PacketParser.Parse(d.Data).Packet!);
            }

            coordinator.Stop();
        }

        await store.FlushAsync(TestContext.Current.CancellationToken);
        var recording = (await store.GetRecordingsAsync(TestContext.Current.CancellationToken)).Single();
        Assert.Equal(GameFormat.F1_26, recording.Format);
        Assert.Equal(11, recording.TrackId);
        Assert.Equal("42", recording.SessionUid);
    }

    /// <summary>A WAL DuckDB can't replay is moved aside, not deleted: it holds everything since the last checkpoint.</summary>
    [Fact]
    public async Task Unreplayable_wal_is_set_aside_not_deleted()
    {
        var path = Path.Combine(_dir, "wal.duckdb");
        await using (var store = new DuckDbTelemetryStore(path))
        {
            await store.InitializeAsync(TestContext.Current.CancellationToken);
        }

        // A WAL from another database: it writes to a table this one doesn't have, so it can't be replayed here.
        var other = Path.Combine(_dir, "other.duckdb");
        using (var connection = new DuckDBConnection($"Data Source={other}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA disable_checkpoint_on_shutdown;
                SET checkpoint_threshold = '1GB';
                CREATE TABLE elsewhere (x INTEGER);
                CHECKPOINT;
                INSERT INTO elsewhere VALUES (1), (2), (3);
                """;
            command.ExecuteNonQuery();
        }

        var garbage = await File.ReadAllBytesAsync(other + ".wal", TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(path + ".wal", garbage, TestContext.Current.CancellationToken);

        await using (var store = new DuckDbTelemetryStore(path))
        {
            await store.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Empty(await store.GetRecordingsAsync(TestContext.Current.CancellationToken));
        }

        var aside = Assert.Single(Directory.GetFiles(_dir, "wal.duckdb.wal.*.unreplayed"));
        Assert.Equal(garbage, await File.ReadAllBytesAsync(aside, TestContext.Current.CancellationToken));
    }

    /// <summary>Telemetry that can't be written is reported, not just logged, so the app can say so.</summary>
    [Fact]
    public async Task Failed_write_is_reported()
    {
        var path = Path.Combine(_dir, "broken.duckdb");
        await using var store = new DuckDbTelemetryStore(path);
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var failures = new List<Exception>();
        store.WriteFailed += failures.Add;

        using (var connection = new DuckDBConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE telemetry DROP COLUMN overtake_available";
            command.ExecuteNonQuery();
        }

        var id = store.BeginRecording(new NewRecording("1", "Broken", 11, "Monza", Race, GameFormat.F1_25));
        store.AppendSample(new TelemetrySample { RecordingId = id, LapNumber = 1, SessionTime = 1 });
        await store.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Single(failures);
    }

    /// <summary>Stopping a raw capture from the UI while the ingest thread writes to it must not fail the ingest.</summary>
    [Fact]
    public void Capture_writer_ignores_packets_after_it_is_closed()
    {
        var writer = PacketFileWriter.Create(Path.Combine(_dir, "closed.f1rec"));
        writer.Dispose();

        writer.Write(new RawPacket(TimeSpan.Zero, [1, 2, 3]));
        Assert.Equal(0, writer.PacketCount);
    }

    /// <summary>Drives <paramref name="points"/> through the engine and the recording coordinator into a new store.</summary>
    private async Task<DuckDbTelemetryStore> RecordAsync(string name, int sessionType, List<Point> points)
    {
        var store = new DuckDbTelemetryStore(Path.Combine(_dir, name));
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var engine = new SessionEngine();
        using (var coordinator = new RecordingCoordinator(engine, store))
        {
            var session = Writer.Create(PacketId.Session, 1, 0, 0, Player);
            session[FormatLayout.HeaderSize + 4] = 5000 & 0xFF;
            session[FormatLayout.HeaderSize + 5] = 5000 >> 8;
            session[FormatLayout.HeaderSize + 6] = (byte)sessionType;
            Process(engine, session);
            coordinator.Start();

            foreach (var p in points)
            {
                var lap = Writer.Create(PacketId.LapData, 1, (float)p.Time, 0, Player);
                Writer.WriteLapData(lap, Player, new LapData(0, 0, 0, 0, 0, 0, p.Distance, 0, 0, 1, (byte)p.Lap, PitStatus.None, 0, 0,
                    false, 0, 0, 0, 0, 0, 1, DriverStatus.OnTrack, ResultStatus.Active, false, 0, 0, 0));
                Process(engine, lap);
                Process(engine, Writer.Create(PacketId.CarStatus, 1, (float)p.Time, 0, Player));
                Process(engine, Writer.Create(PacketId.CarDamage, 1, (float)p.Time, 0, Player));
                Process(engine, Writer.Create(PacketId.CarTelemetry, 1, (float)p.Time, 0, Player));
            }

            coordinator.Stop();
        }

        await store.FlushAsync(TestContext.Current.CancellationToken);
        return store;
    }

    private static void Process(SessionEngine engine, byte[] packet) => engine.Process(PacketParser.Parse(packet).Packet!);
}
