using F1Telemetry.Core.Analytics;
using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Lookups;

namespace F1Telemetry.Tests;

/// <summary>Fuel by kind of lap: each run read as a race, race sim, qualifying run or quali sim, and the fuel calculators.</summary>
public sealed class FuelModelTests
{
    private const int Practice = 1;
    private const int Qualifying = 5;
    private const int Race = 15;

    /// <summary>One lap: its time (0 = untimed: an out- or in-lap) and what it burnt; a lap may be under a safety car.</summary>
    private sealed record L(double Seconds, double Kg, bool SafetyCar = false);

    /// <summary>A session of runs, each a set on a fill of fuel: its start fuel and its laps.</summary>
    private static RaceInput Session(long id, int sessionType, int length, params (string Compound, double StartKg, L[] Laps)[] runs)
    {
        var laps = new List<LapRecord>();
        var aggregates = new List<LapAggregate>();
        var number = 0;
        for (var r = 0; r < runs.Length; r++)
        {
            var fuel = runs[r].StartKg;
            var age = 0;
            foreach (var lap in runs[r].Laps)
            {
                number++;
                laps.Add(new LapRecord
                {
                    LapNumber = number,
                    LapTimeMs = (uint)Math.Round(lap.Seconds * 1000),
                    Compound = runs[r].Compound,
                    StintIndex = r,
                    LapType = lap.SafetyCar ? LapType.SafetyCar : LapType.Regular,
                });
                var w = age++ * 1.0;
                aggregates.Add(new LapAggregate(number, fuel - lap.Kg, fuel, w, w + 1, w, w + 1, w, w + 1, w, w + 1));
                fuel -= lap.Kg;
            }
        }

        var classified = LapClassifier.Classify(laps, sessionType).ToList();
        var analysis = StintAnalyzer.Analyze(classified, aggregates, default, sessionType, new StrategyOptions())!;
        return new RaceInput(new RecordingInfo(id, "", "", 11, "Monza", sessionType, GameFormat.F1_25, DateTimeOffset.UnixEpoch.AddHours(id), null),
            classified, aggregates, analysis) { RaceLength = length };
    }

    private static L[] Repeat(int count, double seconds, double kg) => [.. Enumerable.Range(0, count).Select(i => new L(seconds + 0.05 * i, kg))];

    /// <summary>Practice: a qualifying simulation on a light tank, then a race simulation on a heavy one.</summary>
    private static RaceInput PracticeWithBothSims(long id = 1, int length = RaceLengths.Medium) => Session(id, Practice, length,
        ("Soft", 9.0, [new L(0, 1.0), new L(80.0, 2.0), new L(92.0, 1.1), new L(80.1, 2.0), new L(0, 0.9)]),
        ("Medium", 40.0, [new L(0, 1.0), .. Repeat(8, 82.0, 1.6), new L(0, 1.0)]));

    [Fact]
    public void Practice_runs_are_split_into_a_quali_sim_and_a_race_sim()
    {
        var runs = FuelModelBuilder.Runs(PracticeWithBothSims());

        Assert.Equal(2, runs.Count);
        Assert.Equal(FuelRunKind.QualiSim, runs[0].Kind);
        Assert.Equal(FuelRunKind.RaceSim, runs[1].Kind);
        Assert.Equal([FuelLapKind.OutLap, FuelLapKind.Push, FuelLapKind.CoolDown, FuelLapKind.Push, FuelLapKind.InLap], runs[0].Laps.Select(l => l.Kind));
        Assert.Equal(2.0, runs[0].PushKgPerLap, 6);
        Assert.Equal(1.6, runs[1].PushKgPerLap, 6);
        Assert.Equal(40.0, runs[1].StartFuelKg, 6);
        Assert.Contains("light tank", runs[0].Why);
        Assert.Contains("long run", runs[1].Why);
    }

    [Fact]
    public void Race_fuel_uses_race_pace_and_qualifying_fuel_push_laps()
    {
        var model = FuelModelBuilder.Build([PracticeWithBothSims()], RaceLengths.Medium);

        Assert.Null(model.Race);
        Assert.Equal(1.6, model.RaceBurn!.KgPerLap, 6); // the race sim's, not pooled with the quali sim's 2.0
        Assert.Equal(2.0, model.Push!.KgPerLap, 6);
        Assert.Equal(1.1, model.CoolDown!.KgPerLap, 6);
        Assert.Equal(1.0, model.OutLap!.KgPerLap, 6); // the qualifying run's out-lap

        // A race at that length takes over from the race sim; its safety car lap stays out of race pace.
        var race = Session(2, Race, RaceLengths.Medium, ("Medium", 30.0, [.. Repeat(10, 81.0, 1.5), new L(110.0, 0.7, SafetyCar: true), .. Repeat(4, 81.5, 1.5)]));
        model = FuelModelBuilder.Build([PracticeWithBothSims(), race], RaceLengths.Medium);
        Assert.Equal(FuelRunKind.Race, model.Runs.Last().Kind);
        Assert.Equal(1.5, model.RaceBurn!.KgPerLap, 6);
        Assert.Equal(1.6, model.RaceSim!.KgPerLap, 6);
        Assert.Equal(0.7, model.SafetyCar!.KgPerLap, 6);
    }

    [Fact]
    public void Burns_come_from_the_race_length_planned_for_else_from_the_others()
    {
        var medium = PracticeWithBothSims(1, RaceLengths.Medium);
        var full = Session(2, Practice, RaceLengths.Full, ("Medium", 60.0, [new L(0, 1.0), .. Repeat(8, 82.0, 1.2), new L(0, 1.0)]));

        var atFull = FuelModelBuilder.Build([medium, full], RaceLengths.Full);
        Assert.Equal(1.2, atFull.RaceBurn!.KgPerLap, 6);
        Assert.False(atFull.RaceBurn.FromOtherLengths);
        Assert.True(atFull.Push!.FromOtherLengths); // no quali sim at full length

        var atLong = FuelModelBuilder.Build([medium, full], RaceLengths.Long);
        Assert.True(atLong.RaceBurn!.FromOtherLengths);
        Assert.Equal(1.4, atLong.RaceBurn.KgPerLap, 6);
    }

    [Fact]
    public void Qualifying_sessions_and_a_fast_short_run_on_a_heavy_tank_count_as_qualifying()
    {
        var quali = Session(1, Qualifying, RaceLengths.Medium, ("Soft", 30.0, [new L(0, 1.0), new L(79.0, 2.1), new L(0, 0.9)]));
        Assert.Equal(FuelRunKind.Qualifying, FuelModelBuilder.Runs(quali).Single().Kind);

        // No fuel change in the garage: the quali sim is told by its pace, a slow short run is neither.
        var practice = Session(2, Practice, RaceLengths.Medium,
            ("Medium", 50.0, [new L(0, 1.0), .. Repeat(6, 82.0, 1.6), new L(0, 1.0)]),
            ("Soft", 50.0, [new L(0, 1.0), new L(80.0, 2.0), new L(0, 1.0)]),
            ("Hard", 50.0, [new L(0, 1.0), new L(83.5, 1.6), new L(0, 1.0)]));
        var runs = FuelModelBuilder.Runs(practice);
        Assert.Equal([FuelRunKind.RaceSim, FuelRunKind.QualiSim, FuelRunKind.Other], runs.Select(r => r.Kind));
    }

    [Fact]
    public void Race_plan_adds_the_formation_lap_and_margin_and_prices_lighter_starts()
    {
        var model = FuelModelBuilder.Build([PracticeWithBothSims()]);
        var plan = model.PlanRace(laps: 20, marginLaps: 1, formationLap: true, fuelEffect: 0.03)!;

        Assert.True(plan.FromRaceSims);
        Assert.Equal(1.0, plan.FormationKg, 6); // the out-lap's burn
        Assert.Equal(20 * 1.6 + 1.0, plan.NeededKg, 6);
        Assert.Equal(plan.NeededKg + 1.6, plan.StartKg, 6);
        Assert.Equal(0.03 * 1.6 * 20, plan.MarginCostSeconds, 6);

        var oneLess = plan.Lighter[0];
        Assert.Equal(plan.NeededKg - 1.6, oneLess.StartKg, 6);
        Assert.Equal(1.6 / 20, oneLess.SaveKgPerLap, 6);
        Assert.Equal(5.0, oneLess.SavePercent, 6);
        Assert.Equal(0.03 * 1.6 / 2 * 20, oneLess.GainSeconds, 6);

        Assert.Equal(20 * 1.6, model.PlanRace(20, 0, false, 0.03)!.StartKg, 6);
    }

    [Fact]
    public void Qualifying_plan_counts_out_lap_pushes_cool_downs_and_in_lap()
    {
        var model = FuelModelBuilder.Build([PracticeWithBothSims()]);

        var two = model.PlanQualifying(pushLaps: 2, inLap: true)!;
        Assert.Equal(0.95, model.InLap!.KgPerLap, 6); // both runs' in-laps (0.9 and 1.0)
        Assert.Equal(1.0 + 2 * 2.0 + 1.1 + 0.95, two.TotalKg, 6);
        Assert.All(two.Parts, p => Assert.True(p.Measured));

        var one = model.PlanQualifying(pushLaps: 1, inLap: false)!;
        Assert.Equal(1.0 + 2.0, one.TotalKg, 6);

        // Without push laps it falls back on the race burn, and says so.
        var raceOnly = FuelModelBuilder.Build([Session(1, Race, RaceLengths.Medium, ("Medium", 30.0, Repeat(10, 81.0, 1.5)))]);
        var fallback = raceOnly.PlanQualifying(1, true)!;
        Assert.True(fallback.PushFromRace);
        Assert.Equal(1.5 * (1 + 2 * FuelModel.SlowLapShare), fallback.TotalKg, 6);
    }

    [Fact]
    public void The_compound_library_plans_with_the_race_pace_burn()
    {
        var library = CompoundLibraryBuilder.Build([PracticeWithBothSims()], null);

        Assert.Equal(1.6, library.FuelPerLap, 6);
    }
}
