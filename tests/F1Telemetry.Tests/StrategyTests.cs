using F1Telemetry.Core.Analytics;
using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Models;
using F1Telemetry.Core.Recording;
using F1Telemetry.Core.Tracks;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Packets;
using F1Telemetry.Simulation;
using F1Telemetry.Storage;

namespace F1Telemetry.Tests;

/// <summary>Fuel-corrected pace, pit-stop loss and race-vs-race comparison.</summary>
public sealed class StrategyTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "f1tel-strategy-" + Guid.NewGuid().ToString("N"));

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
    /// A synthetic race: per compound a base pace and wear rate, plus <paramref name="fuelEffect"/> s/kg on 1.6 kg/lap
    /// of fuel from 50 kg. Each pit lap pair (in-lap, out-lap) is <paramref name="pitLoss"/> slower in total.
    /// </summary>
    private static (List<LapRecord> Laps, List<LapAggregate> Aggregates) Race(
        (string Compound, int Laps, double Base, double Degradation)[] stints, double fuelEffect, double pitLoss = 20)
    {
        var laps = new List<LapRecord>();
        var aggregates = new List<LapAggregate>();
        var lapNumber = 0;
        for (var s = 0; s < stints.Length; s++)
        {
            var (compound, count, basePace, degradation) = stints[s];
            for (var age = 0; age < count; age++)
            {
                lapNumber++;
                var fuelStart = 50 - (lapNumber - 1) * 1.6;
                var fuelMean = fuelStart - 0.8;
                var seconds = basePace + degradation * age + fuelEffect * fuelMean;
                var isInLap = age == count - 1 && s < stints.Length - 1;
                var isOutLap = age == 0 && s > 0;
                if (isInLap || isOutLap)
                {
                    seconds += pitLoss / 2;
                }

                laps.Add(new LapRecord
                {
                    LapNumber = lapNumber, LapTimeMs = (uint)Math.Round(seconds * 1000), Compound = compound, StintIndex = s,
                    PitLaneTimeMs = isInLap ? 22_500u : 0, PitStopTimeMs = isInLap ? 2_400u : 0,
                });
                aggregates.Add(new LapAggregate(lapNumber, fuelStart - 1.6, fuelStart, 0, 1, 0, 1, 0, 1, 0, 1));
            }
        }

        return (LapClassifier.Classify(laps, sessionType: 15).ToList(), aggregates);
    }

    [Fact]
    public void Fuel_effect_is_measured_when_a_compound_is_run_twice()
    {
        var (laps, aggregates) = Race([("Medium", 8, 80.0, 0.10), ("Hard", 8, 80.6, 0.05), ("Medium", 8, 80.0, 0.10)], fuelEffect: 0.035);
        var analysis = StintAnalyzer.Analyze(laps, aggregates, default, 15)!;

        Assert.Equal(FuelEffectSource.Measured, analysis.FuelEffect.Source);
        Assert.Equal(0.035, analysis.FuelEffect.SecondsPerKg, 3);

        // Raw lap times hide wear behind the fuel burn: 0.10 s/lap of wear minus 1.6 kg × 0.035 s/kg = 0.044 s/lap.
        var first = analysis.Stints[0];
        Assert.Equal(0.100, first.FuelCorrectedDegradationPerLap, 3);
        Assert.Equal(0.044, first.PaceDegradationPerLap, 3);
        Assert.Equal(0.050, analysis.Stints[1].FuelCorrectedDegradationPerLap, 3);
    }

    /// <summary>
    /// A practice laid out like a real F1 25 one: runs back to the garage, each lap given here as (compound, game stint
    /// index, fuel at the start of the lap, wear at its start, lap time).
    /// </summary>
    private static (List<LapRecord> Laps, List<LapAggregate> Aggregates) Practice((string Compound, int Stint, double Fuel, double Wear, double Seconds)[] laps)
    {
        var records = laps.Select((l, i) => new LapRecord
        {
            LapNumber = i + 1, LapTimeMs = (uint)Math.Round(l.Seconds * 1000), Compound = l.Compound, StintIndex = l.Stint, IsValid = true,
        }).ToList();
        var aggregates = laps.Select((l, i) =>
            new LapAggregate(i + 1, l.Fuel - 1.35, l.Fuel, l.Wear, l.Wear + 0.8, l.Wear, l.Wear + 0.8, l.Wear, l.Wear + 0.8, l.Wear, l.Wear + 0.8)).ToList();
        return (LapClassifier.Classify(records, sessionType: 1).ToList(), aggregates);
    }

    [Fact]
    public void Practice_runs_follow_the_fuel_and_tyres_not_the_games_stint_ends()
    {
        // Madrid, F1 25 2026 pack: 8 laps on the hard, garage, one lap on a new soft, garage, a low-fuel lap on another new
        // soft, garage (refuel, same set), a long run on it. The game ended the hard's stint a lap early (lap 8 came
        // back as a soft lap) and kept the low-fuel lap and the long run in one stint.
        var hard = Enumerable.Range(0, 8).Select(i => (i < 7 ? "Hard" : "Soft", i < 7 ? 0 : 1, 28.7 - 1.35 * i, 0.4 + 0.8 * i, 95.0));
        (string, int, double, double, double)[] soft = [("Soft", 2, 28.7, 1.3, 92.7), ("Soft", 3, 3.6, 2.0, 91.8)];
        var longRun = Enumerable.Range(0, 6).Select(i => ("Soft", 3, 26.6 - 1.35 * i, 8.1 + 2.4 * i, 95.0 + 0.1 * i));
        var (laps, aggregates) = Practice([.. hard, .. soft, .. longRun]);

        var analysis = StintAnalyzer.Analyze(laps, aggregates, default, 1)!;

        Assert.Equal([("Hard", 1, 8), ("Soft", 9, 9), ("Soft", 10, 10), ("Soft", 11, 16)],
            analysis.Stints.Select(s => (s.Compound, s.StartLap, s.EndLap)));
        Assert.All(analysis.PaceLaps.Where(l => l.LapNumber <= 8), l => Assert.Equal("Hard", l.Compound));
    }

    [Fact]
    public void Single_push_laps_dont_measure_the_fuel_effect_but_show_in_the_guide()
    {
        // One soft long run, plus two one-lap runs on the soft (one on high fuel, one on almost none): no fuel effect yet.
        (string, int, double, double, double)[] pushLaps = [("Soft", 0, 28.7, 1.3, 92.7), ("Soft", 1, 3.6, 2.0, 91.8)];
        var longRun = Enumerable.Range(0, 8).Select(i => ("Soft", 1, 26.6 - 1.35 * i, 8.1 + 2.4 * i, 95.0 + 0.1 * i - 0.04 * 1.35 * i));
        var (laps, aggregates) = Practice([.. pushLaps, .. longRun]);
        var analysis = StintAnalyzer.Analyze(laps, aggregates, default, 1)!;
        Assert.Equal(FuelEffectSource.Default, analysis.FuelEffect.Source);

        var session = new RaceInput(new RecordingInfo(1, "", "", 11, "Monza", 1, GameFormat.F1_26, DateTimeOffset.UnixEpoch, null), laps, aggregates, analysis);
        var fuel = DataGuideBuilder.Build(CompoundLibraryBuilder.Build([session], 1), [session]).Items.Single(i => i.Title == "Fuel effect");
        Assert.Equal(GuideState.Partial, fuel.State);
        Assert.Equal("1 / 2 RUNS", fuel.Count);
        Assert.Contains("one run of 3+ laps on the soft", fuel.Have);
        Assert.Contains("2 shorter runs on the soft left out", fuel.Have);
        Assert.Contains("starting on a fuel load well away from 26 kg", fuel.Do);
    }

    [Fact]
    public void A_run_that_gets_faster_measures_no_degradation()
    {
        // The first run of a session: track rubbering in, the driver finding pace. Half a second quicker every lap.
        var run = Enumerable.Range(0, 8).Select(i => ("Hard", 0, 28.7 - 1.35 * i, 0.4 + 0.8 * i, 96.5 - 0.5 * i));
        var (laps, aggregates) = Practice([.. run]);
        var analysis = StintAnalyzer.Analyze(laps, aggregates, default, 1)!;
        var session = new RaceInput(new RecordingInfo(1, "", "", 11, "Monza", 1, GameFormat.F1_26, DateTimeOffset.UnixEpoch, null), laps, aggregates, analysis);

        var library = CompoundLibraryBuilder.Build([session], 1);
        Assert.False(library["Hard"]!.DegradationMeasured);

        var item = DataGuideBuilder.Build(library, [session]).Items.Single(i => i.Title == "Hard long run");
        Assert.Equal(GuideState.Partial, item.State);
        Assert.Contains("got faster through the run", item.Have);
    }

    [Fact]
    public void Fuel_effect_falls_back_to_the_default_when_it_cant_be_separated()
    {
        var (laps, aggregates) = Race([("Medium", 10, 80.0, 0.10), ("Hard", 10, 80.6, 0.05)], fuelEffect: 0.03);
        var analysis = StintAnalyzer.Analyze(laps, aggregates, default, 15, new StrategyOptions { FuelEffectSecondsPerKg = 0.03 })!;

        Assert.Equal(FuelEffectSource.Default, analysis.FuelEffect.Source);
        Assert.Equal(0.100, analysis.Stints[0].FuelCorrectedDegradationPerLap, 3);
    }

    [Fact]
    public void Race_lap_one_and_outliers_are_left_out_of_the_pace_fit()
    {
        var (laps, aggregates) = Race([("Medium", 10, 80.0, 0.10)], fuelEffect: 0.03);
        laps[0] = laps[0] with { LapTimeMs = laps[0].LapTimeMs + 4_000 }; // standing start
        laps[5] = laps[5] with { LapTimeMs = laps[5].LapTimeMs + 9_000 }; // spin
        var stint = StintAnalyzer.Analyze(laps, aggregates, default, 15)!.Stints[0];

        Assert.Equal(8, stint.PaceLapCount);
        Assert.Equal(0.100, stint.FuelCorrectedDegradationPerLap, 3);
    }

    [Fact]
    public void Traffic_cut_corners_and_a_flashback_lap_dont_bend_the_pace_fit()
    {
        var (laps, aggregates) = Race([("Medium", 16, 80.0, 0.10)], fuelEffect: 0.03);
        laps[3] = laps[3] with { LapTimeMs = laps[3].LapTimeMs + 2_000 };   // stuck behind a slower car: under the old 3 s limit
        laps[7] = laps[7] with { LapTimeMs = laps[7].LapTimeMs - 2_500 };   // corner cut / flashback-stitched lap
        laps[14] = laps[14] with { LapTimeMs = laps[14].LapTimeMs + 1_800 }; // a lock-up late in the stint
        var stint = StintAnalyzer.Analyze(laps, aggregates, default, 15)!.Stints[0];

        Assert.Equal(16 - 1 - 3, stint.PaceLapCount); // lap 1 and the three bad laps
        Assert.Equal(0.100, stint.FuelCorrectedDegradationPerLap, 3);
    }

    [Fact]
    public void Wear_and_fuel_rates_ignore_a_lap_with_a_bad_reading()
    {
        var (laps, aggregates) = Race([("Medium", 8, 80.0, 0.10)], fuelEffect: 0.03);
        aggregates[4] = aggregates[4] with { MinFuel = aggregates[4].MaxFuel - 6, MaxWearFl = 9 }; // a flashback mid-lap
        var stint = StintAnalyzer.Analyze(laps, aggregates, default, 15)!.Stints[0];

        Assert.Equal(1.6, stint.AverageFuelPerLap, 6);
        Assert.Equal(1.0, stint.AverageWearPerLap.Fl, 6);
    }

    [Fact]
    public void A_fuel_effect_the_laps_cant_pin_down_falls_back_to_the_default()
    {
        // The same compound twice, but with lap-to-lap scatter of a few tenths: the fit's error is too large to trust.
        var (laps, aggregates) = Race([("Medium", 6, 80.0, 0.10), ("Hard", 6, 80.6, 0.05), ("Medium", 6, 80.0, 0.10)], fuelEffect: 0.035);
        var random = new Random(3);
        laps = laps.Select(l => l with { LapTimeMs = (uint)(l.LapTimeMs + random.Next(-600, 600)) }).ToList();
        var analysis = StintAnalyzer.Analyze(laps, aggregates, default, 15, new StrategyOptions { FuelEffectSecondsPerKg = 0.03 })!;

        Assert.Equal(FuelEffectSource.Default, analysis.FuelEffect.Source);
        Assert.Equal(0.03, analysis.FuelEffect.SecondsPerKg);
    }

    [Fact]
    public void Pit_stop_loss_is_the_in_and_out_lap_against_the_laps_around_them()
    {
        var (laps, _) = Race([("Medium", 10, 80.0, 0), ("Hard", 10, 80.0, 0)], fuelEffect: 0, pitLoss: 21);
        var stop = Assert.Single(PitStopAnalyzer.Analyze(laps, 15));

        Assert.Equal((10, 11, "Medium", "Hard"), (stop.InLap, stop.OutLap, stop.FromCompound, stop.ToCompound));
        Assert.Equal(21.0, stop.LossSeconds, 3);
        Assert.Equal(80.0, stop.ReferenceLapSeconds, 3);
        Assert.Equal(22.5, stop.PitLaneSeconds);
        Assert.Equal(2.4, stop.StationarySeconds);
    }

    [Fact]
    public void Race_comparison_gap_and_breakdown_add_up()
    {
        // Same car and fuel: A one-stops on worn tyres, B two-stops on fresher ones.
        var a = Race([("Medium", 12, 80.0, 0.15), ("Hard", 12, 80.5, 0.08)], fuelEffect: 0.03, pitLoss: 20);
        var b = Race([("Medium", 8, 80.0, 0.15), ("Hard", 8, 80.5, 0.08), ("Medium", 8, 80.0, 0.15)], fuelEffect: 0.03, pitLoss: 20);
        RaceInput Input((List<LapRecord> Laps, List<LapAggregate> Aggregates) race, long id) => new(
            new RecordingInfo(id, "", "", 11, "Monza", 15, GameFormat.F1_25, DateTimeOffset.UnixEpoch, null),
            race.Laps, race.Aggregates, StintAnalyzer.Analyze(race.Laps, race.Aggregates, default, 15)!);

        var result = RaceComparison.Compare(Input(a, 1), Input(b, 2), 0.03)!;

        Assert.Equal(24, result.Laps.Count);
        var totalA = a.Laps.Sum(l => l.LapTimeMs) / 1000.0;
        var totalB = b.Laps.Sum(l => l.LapTimeMs) / 1000.0;
        Assert.Equal(totalB - totalA, result.TotalGap, 6);
        foreach (var part in new[] { result.A, result.B })
        {
            Assert.Equal(part.Total, part.PitStops + part.TyreWear + part.FuelLoad + part.BasePace + part.Other, 6);
        }

        Assert.Equal(20.0, result.B.PitStops - result.A.PitStops, 1);   // one extra stop
        Assert.True(result.B.TyreWear < result.A.TyreWear);             // shorter stints
        Assert.Equal(0, result.B.FuelLoad - result.A.FuelLoad, 6);      // same fuel
        Assert.Equal(3, result.StintsB.Count);
    }

    [Fact]
    public async Task Simulated_one_and_two_stop_races_are_compared()
    {
        var track = new TrackLibrary(Path.Combine(AppContext.BaseDirectory, "track_maps")).Get("Monza");
        await using var store = new DuckDbTelemetryStore(Path.Combine(_dir, "races.duckdb"));
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        async Task<RecordingInfo> Record(int[] pitLaps, ulong uid)
        {
            var engine = new SessionEngine();
            using var coordinator = new RecordingCoordinator(engine, store);
            var options = new SimulationOptions { Track = track, Laps = 9, PitLaps = pitLaps, TickRateHz = 20, SessionUid = uid, Seed = 3 };
            foreach (var datagram in new SessionSimulator(options).Generate(TestContext.Current.CancellationToken))
            {
                engine.Process(PacketParser.Parse(datagram.Data).Packet!);
                if (!coordinator.IsRecording && engine.Session is { TrackId: >= 0 })
                {
                    coordinator.Start($"{pitLaps.Length}-stop");
                }
            }

            coordinator.Stop();
            await store.FlushAsync(TestContext.Current.CancellationToken);
            return (await store.GetRecordingsAsync(TestContext.Current.CancellationToken)).First(r => r.Description == $"{pitLaps.Length}-stop");
        }

        var oneStop = await Record([4], 101);
        var twoStop = await Record([3, 6], 102);
        var analysis = new SessionAnalysisService(store);

        var race = (await analysis.LoadRaceAsync(twoStop, TestContext.Current.CancellationToken))!;
        Assert.Equal(["Medium", "Hard", "Medium"], race.Analysis.Stints.Select(s => s.Compound));
        Assert.Equal(2, race.Analysis.PitStops.Count);
        Assert.All(race.Analysis.PitStops, stop =>
        {
            Assert.InRange(stop.PitLaneSeconds!.Value, 10, 40);
            Assert.InRange(stop.StationarySeconds!.Value, 2.0, 3.0);
            Assert.InRange(stop.LossSeconds, 5, 40);
        });

        var comparison = (await analysis.CompareAsync(oneStop, twoStop, TestContext.Current.CancellationToken))!;
        Assert.Equal(Enumerable.Range(1, 9), comparison.Laps);
        Assert.Equal(comparison.B.Total - comparison.A.Total, comparison.TotalGap, 6);
        Assert.True(comparison.B.PitStops > comparison.A.PitStops);
    }
}
