using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Models;
using F1Telemetry.Core.Recording;
using F1Telemetry.Core.Setups;
using F1Telemetry.Core.Tracks;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Packets;
using F1Telemetry.Simulation;
using F1Telemetry.Storage;

namespace F1Telemetry.Tests;

public sealed class SetupLibraryTests : IAsyncLifetime
{
    private static readonly CarSetup Spa = new(18, 12, 100, 25, -3.4f, -1.9f, 0.01f, 0.12f, 37, 17, 15, 8, 24, 51, 100, 55, 50,
        new Tyres<float>(24.2f, 24.2f, 28.0f, 28.0f), 6, 6);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "f1tel-tests-" + Guid.NewGuid().ToString("N"));

    private CancellationToken Ct => TestContext.Current.CancellationToken;

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

    private async Task<DuckDbTelemetryStore> OpenAsync(string name)
    {
        var store = new DuckDbTelemetryStore(Path.Combine(_dir, name));
        await store.InitializeAsync(Ct);
        return store;
    }

    [Fact]
    public async Task Simulated_race_fills_the_library_with_each_setup_and_where_it_was_driven()
    {
        await using var store = await OpenAsync("race.duckdb");
        var track = new TrackLibrary(Path.Combine(AppContext.BaseDirectory, "track_maps")).Get("Monza");
        var engine = new SessionEngine();
        using var coordinator = new RecordingCoordinator(engine, store);
        foreach (var datagram in new SessionSimulator(new SimulationOptions { Format = GameFormat.F1_26, Track = track, Laps = 5, PitOnLap = 2, TickRateHz = 20 }).Generate(Ct))
        {
            engine.Process(PacketParser.Parse(datagram.Data).Packet!);
            if (!coordinator.IsRecording && engine.Session is { TrackId: >= 0 })
            {
                coordinator.Start("Library");
            }
        }

        coordinator.Stop();
        await store.FlushAsync(Ct);

        var library = await store.GetSetupLibraryAsync(Ct);
        Assert.Equal(2, library.Count); // the starting setup, then the new front wing from the stop
        Assert.All(library, s => Assert.Equal((GameFormat.F1_26, 11, "Monza", SetupSource.Recorded), (s.Format, s.TrackId, s.TrackName, s.Source)));
        var versions = SetupLibrary.Versions(library);
        Assert.Equal(["Monza · v1", "Monza · v2"], library.Select(s => SetupLibrary.DisplayName(s, versions[s.Id])));
        Assert.Equal(library[0].Setup.FrontWing + 1, library[1].Setup.FrontWing);
        Assert.Same(library[0], SetupLibrary.Previous(library[1], library));
        Assert.Equal(["Front wing"], SetupSettings.Differences(library[0].Setup, library[1].Setup).Select(s => s.Label));

        var runs = await store.GetSetupRunsAsync(Ct);
        Assert.Equal(2, runs.Count);
        var first = runs.Single(r => r.SetupId == library[0].Id);
        var second = runs.Single(r => r.SetupId == library[1].Id);
        Assert.Equal((1, 2), (first.FromLap, first.ToLap));
        Assert.Equal((3, 5), (second.FromLap, second.ToLap));
        Assert.Equal(2, first.Laps);
        Assert.Equal(3, second.Laps);
        Assert.True(first.BestLapMs > 0 && first.TopSpeed > 250);
        Assert.Equal(new SessionConditions(1, 32, 24), first.Conditions);

        // Asking again adds nothing.
        Assert.Equal(2, (await store.GetSetupLibraryAsync(Ct)).Count);
    }

    [Fact]
    public async Task Setups_loaded_in_the_garage_but_never_driven_stay_out()
    {
        await using var store = await OpenAsync("garage.duckdb");
        var id = store.BeginRecording(new NewRecording("1", "Garage", 10, "Spa", 1, GameFormat.F1_26));
        store.AppendSetup(id, new SetupChange(0, 10, Spa with { RearWing = 20 }));
        store.AppendSetup(id, new SetupChange(0, 12, Spa with { RearWing = 25 }));
        store.AppendSetup(id, new SetupChange(0, 14, Spa));
        for (var t = 20; t < 80; t++)
        {
            store.AppendSample(new TelemetrySample { RecordingId = id, LapNumber = 1, SessionTime = t, Speed = 200 });
        }

        var library = await store.GetSetupLibraryAsync(Ct);
        Assert.Equal(Spa with { FuelLoad = 0 }, Assert.Single(library).Setup with { FuelLoad = 0 });
    }

    [Fact]
    public async Task Renamed_starred_and_removed_setups_keep_their_state()
    {
        await using var store = await OpenAsync("edit.duckdb");
        await store.ImportSetupsAsync([new NewSetup(GameFormat.F1_26, 10, "Spa", "", "", Spa)], Ct);
        var setup = Assert.Single(await store.GetSetupLibraryAsync(Ct));
        Assert.Null(setup.Name);

        store.UpdateSetup(setup.Id, name: "  Spa low downforce ", notes: "Rear end loose in Pouhon", favourite: true);
        var edited = Assert.Single(await store.GetSetupLibraryAsync(Ct));
        Assert.Equal(("Spa low downforce", "Rear end loose in Pouhon", true), (edited.Name, edited.Notes, edited.Favourite));

        store.UpdateSetup(setup.Id, name: "");
        Assert.Null(Assert.Single(await store.GetSetupLibraryAsync(Ct)).Name); // back to the automatic name

        store.UpdateSetup(setup.Id, removed: true);
        Assert.Empty(await store.GetSetupLibraryAsync(Ct));
    }

    [Fact]
    public async Task Exported_setups_import_once_into_another_library()
    {
        await using var source = await OpenAsync("source.duckdb");
        await source.ImportSetupsAsync(
        [
            new NewSetup(GameFormat.F1_26, 10, "Spa", "Spa race", "Low wing", Spa),
            new NewSetup(GameFormat.F1_26, 27, "Imola", "", "", Spa with { FrontWing = 39, RearWing = 29 }),
        ], Ct);
        var chosen = (await source.GetSetupLibraryAsync(Ct)).Where(s => s.TrackId == 10).ToList();

        var json = SetupFile.Write(chosen.Select(s => (s, s.Name ?? "", (SetupFile.Stats?)new SetupFile.Stats(108_211, 11))), DateTimeOffset.UtcNow);
        var read = SetupFile.Read(json);
        var setup = Assert.Single(read);
        Assert.Equal(("Spa race", "Low wing", GameFormat.F1_26, 10), (setup.Name, setup.Notes, setup.Format, setup.TrackId));
        Assert.True(setup.Setup.SameSettings(Spa)); // floats survive the file exactly

        await using var target = await OpenAsync("target.duckdb");
        Assert.Equal(new SetupImportResult(1, 0), await target.ImportSetupsAsync(read, Ct));
        Assert.Equal(new SetupImportResult(0, 1), await target.ImportSetupsAsync(read, Ct));

        var imported = Assert.Single(await target.GetSetupLibraryAsync(Ct));
        Assert.Equal(SetupSource.Imported, imported.Source);
        target.UpdateSetup(imported.Id, removed: true);
        Assert.Equal(new SetupImportResult(1, 0), await target.ImportSetupsAsync(read, Ct)); // a removed setup comes back
        Assert.Single(await target.GetSetupLibraryAsync(Ct));
    }

    [Fact]
    public void Files_that_are_not_setup_files_are_refused()
    {
        Assert.Throws<FormatException>(() => SetupFile.Read("not json"));
        Assert.Throws<FormatException>(() => SetupFile.Read("""{ "schema": 99, "setups": [] }"""));
        Assert.Throws<FormatException>(() => SetupFile.Read("""{ "schema": 1, "setups": [ { "format": 2026, "trackId": 10, "values": { "frontWing": 18 } } ] }"""));
        Assert.Throws<FormatException>(() => SetupFile.Read("""{ "schema": 1, "setups": [ { "format": 2019, "trackId": 10, "values": {} } ] }"""));
    }
}
