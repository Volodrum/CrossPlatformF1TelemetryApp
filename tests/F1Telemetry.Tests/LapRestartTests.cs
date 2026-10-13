using DuckDB.NET.Data;
using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Models;
using F1Telemetry.Core.Recording;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Packets;
using F1Telemetry.Simulation;
using F1Telemetry.Storage;

namespace F1Telemetry.Tests;

/// <summary>
/// Outside races the game keeps a lap number after a run that ends in the pits: the abandoned lap, the in-lap, the garage
/// and the out-lap carry the number of the flying lap after them. Only that flying lap may be stored as the lap.
/// </summary>
public sealed class LapRestartTests : IAsyncLifetime
{
    private const byte Player = 0;
    private const int ShortQualifying = 8;
    private const int Race = 15;
    private static readonly PacketWriter Writer = new(FormatLayout.F1_25);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "f1tel-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>A point of a drive: the lap number and distance the game reports, and which stretch of driving it is.</summary>
    private sealed record Point(double Time, int Lap, float Distance, string Stretch);

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

    /// <summary>Two qualifying runs as the game numbers them (Singapore, recording 23 of the issue).</summary>
    private static List<Point> QualifyingRuns()
    {
        var points = new List<Point>();
        void Stretch(string name, int lap, IEnumerable<float> distances) =>
            points.AddRange(distances.Select(d => new Point(points.Count * 0.5, lap, d, name)));
        IEnumerable<float> Lap(float from = 0, float to = 4900) => Enumerable.Range(0, (int)((to - from) / 100) + 1).Select(i => from + i * 100);

        Stretch("first out-lap", 1, Lap(-4900, -100));
        Stretch("flying 1", 1, Lap());
        Stretch("abandoned into the pits", 2, Lap());
        Stretch("garage and out-lap", 2, [0, .. Enumerable.Repeat(80f, 10), .. Lap(100)]);
        Stretch("flying 2", 2, Lap());
        Stretch("lap 3", 3, Lap(0, 1000));
        return points;
    }

    [Fact]
    public async Task Qualifying_lap_holds_only_its_flying_lap()
    {
        var points = QualifyingRuns();
        await using var store = await RecordAsync("qualifying.duckdb", ShortQualifying, points);
        var id = (await store.GetRecordingsAsync(TestContext.Current.CancellationToken)).Single().Id;

        Assert.Equal([1, 2, 3], (await store.GetLapsAsync(id, TestContext.Current.CancellationToken)).Select(l => l.LapNumber));
        await AssertLapIs(store, id, 1, points, "flying 1");
        await AssertLapIs(store, id, 2, points, "flying 2");
        await AssertLapIs(store, id, 3, points, "lap 3");
    }

    /// <summary>In a race every line crossing counts: a lap is stored as driven.</summary>
    [Fact]
    public async Task Race_laps_are_stored_as_driven()
    {
        var points = QualifyingRuns().Where(p => p.Distance >= 0).ToList();
        await using var store = await RecordAsync("race.duckdb", Race, points);
        var id = (await store.GetRecordingsAsync(TestContext.Current.CancellationToken)).Single().Id;

        var lap2 = await store.GetLapSamplesAsync(id, 2, TestContext.Current.CancellationToken);
        Assert.Equal(points.Count(p => p.Lap == 2), lap2.Count);
    }

    /// <summary>Recordings made before the fix are corrected by the v5 migration, races left alone.</summary>
    [Fact]
    public async Task Migration_corrects_earlier_recordings()
    {
        var path = Path.Combine(_dir, "migrate.duckdb");
        var points = QualifyingRuns();
        long qualifying, race;
        await using (var store = new DuckDbTelemetryStore(path))
        {
            await store.InitializeAsync(TestContext.Current.CancellationToken);
            qualifying = store.BeginRecording(new NewRecording("1", "Before the fix", 13, "Singapore", ShortQualifying, GameFormat.F1_25));
            race = store.BeginRecording(new NewRecording("2", "A race", 13, "Singapore", Race, GameFormat.F1_25));
            foreach (var p in points)
            {
                store.AppendSample(new TelemetrySample { RecordingId = qualifying, LapNumber = p.Lap, SessionTime = p.Time, LapDistance = p.Distance });
                store.AppendSample(new TelemetrySample { RecordingId = race, LapNumber = p.Lap, SessionTime = p.Time, LapDistance = p.Distance });
            }

            await store.FlushAsync(TestContext.Current.CancellationToken);
        }

        using (var connection = new DuckDBConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM schema_info WHERE version >= 5";
            command.ExecuteNonQuery();
        }

        await using (var store = new DuckDbTelemetryStore(path))
        {
            await store.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Equal([1, 2, 3], (await store.GetLapsAsync(qualifying, TestContext.Current.CancellationToken)).Select(l => l.LapNumber));
            await AssertLapIs(store, qualifying, 1, points, "flying 1");
            await AssertLapIs(store, qualifying, 2, points, "flying 2");
            Assert.Equal(points.Count(p => p.Lap == 2), (await store.GetLapSamplesAsync(race, 2, TestContext.Current.CancellationToken)).Count);
        }
    }

    private static async Task AssertLapIs(DuckDbTelemetryStore store, long id, int lap, List<Point> points, string stretch)
    {
        var samples = await store.GetLapSamplesAsync(id, lap, TestContext.Current.CancellationToken);
        Assert.Equal(points.Where(p => p.Stretch == stretch).Select(p => p.Time), samples.Select(s => (double)s.SessionTime));
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
