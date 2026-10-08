using DuckDB.NET.Data;
using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Models;
using F1Telemetry.Core.Recording;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Packets;
using F1Telemetry.Simulation;
using F1Telemetry.Storage;

namespace F1Telemetry.Tests;

/// <summary>The player's setup through the engine and the store, and the v3 schema on an existing database.</summary>
public sealed class SetupAndEnergyTests : IAsyncLifetime
{
    private static readonly CarSetup Spa = new(18, 12, 100, 25, -3.4f, -1.9f, 0.01f, 0.12f, 37, 17, 15, 8, 24, 51, 100, 55, 50,
        new Tyres<float>(24.2f, 24.2f, 28.0f, 28.0f), 6, 6);

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
    public void Engine_reports_a_setup_once_and_again_only_when_it_changes()
    {
        var writer = new PacketWriter(FormatLayout.F1_26);
        var engine = new SessionEngine();
        var changes = new List<SetupChange>();
        engine.SetupChanged += changes.Add;

        void Send(ulong uid, float time, CarSetup setup)
        {
            var packet = writer.Create(PacketId.CarSetups, uid, time, 0, 0);
            writer.WriteCarSetup(packet, 0, setup);
            engine.Process(PacketParser.Parse(packet).Packet!);
        }

        Send(7, 1, default);                                  // nothing loaded yet
        Send(7, 2, Spa);
        Send(7, 3, Spa with { FuelLoad = 5.5f });             // the tank, not the setup
        Send(7, 4, Spa with { RearAntiRollBar = 11 });
        Send(7, 5, Spa with { RearAntiRollBar = 11 });

        Assert.Equal([2.0, 4.0], changes.Select(c => c.SessionTime));
        Assert.Equal(11, engine.CurrentSetup!.Setup.RearAntiRollBar);

        Send(8, 1, Spa with { RearAntiRollBar = 11 });        // a new session starts without a known setup
        Assert.Equal(3, changes.Count);
    }

    [Fact]
    public async Task Recording_keeps_the_setup_it_started_with_and_each_change()
    {
        await using var store = new DuckDbTelemetryStore(Path.Combine(_dir, "setups.duckdb"));
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var writer = new PacketWriter(FormatLayout.F1_25);
        var engine = new SessionEngine();
        using var coordinator = new RecordingCoordinator(engine, store);

        void Send(float time, CarSetup setup)
        {
            var packet = writer.Create(PacketId.CarSetups, 9, time, 0, 0);
            writer.WriteCarSetup(packet, 0, setup);
            engine.Process(PacketParser.Parse(packet).Packet!);
        }

        Send(1, Spa);                                         // before recording: stored when it starts
        var id = coordinator.Start("Setups");
        Send(2, Spa);
        Send(3, Spa with { FrontWing = 19 });
        coordinator.Stop();
        Send(4, Spa with { FrontWing = 20 });                 // after recording: not stored
        await store.FlushAsync(TestContext.Current.CancellationToken);

        var setups = await store.GetSetupsAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal([1.0, 3.0], setups.Select(s => s.SessionTime));
        Assert.Equal(Spa, setups[0].Setup);
        Assert.Equal(Spa with { FrontWing = 19 }, setups[1].Setup);

        await store.DeleteRecordingAsync(id, TestContext.Current.CancellationToken);
        Assert.Empty(await store.GetSetupsAsync(id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Version_2_database_gains_the_energy_columns_and_setups_table()
    {
        var path = Path.Combine(_dir, "v2.duckdb");

        // A database as v0.2.0 left it: today's schema without the v3 and v4 columns and tables, one recorded sample.
        await using (var fresh = new DuckDbTelemetryStore(path))
        {
            await fresh.InitializeAsync(TestContext.Current.CancellationToken);
        }

        using (var connection = new DuckDBConnection($"Data Source={path}"))
        {
            connection.Open();
            foreach (var sql in new[]
            {
                "ALTER TABLE telemetry DROP COLUMN engine_power_ice",
                "ALTER TABLE telemetry DROP COLUMN engine_power_mguk",
                "ALTER TABLE telemetry DROP COLUMN ers_harvest_limit",
                "ALTER TABLE telemetry DROP COLUMN active_aero_available",
                "ALTER TABLE telemetry DROP COLUMN overtake_available",
                "DROP TABLE recording_setups",
                "DROP TABLE setups",
                "DELETE FROM schema_info WHERE version >= 3",
                "INSERT INTO recordings (id, description, track_id, track_name, session_type, game_format, start_time) VALUES (1, 'Old', 10, 'Spa', 15, 2026, now())",
                "INSERT INTO telemetry (recording_id, lap_number, session_time, speed) VALUES (1, 1, 0.5, 210)",
            })
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        await using var store = new DuckDbTelemetryStore(path);
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        store.AppendSample(new TelemetrySample
        {
            RecordingId = 1, LapNumber = 2, SessionTime = 100, Speed = 288, ErsStoreEnergy = 1_980_000, ErsDeployMode = 3,
            EnginePowerIce = 410_000, EnginePowerMguk = 315_000, ErsHarvestLimit = 7_100_000, ActiveAeroAvailable = 1, OvertakeAvailable = 1,
            ActiveAeroMode = 1, OvertakeActive = 1,
        });
        store.AppendSetup(1, new SetupChange(2, 100, Spa));
        await store.FlushAsync(TestContext.Current.CancellationToken);

        var old = Assert.Single(await store.GetLapSamplesAsync(1, 1, TestContext.Current.CancellationToken));
        Assert.Equal(210, old.Speed);
        Assert.Equal(0, old.EnginePowerMguk);

        var sample = Assert.Single(await store.GetLapSamplesAsync(1, 2, TestContext.Current.CancellationToken));
        Assert.Equal(288, sample.Speed);
        Assert.Equal(3, sample.ErsDeployMode);
        Assert.Equal(410_000, sample.EnginePowerIce);
        Assert.Equal(315_000, sample.EnginePowerMguk);
        Assert.Equal(7_100_000, sample.ErsHarvestLimit);
        Assert.Equal(1, sample.ActiveAeroMode);
        Assert.Equal(1, sample.ActiveAeroAvailable);
        Assert.Equal(1, sample.OvertakeActive);
        Assert.Equal(1, sample.OvertakeAvailable);

        Assert.Equal(Spa, Assert.Single(await store.GetSetupsAsync(1, TestContext.Current.CancellationToken)).Setup);
    }
}
