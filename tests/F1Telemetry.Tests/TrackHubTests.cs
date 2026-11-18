using F1Telemetry.Core.Analytics;
using F1Telemetry.Core.Energy;
using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Hub;
using F1Telemetry.Core.Models;
using F1Telemetry.Core.Recording;
using F1Telemetry.Core.Tracks;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Lookups;
using F1Telemetry.Protocol.Packets;
using F1Telemetry.Simulation;
using F1Telemetry.Storage;

namespace F1Telemetry.Tests;

/// <summary>The track hub: sessions saved with what the plans learned from them, kept after their recordings are deleted.</summary>
public sealed class TrackHubTests : IAsyncLifetime
{
    private const GameFormat Format = GameFormat.F1_26;
    private const int Monza = 11;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "f1tel-tests-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    /// <summary>Records a simulated session at Monza and returns its recording.</summary>
    private static async Task<RecordingInfo> RecordAsync(DuckDbTelemetryStore store, ulong uid, int laps = 6, byte length = RaceLengths.Medium)
    {
        var track = new TrackLibrary(Path.Combine(AppContext.BaseDirectory, "track_maps")).Get("Monza");
        var engine = new SessionEngine();
        using var coordinator = new RecordingCoordinator(engine, store);
        var options = new SimulationOptions { Format = Format, Track = track, Laps = laps, PitOnLap = 3, TickRateHz = 20, SessionUid = uid, SessionLength = length, Seed = (int)uid };
        foreach (var datagram in new SessionSimulator(options).Generate(Ct))
        {
            engine.Process(PacketParser.Parse(datagram.Data).Packet!);
            if (!coordinator.IsRecording && engine.Session is { TrackId: >= 0 })
            {
                coordinator.Start("Hub");
            }
        }

        coordinator.Stop();
        await store.FlushAsync(Ct);
        return (await store.GetRecordingsAsync(Ct)).Single(r => r.SessionUid == uid.ToString());
    }

    [Fact]
    public async Task A_saved_session_keeps_the_plans_after_its_recording_is_deleted()
    {
        await using var store = await OpenAsync("hub.duckdb");
        var recording = await RecordAsync(store, 1);
        Assert.Equal(RaceLengths.Medium, recording.SessionLength);

        var analysis = new SessionAnalysisService(store);
        var hub = new TrackHubService(store, analysis);
        var before = (await analysis.BuildTrackKnowledgeAsync(Format, Monza, null, ct: Ct)).Library;
        var modelBefore = await hub.BuildErsModelAsync(Format, Monza);
        Assert.True(modelBefore.HasPowerData);

        var unsaved = Assert.Single(await hub.SessionsAsync(Format, Monza));
        Assert.False(unsaved.IsSaved);
        Assert.Equal(RaceLengths.Medium, unsaved.RaceLength);

        Assert.Equal(1, await hub.SaveAsync([unsaved]));
        var saved = Assert.Single(await store.GetHubSessionsAsync(Format, Monza, Ct));
        Assert.Equal((recording.Id, recording.SessionUid, RaceLengths.Medium, 6), (saved.RecordingId, saved.SessionUid, saved.RaceLength, saved.Laps));
        Assert.Equal(6, saved.Strategy.Laps.Count);
        Assert.NotNull(saved.Energy.Model);
        Assert.NotNull(saved.Energy.Typical);
        Assert.NotNull(saved.Energy.Fastest);
        Assert.NotEmpty(saved.SetupRuns);
        Assert.True(Assert.Single(await hub.SessionsAsync(Format, Monza)) is { IsSaved: true, HasRecording: true });

        // Saving it again replaces it.
        Assert.Equal(1, await hub.SaveAsync([unsaved]));
        Assert.Equal(saved.Id, Assert.Single(await store.GetHubSessionsAsync(cancellationToken: Ct)).Id);

        // The recording goes; the hub keeps what the plans use.
        await store.DeleteRecordingAsync(recording.Id, Ct);
        analysis.Forget(recording.Id);
        hub.Forget(recording.Id);
        Assert.Empty(await store.GetRecordingsAsync(Ct));
        Assert.True(Assert.Single(await hub.SessionsAsync(Format, Monza)) is { IsSaved: true, HasRecording: false });

        var after = (await analysis.BuildTrackKnowledgeAsync(Format, Monza, null, ct: Ct)).Library;
        Assert.Equal(before.Sessions, after.Sessions);
        Assert.Equal(before.Compounds.Select(c => c.Compound), after.Compounds.Select(c => c.Compound));
        foreach (var (a, b) in before.Compounds.Zip(after.Compounds))
        {
            Assert.Equal(a.BaseLapSeconds, b.BaseLapSeconds, 9);
            Assert.Equal(a.DegradationPerLap, b.DegradationPerLap, 9);
            Assert.Equal(a.WearPerLap, b.WearPerLap, 9);
        }

        Assert.Equal(before.PitLossSeconds, after.PitLossSeconds, 9);
        Assert.Equal(before.FuelPerLap, after.FuelPerLap, 9);
        Assert.Equal([RaceLengths.Medium], after.Lengths);

        var modelAfter = await hub.BuildErsModelAsync(Format, Monza);
        Assert.Equal((modelBefore.Laps, modelBefore.PowerLaps), (modelAfter.Laps, modelAfter.PowerLaps));
        Assert.Equal(modelBefore.Deploy.Curves.Select(c => c.Mode), modelAfter.Deploy.Curves.Select(c => c.Mode));
        Assert.Equal(modelBefore.StraightLine.Count, modelAfter.StraightLine.Count);

        var (laps, why) = await hub.PlanLapsAsync(Format, Monza, modelAfter);
        if (modelAfter.StraightLine.Count > 0 && modelAfter.Deploy.Curves.Count > 0)
        {
            Assert.True(laps is { RaceLaps: > 0 }, why);
        }

        // Its setup runs stay in the library, and its recording id is never handed out again.
        Assert.NotEmpty(await store.GetSetupRunsAsync(Ct));
        Assert.All(await store.GetSetupRunsAsync(Ct), r => Assert.Equal(recording.Id, r.RecordingId));
        Assert.True(store.BeginRecording(new NewRecording("9", "Next", Monza, "Monza", 15, Format)) > recording.Id);

        await hub.SetRaceLengthAsync(saved, RaceLengths.Full);
        Assert.Equal(RaceLengths.Full, Assert.Single(await store.GetHubSessionsAsync(cancellationToken: Ct)).RaceLength);

        await hub.RemoveAsync([saved]);
        Assert.Empty(await store.GetHubSessionsAsync(cancellationToken: Ct));
        Assert.Empty(await store.GetSetupRunsAsync(Ct));
    }

    [Fact]
    public async Task Car_model_sums_of_sessions_merge_into_the_model_of_all_their_samples()
    {
        await using var store = await OpenAsync("stats.duckdb");
        var first = await RecordAsync(store, 1, laps: 4);
        var second = await RecordAsync(store, 2, laps: 4);
        var samples = await store.GetModelSamplesAsync(Format, Monza, Ct);

        var direct = ErsModelBuilder.Build(Format, Monza, samples);
        var merged = ErsModelStats.Merge(
        [
            ErsModelStats.From(Format, [.. samples.Where(s => s.RecordingId == first.Id)]),
            ErsModelStats.From(Format, [.. samples.Where(s => s.RecordingId == second.Id)]),
        ]).Build(Format, Monza);

        Assert.Equal((direct.Laps, direct.PowerLaps), (merged.Laps, merged.PowerLaps));
        Assert.Equal(direct.Deploy.Curves.Select(c => c.Mode), merged.Deploy.Curves.Select(c => c.Mode));
        foreach (var (a, b) in direct.Deploy.Curves.Zip(merged.Deploy.Curves))
        {
            Assert.Equal(a.Points.Select(p => (p.SpeedKmh, p.Samples)), b.Points.Select(p => (p.SpeedKmh, p.Samples)));
            Assert.All(a.Points.Zip(b.Points), p => Assert.Equal(p.First.PowerW, p.Second.PowerW, ErsModelStats.PowerResolution / 2.0 + 1e-6));
        }

        Assert.Equal(direct.StraightLine.Select(f => (f.Aero, f.Samples)), merged.StraightLine.Select(f => (f.Aero, f.Samples)));
        foreach (var (a, b) in direct.StraightLine.Zip(merged.StraightLine))
        {
            Assert.Equal(a.Efficiency, b.Efficiency, 6);
            Assert.Equal(a.Drag, b.Drag, 6);
            Assert.Equal(a.Resistance, b.Resistance, 6);
            Assert.Equal(a.RSquared, b.RSquared, 6);
        }

        Assert.Equal(direct.Grip.Bands.Select(b => (b.FromKmh, b.FromG, b.Samples)), merged.Grip.Bands.Select(b => (b.FromKmh, b.FromG, b.Samples)));
        Assert.All(direct.Grip.Bands.Zip(merged.Grip.Bands), p => Assert.Equal(p.First.Factor, p.Second.Factor, 6));

        Assert.Equal(direct.Map.Segments, merged.Map.Segments);
        for (var segment = 0; segment < direct.Map.Segments; segment++)
        {
            Assert.Equal(direct.Map.Lift(segment) ?? -1, merged.Map.Lift(segment) ?? -1, 6);
            for (var mode = 0; mode <= DeployModes.Overtake; mode++)
            {
                Assert.Equal(direct.Map.Clip(mode, segment) ?? -1, merged.Map.Clip(mode, segment) ?? -1, 6);
                Assert.Equal(direct.Map.Deploy(mode, segment, 250, null)?.Output ?? -1, merged.Map.Deploy(mode, segment, 250, null)?.Output ?? -1, 6);
            }
        }
    }

    [Fact]
    public void Practice_counts_for_the_race_length_of_its_weekend()
    {
        var start = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        RecordingInfo Recording(long id, int type, double hours, int length = 0, int track = Monza) =>
            new(id, $"{id}", "", track, "Monza", type, Format, start.AddHours(hours), start.AddHours(hours + 1)) { SessionLength = length };

        var race = Recording(1, 15, 4, RaceLengths.Medium);
        var practice = Recording(2, 1, 0, RaceLengths.Short); // a practice's own session length is not the race's
        var lastWeek = Recording(3, 1, -170);
        var elsewhere = Recording(4, 15, 2, RaceLengths.Full, track: 10);
        IReadOnlyList<RecordingInfo> all = [race, practice, lastWeek, elsewhere];

        Assert.Equal(RaceLengths.Medium, TrackHub.RaceLengthOf(race, all));
        Assert.Equal(RaceLengths.Medium, TrackHub.RaceLengthOf(practice, all));
        Assert.Equal(RaceLengths.Unknown, TrackHub.RaceLengthOf(lastWeek, all));
        Assert.Equal(RaceLengths.Unknown, TrackHub.RaceLengthOf(Recording(5, 15, 0), all)); // a race with no length recorded
    }

    [Fact]
    public void Wear_and_fuel_come_from_the_race_length_planned_for_and_pace_is_shared()
    {
        // The same medium tyre wears four times as fast in the medium-length race as in the full-length one.
        var medium = Session(1, 15, RaceLengths.Medium, [("Medium", 10, 80.0, 0.12, 4.0), ("Hard", 10, 80.5, 0.08, 2.4)], fuelPerLap: 3.0);
        var full = Session(2, 15, RaceLengths.Full, [("Medium", 10, 80.0, 0.03, 1.0), ("Soft", 10, 79.6, 0.05, 1.6)], fuelPerLap: 1.5);

        var atMedium = CompoundLibraryBuilder.Build([medium, full], 1, raceLength: RaceLengths.Medium);
        var atFull = CompoundLibraryBuilder.Build([medium, full], 1, raceLength: RaceLengths.Full);
        var pooled = CompoundLibraryBuilder.Build([medium, full], 1);

        Assert.Equal(4.0, atMedium["Medium"]!.WearPerLap, 1);
        Assert.Equal(1.0, atFull["Medium"]!.WearPerLap, 1);
        Assert.Equal(2.5, pooled["Medium"]!.WearPerLap, 1);
        Assert.Equal(0.12, atMedium["Medium"]!.DegradationPerLap, 2);
        Assert.Equal(0.03, atFull["Medium"]!.DegradationPerLap, 2);
        Assert.Equal(3.0, atMedium.FuelPerLap, 1);
        Assert.Equal(1.5, atFull.FuelPerLap, 1);
        Assert.Equal(atMedium["Medium"]!.BaseLapSeconds, atFull["Medium"]!.BaseLapSeconds, 2);

        // The soft was only run at full length: a medium-length plan takes its figures from there, and says so.
        Assert.True(atMedium["Soft"]!.FromOtherLengths);
        Assert.False(atMedium["Medium"]!.FromOtherLengths);
        Assert.False(pooled["Soft"]!.FromOtherLengths);
        Assert.Equal([RaceLengths.Medium, RaceLengths.Full], pooled.Lengths);
        Assert.Equal((RaceLengths.Medium, 20), (atMedium.RaceLength, atMedium.RaceLaps));
        Assert.Equal(0, pooled.RaceLength);
    }

    [Fact]
    public void Lap_profiles_average_by_the_laps_they_stand_for()
    {
        var one = Profile(60);
        var two = Profile(70);
        var three = Profile(80);
        var pair = LapProfile.Average([one, two])!;
        Assert.Equal(2, pair.LapCount);

        var all = LapProfile.Average([one, two, three])!;
        var nested = LapProfile.Average([LapProfile.FromData(pair.ToData())!, three])!;
        Assert.Equal(3, nested.LapCount);
        Assert.Equal(all.Speed, nested.Speed);
        Assert.Equal(all.Segments.Select(s => s.Harvest), nested.Segments.Select(s => s.Harvest));
        Assert.Equal(all.ActualSeconds, nested.ActualSeconds, 9);
    }

    [Theory]
    [InlineData(GameFormat.F1_25)]
    [InlineData(GameFormat.F1_26)]
    public void Session_packet_carries_the_session_length(GameFormat format)
    {
        var writer = new PacketWriter(FormatLayout.For(format));
        var packet = writer.Create(PacketId.Session, 1, 0, 0, 0);
        writer.WriteSession(packet, new SessionData(0, 30, 20, 25, 5793, 15, 11, 0, 3600, 3600, 80, false, 0, false, [], 0, 0, 0, 0, 0, 2, 1, 0, 1900, 3800,
            null, RaceLengths.MediumLong));

        var parsed = Assert.IsType<SessionPacket>(PacketParser.Parse(packet).Packet).Data;
        Assert.Equal(RaceLengths.MediumLong, parsed.SessionLength);
        Assert.Equal((2, 1), (parsed.NumSafetyCarPeriods, parsed.NumVirtualSafetyCarPeriods)); // the bytes after it still line up
    }

    private static LapProfile Profile(double speed)
    {
        const int count = 30;
        var segments = Enumerable.Range(0, count).Select(i => new PlanSegment(i, i * LapProfile.Step, i % 3 != 0, 0, 400_000, 100_000, speed * 100, 0, 1,
            2 * LapProfile.Step / (2 * speed))).ToList();
        return LapProfile.FromData(new LapProfileData(1, 1, segments, [.. Enumerable.Repeat(speed, count + 1)], [.. Enumerable.Repeat(2e6, count + 1)], 35, 800,
            count * LapProfile.Step / speed, 0))!;
    }

    /// <summary>
    /// A synthetic session run at a race length: per stint a compound, laps, base pace, degradation and wear (% per lap
    /// on the worst tyre), with <paramref name="fuelPerLap"/> kg of fuel a lap.
    /// </summary>
    private static RaceInput Session(long id, int sessionType, int length, (string Compound, int Laps, double Base, double Degradation, double Wear)[] stints,
        double fuelPerLap)
    {
        var laps = new List<LapRecord>();
        var aggregates = new List<LapAggregate>();
        var lapNumber = 0;
        for (var s = 0; s < stints.Length; s++)
        {
            var (compound, count, basePace, degradation, wear) = stints[s];
            for (var age = 0; age < count; age++)
            {
                lapNumber++;
                var fuelStart = 60 - (lapNumber - 1) * fuelPerLap;
                var seconds = basePace + degradation * age + 0.03 * (fuelStart - fuelPerLap / 2);
                if ((age == count - 1 && s < stints.Length - 1) || (age == 0 && s > 0))
                {
                    seconds += 10;
                }

                laps.Add(new LapRecord { LapNumber = lapNumber, LapTimeMs = (uint)Math.Round(seconds * 1000), Compound = compound, StintIndex = s });
                var w = age * wear;
                aggregates.Add(new LapAggregate(lapNumber, fuelStart - fuelPerLap, fuelStart, w, w + wear * 0.8, w, w + wear * 0.8, w, w + wear, w, w + wear * 0.9));
            }
        }

        var classified = LapClassifier.Classify(laps, sessionType).ToList();
        var analysis = StintAnalyzer.Analyze(classified, aggregates, default, sessionType, new StrategyOptions { FuelEffectSecondsPerKg = 0.03 })!;
        return new RaceInput(new RecordingInfo(id, "", "", Monza, "Monza", sessionType, Format, DateTimeOffset.UnixEpoch, null), classified, aggregates, analysis)
        {
            RaceLength = length,
        };
    }
}
