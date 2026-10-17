using F1Telemetry.Core.Analytics;
using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.Tests;

/// <summary>Compound library across recordings, and 1-, 2- and 3-stop race plans.</summary>
public sealed class StrategyPlannerTests
{
    private const double FuelEffect = 0.03;

    /// <summary>
    /// A synthetic session: per stint a compound, laps, base pace, degradation and wear (% per lap on the worst tyre),
    /// 1.6 kg/lap of fuel from 50 kg, plus <paramref name="offset"/> on every lap (a slower track that day).
    /// </summary>
    /// <param name="cliff">Seconds each lap of wear past 60 % adds on top of the degradation (a tyre cliff).</param>
    private static RaceInput Session(long id, int sessionType, (string Compound, int Laps, double Base, double Degradation, double Wear)[] stints,
        double offset = 0, double pitLoss = 20, double cliff = 0)
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
                var fuelStart = 50 - (lapNumber - 1) * 1.6;
                var seconds = basePace + offset + degradation * age + FuelEffect * (fuelStart - 0.8)
                    + cliff * Math.Max(0, (age + 0.5) * wear - 60) / wear;
                if ((age == count - 1 && s < stints.Length - 1) || (age == 0 && s > 0))
                {
                    seconds += pitLoss / 2;
                }

                laps.Add(new LapRecord { LapNumber = lapNumber, LapTimeMs = (uint)Math.Round(seconds * 1000), Compound = compound, StintIndex = s });
                var w = age * wear;
                aggregates.Add(new LapAggregate(lapNumber, fuelStart - 1.6, fuelStart, w, w + wear * 0.8, w, w + wear * 0.8, w, w + wear, w, w + wear * 0.9));
            }
        }

        var classified = LapClassifier.Classify(laps, sessionType).ToList();
        var analysis = StintAnalyzer.Analyze(classified, aggregates, default, sessionType, new StrategyOptions { FuelEffectSecondsPerKg = FuelEffect })!;
        return new RaceInput(new RecordingInfo(id, "", "", 11, "Monza", sessionType, GameFormat.F1_25, DateTimeOffset.UnixEpoch, null), classified, aggregates, analysis);
    }

    private static CompoundModel Model(string compound, double basePace, double degradation, double? life = null) =>
        new(compound, 10, 1, 1, basePace, degradation, true, life is { } l ? 75 / l : 0, life, true);

    private static CompoundLibrary Library(double pitLoss, params CompoundModel[] compounds) =>
        new(compounds, new FuelEffect(FuelEffect, FuelEffectSource.Default, ""), pitLoss, 1, 1.6, 1, null);

    [Fact]
    public void Library_separates_compound_pace_from_the_session_it_was_run_in()
    {
        // The race (the anchor) runs medium and hard; practice runs soft and medium on a track 0.6 s slower that day.
        var race = Session(1, 15, [("Medium", 12, 80.0, 0.10, 2.0), ("Hard", 12, 80.6, 0.05, 1.2)], pitLoss: 21);
        var practice = Session(2, 1, [("Soft", 8, 79.4, 0.20, 3.5), ("Medium", 8, 80.0, 0.10, 2.0)], offset: 0.6);

        var library = CompoundLibraryBuilder.Build([race, practice], anchorRecordingId: 1, new StrategyOptions { FuelEffectSecondsPerKg = FuelEffect });

        Assert.Equal(["Soft", "Medium", "Hard"], library.Compounds.Select(c => c.Compound));
        Assert.All(library.Compounds, c => Assert.True(c.Linked));
        Assert.Equal(79.4, library["Soft"]!.BaseLapSeconds, 2);   // in the race's conditions, not practice's
        Assert.Equal(80.0, library["Medium"]!.BaseLapSeconds, 2);
        Assert.Equal(80.6, library["Hard"]!.BaseLapSeconds, 2);
        Assert.Equal(0.20, library["Soft"]!.DegradationPerLap, 3);
        Assert.Equal(0.05, library["Hard"]!.DegradationPerLap, 3);
        Assert.Equal(2, library["Medium"]!.Sessions);

        Assert.Equal(1, library.PitLossStops);
        Assert.Equal(21.0, library.PitLossSeconds, 1);
        Assert.Equal(75 / 3.5, library["Soft"]!.LifeLaps!.Value, 1);
        Assert.Equal(24, library.RaceLaps);
    }

    [Fact]
    public void Library_without_a_race_uses_the_default_pit_loss_and_flags_unlinked_compounds()
    {
        var a = Session(1, 1, [("Medium", 8, 80.0, 0.10, 2.0)]);
        var b = Session(2, 1, [("Soft", 8, 79.4, 0.20, 3.5)]);

        var library = CompoundLibraryBuilder.Build([a, b], anchorRecordingId: 1, new StrategyOptions { DefaultPitLossSeconds = 23 });

        Assert.Equal(0, library.PitLossStops);
        Assert.Equal(23, library.PitLossSeconds);
        Assert.True(library["Medium"]!.Linked);
        Assert.False(library["Soft"]!.Linked);
        Assert.Null(library.RaceLaps);
    }

    [Fact]
    public void One_stop_splits_the_race_where_the_two_stints_cost_the_same_at_the_margin()
    {
        // Equal compounds apart from wear: the faster-wearing one gets the shorter stint.
        var library = Library(20, Model("Medium", 80.0, 0.10), Model("Hard", 80.0, 0.05));
        var plans = StrategyPlanner.Plan(library, 30, new PlannerOptions { MaxStops = 1 });

        var plan = Assert.Single(plans.Plans);
        Assert.Equal(["Medium", "Hard"], plan.Stints.Select(s => s.Compound));
        Assert.Equal(10, plan.Stints[0].Laps); // 0.10 × L1 ≈ 0.05 × L2 with L1 + L2 = 30
        Assert.Equal(30, plan.Stints.Sum(s => s.Laps));
        Assert.Equal(plan.LapSeconds.Sum() + 20, plan.RaceSeconds, 6);
        Assert.Equal(30, plan.LapSeconds.Count);

        var stop = Assert.Single(plan.Stops);
        Assert.Equal(10, stop.InLap);
        Assert.InRange(stop.InLap, stop.WindowFirst, stop.WindowLast);
        Assert.True(stop.WindowFirst < stop.InLap && stop.WindowLast > stop.InLap);
        Assert.Equal(80.0 + 0.10 * 10 - (80.0 + 0.05), stop.UndercutSeconds, 6);
    }

    [Fact]
    public void Plans_are_ranked_and_need_two_dry_compounds()
    {
        var library = Library(22, Model("Soft", 79.2, 0.25), Model("Medium", 79.8, 0.10), Model("Hard", 80.3, 0.05));
        var plans = StrategyPlanner.Plan(library, 50);

        Assert.Empty(plans.Problem);
        Assert.Equal(0, plans.Plans[0].GapSeconds);
        Assert.True(plans.Plans.Zip(plans.Plans.Skip(1)).All(p => p.First.RaceSeconds <= p.Second.RaceSeconds + 1e-9));
        Assert.All(plans.Plans, p => Assert.True(p.Stints.Select(s => s.Compound).Distinct().Count() >= 2));
        Assert.NotNull(plans.BestWith(1));
        Assert.NotNull(plans.BestWith(2));
        Assert.NotNull(plans.BestWith(3));

        var single = StrategyPlanner.Plan(Library(22, Model("Medium", 79.8, 0.10), Model("Inter", 85, 0.1)), 50);
        Assert.Empty(single.Plans);
        Assert.Contains("two compounds", single.Problem);
    }

    [Fact]
    public void Plans_never_use_more_sets_of_a_compound_than_the_race_has()
    {
        // Hard is so much better that, unlimited, the best plans would run it three or four times.
        var library = Library(22, Model("Medium", 80.6, 0.20), Model("Hard", 80.0, 0.02, life: 15));
        Assert.Contains(StrategyPlanner.Plan(library, 50, new PlannerOptions { MaxSets = new Dictionary<string, int>() }).Plans,
            p => p.Stints.Count(s => s.Compound == "Hard") > 2);

        var plans = StrategyPlanner.Plan(library, 50); // the default allocation: 2 hard, 3 medium
        Assert.NotEmpty(plans.Plans);
        Assert.All(plans.Plans, p =>
        {
            Assert.InRange(p.Stints.Count(s => s.Compound == "Hard"), 0, 2);
            Assert.InRange(p.Stints.Count(s => s.Compound == "Medium"), 0, 3);
        });

        var noHards = StrategyPlanner.Plan(library, 50, new PlannerOptions { MaxSets = new Dictionary<string, int> { ["Hard"] = 0, ["Medium"] = 3 } });
        Assert.Empty(noHards.Plans);
        Assert.Contains("set available", noHards.Problem);
    }

    [Fact]
    public void Tyre_life_rules_out_plans_that_overrun_a_set()
    {
        // Each set lasts 15 laps: 40 laps need at least two stops.
        var library = Library(20, Model("Medium", 80.0, 0.02, life: 15), Model("Hard", 80.4, 0.01, life: 15));
        var plans = StrategyPlanner.Plan(library, 40);

        Assert.Null(plans.BestWith(1));
        Assert.Equal(2, plans.Best!.StopCount);
        Assert.All(plans.Plans.SelectMany(p => p.Stints), s => Assert.InRange(s.Laps, 3, 15));
    }

    [Fact]
    public void Library_measures_the_cliff_from_laps_run_past_it()
    {
        // Soft wears 4 %/lap: from about lap 15 its most worn tyre is past the 60 % cliff and loses 0.3 s more per lap.
        var practice = Session(1, 1, [("Soft", 22, 79.4, 0.10, 4.0)], cliff: 0.3);
        var race = Session(2, 15, [("Medium", 12, 80.0, 0.10, 2.0), ("Hard", 12, 80.6, 0.05, 1.2)]);

        var library = CompoundLibraryBuilder.Build([race, practice], anchorRecordingId: 2, new StrategyOptions { FuelEffectSecondsPerKg = FuelEffect });

        var soft = library["Soft"]!;
        Assert.True(soft.CliffMeasured);
        Assert.Equal(0.3, soft.CliffSecondsPerLap, 2);
        Assert.Equal(60 / 4.0, soft.CliffAge!.Value, 1);
        Assert.Equal(0.10, soft.DegradationPerLap, 2); // the straight line, the cliff laps left out of it
        Assert.Equal(soft.LapSeconds(15) + 0.10 * 3 + 0.3 * 3, soft.LapSeconds(18), 6);

        var medium = library["Medium"]!;
        Assert.False(medium.CliffMeasured); // never run past 60 %: the default
        Assert.Equal(new StrategyOptions().DefaultTyreCliffSecondsPerLap, medium.CliffSecondsPerLap);
        Assert.Equal(30, medium.CliffAge!.Value, 6);
    }

    [Fact]
    public void Plans_keep_stints_short_of_a_steep_cliff()
    {
        var straight = Library(22, Model("Medium", 80.0, 0.05, life: 40), Model("Hard", 80.3, 0.02, life: 40));
        var cliffed = Library(22, Model("Medium", 80.0, 0.05, life: 40) with { CliffAge = 12, CliffSecondsPerLap = 1.0 },
            Model("Hard", 80.3, 0.02, life: 40) with { CliffAge = 16, CliffSecondsPerLap = 1.0 });

        var before = StrategyPlanner.Plan(straight, 44, new PlannerOptions { MaxStops = 1 }).Best!;
        var after = StrategyPlanner.Plan(cliffed, 44, new PlannerOptions { MaxStops = 3 }).Best!;

        Assert.Contains(before.Stints, s => s.Laps > 20);
        Assert.True(after.StopCount > 1);
        Assert.All(after.Stints, s => Assert.True(s.Laps <= (s.Compound == "Hard" ? 16 : 12) + 2, $"{s.Compound} {s.Laps} laps"));
        Assert.Equal(after.LapSeconds.Sum() + after.StopCount * 22, after.RaceSeconds, 6);
    }

    [Fact]
    public void A_used_start_set_runs_as_a_set_that_many_laps_old()
    {
        // 75 / 15 = 5 %/lap: a medium with 30 % wear is a medium 6 laps old, with 6 laps less life.
        var library = Library(20, Model("Medium", 80.0, 0.10, life: 15), Model("Hard", 80.4, 0.05, life: 30));
        var fresh = StrategyPlanner.Plan(library, 30, new PlannerOptions { MaxStops = 1, StartSet = new PlannerSet("Medium") }).Best!;
        var used = StrategyPlanner.Plan(library, 30, new PlannerOptions { MaxStops = 1, StartSet = new PlannerSet("Medium", 30) }).Best!;

        Assert.Equal("Medium", used.Stints[0].Compound);
        Assert.Equal(30, used.Stints[0].StartWear);
        Assert.Equal(0, used.Stints[1].StartWear);
        Assert.True(used.Stints[0].Laps < fresh.Stints[0].Laps);
        Assert.InRange(used.Stints[0].Laps, 3, 15 - 6);
        Assert.True(used.RaceSeconds > fresh.RaceSeconds);
        Assert.Equal(80.0 + 0.10 * 6, used.LapSeconds[0] - library.FuelEffect.SecondsPerKg * library.FuelPerLap * 29.5, 6);

        // A start compound with no model, and a start that rules out every plan, say so.
        Assert.Contains("starts on the soft", StrategyPlanner.Plan(library, 30, new PlannerOptions { StartSet = new PlannerSet("Soft") }).Problem);
        Assert.Contains("starting on the medium",
            StrategyPlanner.Plan(library, 30, new PlannerOptions { MaxStops = 1, StartSet = new PlannerSet("Medium", 80) }).Problem);
    }

    [Fact]
    public void Plans_use_the_freshest_of_the_sets_listed()
    {
        var library = Library(20, Model("Medium", 80.0, 0.10, life: 25), Model("Hard", 80.4, 0.05, life: 40));
        var sets = new[] { new PlannerSet("Hard", 40), new PlannerSet("Medium", 10), new PlannerSet("Hard", 5) };
        var plans = StrategyPlanner.Plan(library, 30, new PlannerOptions { MaxStops = 2, Sets = sets });

        var best = plans.Best!;
        Assert.Equal(["Medium", "Hard"], best.Stints.Select(s => s.Compound));
        Assert.Equal([10.0, 5.0], best.Stints.Select(s => s.StartWear));
        Assert.All(plans.Plans, p => Assert.True(p.Stints.Count(s => s.Compound == "Medium") <= 1)); // one medium listed

        var twoHards = plans.Plans.First(p => p.Stints.Count(s => s.Compound == "Hard") == 2);
        Assert.Equal([5.0, 40.0], twoHards.Stints.Where(s => s.Compound == "Hard").Select(s => s.StartWear));
    }

    [Fact]
    public void A_safety_car_before_the_stop_makes_pitting_under_it_pay()
    {
        var library = Library(20, Model("Medium", 80.0, 0.10), Model("Hard", 80.0, 0.05));
        var stop = StrategyPlanner.Plan(library, 30, new PlannerOptions { MaxStops = 1 }).Best!.Stops[0];

        var sc = stop.SafetyCar!;
        Assert.Equal(stop.InLap, sc.LastLap);
        Assert.True(sc.FirstLap < stop.InLap);
        Assert.InRange(sc.FirstLap, 3, stop.InLap);
        Assert.True(sc.GainSeconds >= 20 * 0.55 - 1e-9); // at the planned in-lap the stop simply costs less

        // A virtual safety car saves less of the stop, so it pays over fewer laps.
        var vsc = stop.VirtualSafetyCar!;
        Assert.True(vsc.FirstLap >= sc.FirstLap);
        Assert.True(vsc.GainSeconds < sc.GainSeconds);

        // The next set's life bounds the window: a hard that lasts 22 laps can't run the rest of the race from before lap 8.
        var shortHard = Library(20, Model("Medium", 80.0, 0.10), Model("Hard", 80.0, 0.05, life: 22));
        var bounded = StrategyPlanner.Plan(shortHard, 30, new PlannerOptions { MaxStops = 1 }).Best!.Stops[0];
        Assert.Equal(8, bounded.SafetyCar!.FirstLap);
        Assert.Equal(bounded.InLap, bounded.SafetyCar.LastLap);
    }

    [Fact]
    public void Game_sets_plan_a_race_from_its_start_and_other_sessions_from_their_end()
    {
        static TyreSet Set(byte visual, byte wear, bool available = true) => new(0, visual, wear, available, 0, 20, 20, 0, false);
        static TyreSetsChange Snapshot(int lap, int fitted, params TyreSet[] sets) => new(lap, lap * 90.0, sets, fitted);

        var snapshots = new[]
        {
            Snapshot(0, 0, Set(17, 0), Set(16, 12), Set(18, 0), Set(7, 0)),
            Snapshot(1, 2, Set(17, 0), Set(16, 12), Set(18, 0), Set(7, 0)), // the hard put on, on the grid
            Snapshot(1, 2, Set(17, 0), Set(16, 12), Set(18, 2), Set(7, 0)), // driving
            Snapshot(9, 0, Set(17, 0), Set(16, 12, available: false), Set(18, 20), Set(7, 0)),
        };

        var race = RaceTyreSets.ForPlanning(snapshots, 15)!;
        Assert.Same(snapshots[1], race);
        Assert.Equal(new PlannerSet("Hard"), RaceTyreSets.Fitted(race));
        Assert.Equal([new PlannerSet("Medium"), new PlannerSet("Soft", 12), new PlannerSet("Hard")], RaceTyreSets.Available(race)); // no inter

        var qualifying = RaceTyreSets.ForPlanning(snapshots, 10)!;
        Assert.Same(snapshots[^1], qualifying);
        Assert.DoesNotContain(RaceTyreSets.Available(qualifying), s => s.Compound == "Soft"); // returned
        Assert.Null(RaceTyreSets.ForPlanning([], 15));
    }
}
