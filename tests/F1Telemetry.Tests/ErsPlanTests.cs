using F1Telemetry.Core.Energy;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol;

namespace F1Telemetry.Tests;

/// <summary>
/// The lap simulator and optimiser on a made-up 3 km track with two straights, driven with known physics: Medium all
/// the way (120 kW) on a car whose straight-line fit and deploy map are exact.
/// </summary>
public class ErsPlanTests
{
    private const double Ice = 400_000, Efficiency = 1, Drag = 0.5, Fuel = 10;
    private const double CornerSpeed = 25, Braking = 30, Length = 3000;
    private static readonly double Mass = ErsModelBuilder.CarMass(GameFormat.F1_26) + Fuel;

    private static readonly DeployMap Map = new(
    [
        new DeployCurve(DeployModes.Medium, [.. Enumerable.Range(10, 26).Select(b => new DeployPoint(b * 10, 120_000, 50))], null),
        new DeployCurve(DeployModes.Overtake, [.. Enumerable.Range(10, 26).Select(b => new DeployPoint(b * 10, b * 10 < 250 ? 300_000 : 100_000, 50))], 250),
    ]);

    internal static readonly ErsCarModel Model = new(GameFormat.F1_26, 99, 1, 1, Map, [new StraightLineFit(0, Efficiency, Drag, 0, 1, 0, 1000)], []);

    private static bool InCorner(double d) => d is < 300 or (>= 1450 and < 1750);

    /// <summary>Speed cap ahead of the corners: the fastest the car can go and still brake to corner speed.</summary>
    private static double Envelope(double d)
    {
        if (InCorner(d))
        {
            return CornerSpeed;
        }

        var next = d < 1450 ? 1450 : Length; // the next corner (the lap wraps into the first)
        return Math.Sqrt(CornerSpeed * CornerSpeed + 2 * Braking * (next - d));
    }

    /// <summary>
    /// One lap, Medium on the straights, from <paramref name="store"/> J; harvests 400 kW under braking. The line is
    /// <paramref name="line"/> m round from the first corner: driven from that corner, timed from the line.
    /// </summary>
    internal static List<TelemetrySample> DriveLap(double store, double harvestLimit = 0, double line = 0)
    {
        var samples = new List<TelemetrySample>();
        double t = 0, d = 0, v = CornerSpeed, harvested = 0, deployed = 0;
        const double dt = 0.02;
        while (d < Length + line)
        {
            var corner = InCorner(d % Length);
            var braking = !corner && v >= Envelope(d % Length) - 0.01;
            var full = !corner && !braking;
            var mguk = full && store > 0 ? Math.Min(120_000, store / dt) : 0;
            double a;
            if (corner)
            {
                a = 0;
            }
            else if (braking)
            {
                a = -Braking;
                var harvest = Math.Min(400_000 * dt, 4_000_000 - store);
                harvested += harvest;
                store += harvest;
            }
            else
            {
                a = Efficiency * (Ice + mguk) / (Mass * v) - Drag * v * v / Mass;
                store -= mguk * dt;
                deployed += mguk * dt;
            }

            if (d >= line)
            {
                samples.Add(new TelemetrySample
                {
                    LapNumber = 2, SessionTime = t, LapDistance = d - line, Speed = (int)Math.Round(v * 3.6), Throttle = full ? 1 : corner ? 0.5 : 0,
                    Brake = braking ? 1 : 0, ErsDeployMode = DeployModes.Medium, ErsStoreEnergy = store, ErsHarvestedMguk = harvested, ErsDeployed = deployed,
                    EnginePowerIce = full ? Ice : 100_000, EnginePowerMguk = mguk, GForceLon = a / 9.81, FuelInTank = Fuel, ErsHarvestLimit = harvestLimit,
                });
            }

            var next = Math.Max(1, v + a * dt);
            if (!corner)
            {
                next = Math.Min(next, Envelope((d + v * dt) % Length));
            }

            d += (v + next) / 2 * dt;
            v = corner && !InCorner(d % Length) ? v : next;
            t += dt;
        }

        return samples;
    }

    private static LapSimulator Simulator(double startStore, double reserve = 0, double line = 0)
    {
        var samples = DriveLap(startStore, line: line);
        var lap = LapProfile.From(samples, GameFormat.F1_26)!;
        return LapSimulator.Create(lap, Model, reserve)!;
    }

    [Fact]
    public void The_lap_replays_as_driven_and_with_the_deploy_map()
    {
        var check = ErsOptimizer.Check(Simulator(2_000_000));

        Assert.Equal(check.ActualSeconds, check.DrivenSeconds, 0.05);
        Assert.True(check.IsTrusted, $"map replay off by {check.Error:0.000} s");
        Assert.Equal(check.ActualEndStore, check.MapEndStore, 60_000.0);
    }

    [Fact]
    public void Qualifying_spends_a_full_battery_where_it_buys_the_most()
    {
        var simulator = Simulator(2_000_000);
        var full = ErsOptimizer.Plan(simulator, PlanKind.Qualifying);
        var half = ErsOptimizer.Plan(simulator, PlanKind.Qualifying, qualifyingStart: 2_000_000);

        Assert.Equal(4_000_000, full.Plan.StartStore);
        Assert.True(full.Gain > 0.3, $"gain {full.Gain:0.000} s");
        Assert.True(full.Plan.Seconds <= half.Plan.Seconds); // more charge is never slower
        Assert.True(full.Plan.EndStore < full.Plan.StartStore);
        Assert.Equal(2, full.Plan.Zones.Count);
        Assert.All(full.Plan.Zones, z => Assert.Equal(DeployModes.Overtake, z.Choice.First)); // 300 kW out of the corners
        Assert.All(full.Plan.Zones, z => Assert.True(z.Gain > 0));
        Assert.Null(full.Attack);
    }

    [Fact]
    public void A_race_lap_ends_with_the_charge_it_started_with_and_prices_an_extra_MJ()
    {
        var simulator = Simulator(2_000_000, reserve: 300_000);
        var race = ErsOptimizer.Plan(simulator, PlanKind.Race);

        Assert.True(race.Plan.EndStore >= race.Plan.StartStore - ErsOptimizer.Quantum, $"{race.Plan.StartStore / 1e6:0.00} → {race.Plan.EndStore / 1e6:0.00} MJ");
        Assert.True(race.Plan.StartStore >= 300_000);
        Assert.All(race.Plan.Stores, s => Assert.True(s >= 300_000 - ErsOptimizer.Quantum, $"below the reserve: {s / 1e6:0.00} MJ"));
        Assert.True(race.AttackGain > 0);
        Assert.True(race.RecoverCost > 0);

        var qualifying = ErsOptimizer.Plan(simulator, PlanKind.Qualifying);
        Assert.True(qualifying.Plan.Seconds < race.Plan.Seconds); // spending everything is faster than holding it
    }

    [Fact]
    public void A_race_lap_deploys_what_it_harvests_so_every_lap_can_run_it()
    {
        var race = ErsOptimizer.Plan(Simulator(2_000_000), PlanKind.Race).Plan;

        Assert.True(race.Deployed > 0);
        Assert.Equal(race.Harvested, race.Deployed, 2 * ErsOptimizer.Quantum);
        Assert.Equal(race.StartStore, race.EndStore, 2 * ErsOptimizer.Quantum);

        var qualifying = ErsOptimizer.Plan(Simulator(2_000_000), PlanKind.Qualifying).Plan;
        Assert.True(qualifying.Deployed > race.Deployed); // the full battery on top of the harvest
        Assert.Equal(qualifying.StartStore - qualifying.EndStore, qualifying.Deployed - qualifying.Harvested, 1.0);
    }

    /// <summary>
    /// The planner's model of the battery is checked against what the lap really did: the lap's own modes, replayed with
    /// what the map says they cost, end the lap with the charge it really had. A map that costs a tenth less than the
    /// deploy counter paid is calibrated back.
    /// </summary>
    [Fact]
    public void Replaying_the_lap_with_the_map_ends_with_the_charge_it_had()
    {
        var cheap = new DeployMap([new DeployCurve(DeployModes.Medium, [.. Enumerable.Range(10, 26).Select(b => new DeployPoint(b * 10, 120_000, 50, 108_000))], null)]);
        var simulator = LapSimulator.Create(LapProfile.From(DriveLap(2_000_000), GameFormat.F1_26)!, Model with { Deploy = cheap })!;

        Assert.Equal(120_000 / 108_000.0, simulator.DrawScale, 0.02);
        var check = ErsOptimizer.Check(simulator);
        Assert.Equal(check.ActualEndStore, check.MapEndStore, 50_000.0); // within 50 kJ of what the lap really had
    }

    /// <summary>Flat out in None at high speed the game still deploys, and the plan pays for it.</summary>
    [Fact]
    public void None_costs_what_it_deploys()
    {
        var withNone = Map with { Curves = [.. Map.Curves, new DeployCurve(DeployModes.None, [.. Enumerable.Range(10, 26).Select(b => new DeployPoint(b * 10, 60_000, 50))], null)] };
        var lap = LapProfile.From(DriveLap(2_000_000), GameFormat.F1_26)!;
        var zone = ErsOptimizer.Stages(lap).First(s => s.HasZone);
        var none = new DeployOption(DeployModes.None, DeployModes.None);

        var free = LapSimulator.Create(lap, Model)!.Run(zone.From, zone.DeployEnd, zone.To, none, 2_000_000);
        var paid = LapSimulator.Create(lap, Model with { Deploy = withNone })!.Run(zone.From, zone.DeployEnd, zone.To, none, 2_000_000);

        Assert.True(paid.ExitStore < free.ExitStore - 100_000, $"{free.ExitStore / 1e6:0.00} → {paid.ExitStore / 1e6:0.00} MJ");
        Assert.True(paid.Seconds < free.Seconds);
    }

    /// <summary>
    /// Where the game clips in None, the harvest goes into the battery and its power comes off the wheels; once the lap
    /// has reached its harvest limit it clips no more.
    /// </summary>
    [Fact]
    public void Clipping_takes_power_from_the_wheels_and_stops_at_the_harvest_limit()
    {
        var lap = LapProfile.From(DriveLap(2_000_000, harvestLimit: 6_000_000), GameFormat.F1_26)!;
        var zone = ErsOptimizer.Stages(lap).First(s => s.HasZone);
        var count = lap.Segments.Count;
        var clip = Enumerable.Range(0, DeployModes.Overtake + 1).Select(_ => new HarvestRate?[count]).ToArray();
        Array.Fill(clip[DeployModes.None], new HarvestRate(80_000, 50));
        var map = new PositionMap([.. Enumerable.Range(0, DeployModes.Overtake + 1).Select(_ => new DeployCell?[count])], clip, new HarvestRate?[count]);
        var none = new DeployOption(DeployModes.None, DeployModes.None);

        var plain = LapSimulator.Create(lap, Model)!.Run(zone.From, zone.DeployEnd, zone.DeployEnd, none, 1_000_000);
        var clipping = LapSimulator.Create(lap, Model with { Positions = map })!;
        var clipped = clipping.Run(zone.From, zone.DeployEnd, zone.DeployEnd, none, 1_000_000);
        var atLimit = clipping.Run(zone.From, zone.DeployEnd, zone.DeployEnd, none, 1_000_000, harvested: 6_000_000);

        Assert.True(clipped.Seconds > plain.Seconds + 0.05, $"{plain.Seconds:0.000} → {clipped.Seconds:0.000} s");
        Assert.Equal(80_000 * clipped.Seconds, clipped.ExitHarvested - plain.ExitHarvested, 2_000.0);
        Assert.Equal(clipped.ExitHarvested - plain.ExitHarvested, clipped.ExitStore - plain.ExitStore, 1.0);
        Assert.Equal(plain.Seconds, atLimit.Seconds, 1e-6);
        Assert.Equal(6_000_000, atLimit.ExitHarvested);
    }

    /// <summary>
    /// Entering a zone with a battery the plan didn't expect, the rest of the lap is planned again from it: the zone
    /// already driven keeps its plan, and an empty battery is never asked for a deploy mode.
    /// </summary>
    [Fact]
    public void Entering_a_zone_off_plan_re_plans_the_rest_from_the_battery_the_car_has()
    {
        var planner = new ErsPlanner(Simulator(2_000_000, reserve: 300_000));
        var steady = planner.Plan(PlanKind.Race).Plan.StartStore;
        var plan = planner.PlanFrom(PlanKind.Race, steady, steady);
        Assert.Equal(DeployModes.Overtake, plan.Zones[1].Choice.First);

        var empty = planner.Continue(plan, 2, 0, 0, steady)!;

        Assert.Equal(plan.Zones[0], empty.Zones[0]);
        Assert.Equal(plan.StartStore, empty.StartStore);
        Assert.Equal(0, empty.Zones[1].EntryStore);
        Assert.Equal(DeployModes.None, empty.Zones[1].Choice.First);
        Assert.Equal(plan.Modes[..10], empty.Modes[..10]);
        Assert.Null(planner.Continue(plan, 3, 0, 0, steady)); // the lap has two zones
    }

    [Fact]
    public void The_typical_lap_averages_the_session_and_plans_like_its_laps()
    {
        var low = LapProfile.From(DriveLap(1_000_000), GameFormat.F1_26)!;
        var high = LapProfile.From(DriveLap(3_000_000), GameFormat.F1_26)!;
        var typical = LapProfile.Average([low, high])!;

        Assert.Equal(2, typical.LapCount);
        Assert.Equal(0, typical.LapNumber);
        Assert.Equal((low.StartStore + high.StartStore) / 2, typical.StartStore, 1.0);
        Assert.Equal((low.ActualSeconds + high.ActualSeconds) / 2, typical.ActualSeconds, 1e-6);
        Assert.Equal(Math.Min(low.Segments.Count, high.Segments.Count), typical.Segments.Count);
        Assert.Equal(low.Segments.Count(s => s.FullThrottle), typical.Segments.Count(s => s.FullThrottle), 2.0);
        Assert.Same(low, LapProfile.Average([low]));
        Assert.Null(LapProfile.Average([]));

        // Driven the same way, the average replays and plans like either lap.
        var simulator = LapSimulator.Create(typical, Model)!;
        Assert.True(ErsOptimizer.Check(simulator).IsTrusted);
        var race = ErsOptimizer.Plan(simulator, PlanKind.Race);
        var single = ErsOptimizer.Plan(LapSimulator.Create(low, Model)!, PlanKind.Race);
        Assert.Equal(single.Plan.Seconds, race.Plan.Seconds, 0.1);
    }

    [Fact]
    public void Live_replans_aim_for_the_steady_level_from_whatever_the_battery_has()
    {
        var planner = new ErsPlanner(Simulator(2_000_000, reserve: 300_000));
        var steady = planner.Plan(PlanKind.Race).Plan.StartStore;

        var low = planner.PlanFrom(PlanKind.Race, 500_000, steady);
        Assert.Equal(500_000, low.StartStore, 1.0);
        Assert.True(low.EndStore > low.StartStore); // a low battery climbs back towards the steady level

        var attack = planner.PlanFrom(PlanKind.Race, steady, steady - ErsOptimizer.OneMj);
        var normal = planner.PlanFrom(PlanKind.Race, steady, steady);
        Assert.True(attack.Seconds < normal.Seconds);
        Assert.True(attack.EndStore >= steady - ErsOptimizer.OneMj - ErsOptimizer.Quantum);
    }

    [Fact]
    public void The_coach_reads_the_plan_at_the_cars_position()
    {
        var modes = new int[120];
        var stores = Enumerable.Range(0, 120).Select(i => 3_000_000 - i * 10_000.0).ToArray();
        var plan = new LapPlan(PlanKind.Race, 3_000_000, 1_810_000, 80, [
            new ZonePlan(1, 300, 1300, new DeployOption(DeployModes.Overtake, DeployModes.Medium, UntilKmh: 250), 0, 0, 0, 0),
            new ZonePlan(2, 1750, 2875, new DeployOption(DeployModes.Overtake, DeployModes.None, UntilFraction: 1 / 3.0), 0, 0, 0, 0),
        ], modes, stores);

        var early = ErsCoach.Advise(plan, 500, 2_900_000, DeployModes.Overtake, 200);
        Assert.Equal((1, DeployModes.Overtake, true, DeployModes.Medium, "250 KM/H"), (early.Zone!.Number, early.NowMode, early.OnPlan, early.NextMode, early.NextAt));
        Assert.Equal(stores[20], early.Target);
        Assert.Equal(2_900_000 - stores[20], early.Delta);
        Assert.Null(early.NextIn); // a speed switch has no distance

        var fast = ErsCoach.Advise(plan, 500, 2_900_000, DeployModes.Overtake, 260);
        Assert.Equal((DeployModes.Medium, false, DeployModes.Overtake, "1250 M"), (fast.NowMode, fast.OnPlan, fast.NextMode, fast.NextAt));

        var between = ErsCoach.Advise(plan, 1500, 2_000_000, DeployModes.None, 150);
        Assert.Equal((null, true, DeployModes.Overtake, "250 M"), (between.NowMode, between.OnPlan, between.NextMode, between.NextAt));
        Assert.Null(between.Zone);

        var partial = ErsCoach.Advise(plan, 1825, 2_000_000, DeployModes.Overtake, 200);
        Assert.Equal((DeployModes.Overtake, DeployModes.None, "300 M"), (partial.NowMode, partial.NextMode, partial.NextAt)); // switch at 2125 m
        Assert.Equal(300, partial.NextIn!.Value, 6);

        var last = ErsCoach.Advise(plan, 2900, 2_000_000, DeployModes.None, 150);
        Assert.Equal((DeployModes.Overtake, "400 M"), (last.NextMode, last.NextAt)); // the first zone of the next lap
    }

    // The line 1000 m round from the first corner: halfway down the first straight, so each lap starts and ends flat out.
    private const double LineOnStraight = 1000;

    /// <summary>
    /// Where the line is on a straight, a race lap runs from the first corner exit after it to the same point a lap
    /// later: its last zone goes from the last corner across the line, and it repeats with the charge it started with.
    /// </summary>
    [Fact]
    public void A_race_lap_runs_its_last_zone_across_a_line_on_a_straight()
    {
        var simulator = Simulator(2_000_000, reserve: 300_000, line: LineOnStraight);
        Assert.True(simulator.Lap.LineOnStraight);
        var planner = new ErsPlanner(simulator, PlanKind.Race);
        Assert.True(planner.CrossesLine);

        var race = planner.Plan(PlanKind.Race);
        var plan = race.Plan;
        Assert.False(plan.StartsAtLine);
        Assert.True(plan.CrossesLine);
        var across = plan.Zones[^1];
        Assert.True(across.CrossesLine);
        Assert.True(across.From < simulator.Lap.Length && across.To > simulator.Lap.Length);
        Assert.True(across.Contains(across.From) && across.Contains(across.EndOnLap - 1) && !across.Contains(plan.Zones[0].From));
        Assert.Equal(2, plan.Zones.Count); // the two straights, the first one split by the line
        Assert.True(plan.EndStore >= plan.StartStore - ErsOptimizer.Quantum, $"{plan.StartStore / 1e6:0.00} → {plan.EndStore / 1e6:0.00} MJ");
        Assert.Equal(plan.Stores[^1], plan.LineStore);
        Assert.All(plan.Stores, s => Assert.True(s >= 300_000 - ErsOptimizer.Quantum, $"below the reserve: {s / 1e6:0.00} MJ"));
        Assert.True(race.RecoverCost > 0); // the battery is at the reserve at the line here: nothing more to spend, so no attack gain

        // A qualifying lap still ends at the line, and a lap planned from the line starts with its own zone there.
        Assert.False(new ErsPlanner(simulator, PlanKind.Qualifying).CrossesLine);
        Assert.All(ErsOptimizer.Plan(simulator, PlanKind.Qualifying).Plan.Zones, z => Assert.False(z.CrossesLine));
        var fromLine = planner.PlanFrom(PlanKind.Race, plan.LineStore, plan.StartStore);
        Assert.True(fromLine.StartsAtLine);
        Assert.Equal(3, fromLine.Zones.Count);
        Assert.Equal(0, fromLine.Zones[0].From);
        Assert.True(fromLine.Zones[^1].CrossesLine);
    }

    /// <summary>
    /// What a zone out of the last corner deploys keeps paying past the line: the car is faster all the way down the
    /// straight, not just to the line, so deploying there gains more than the stretch to the line alone shows.
    /// </summary>
    [Fact]
    public void The_speed_out_of_the_last_corner_carries_across_the_line()
    {
        var simulator = Simulator(2_000_000, line: LineOnStraight);
        var stages = ErsOptimizer.RaceStages(simulator.Lap);
        var (first, across) = (stages[0], stages[^1]);
        var count = simulator.Lap.Segments.Count;
        Assert.Equal(count + first.To, across.To);
        var (overtake, none) = (new DeployOption(DeployModes.Overtake, DeployModes.Overtake), new DeployOption(DeployModes.None, DeployModes.None));

        double Seconds(DeployOption option) => simulator.Run(across.From, across.DeployEnd, across.To, option, 4_000_000).Seconds;
        double ToLine(DeployOption option) => simulator.Run(across.From, count, count, option, 4_000_000).Seconds;
        var gain = Seconds(none) - Seconds(overtake);
        var toLine = ToLine(none) - ToLine(overtake);

        Assert.True(toLine > 0);
        Assert.True(gain > toLine + 0.02, $"across the line {gain:0.000} s, to the line {toLine:0.000} s");
    }

    /// <summary>The game counts the harvest limit per lap: a lap that reached it before the line harvests again after it.</summary>
    [Fact]
    public void The_harvest_limit_starts_again_at_the_line()
    {
        var lap = LapProfile.From(DriveLap(2_000_000, harvestLimit: 6_000_000, line: LineOnStraight), GameFormat.F1_26)!;
        var simulator = LapSimulator.Create(lap, Model)!;
        var across = ErsOptimizer.RaceStages(lap)[^1];
        var none = new DeployOption(DeployModes.None, DeployModes.None);

        var capped = simulator.Run(across.From, across.DeployEnd, across.To, none, 1_000_000, harvested: 6_000_000);

        Assert.Equal(6_000_000, capped.LineHarvested);
        Assert.True(capped.ExitHarvested > 100_000, $"harvested {capped.ExitHarvested / 1e6:0.00} MJ after the line"); // braking for the first corner
    }

    /// <summary>In a zone across the line the coach reads the switch point on from the zone's start, past the line.</summary>
    [Fact]
    public void The_coach_reads_a_zone_across_the_line()
    {
        var modes = new int[120];
        var stores = new double[120];
        var plan = new LapPlan(PlanKind.Race, 2_000_000, 2_000_000, 80, [
            new ZonePlan(1, 750, 1900, new DeployOption(DeployModes.Overtake, DeployModes.None), 0, 0, 0, 0) { LapLength = 3000 },
            new ZonePlan(2, 2300, 3300, new DeployOption(DeployModes.Overtake, DeployModes.None, UntilFraction: 0.5), 0, 0, 0, 0) { LapLength = 3000 },
        ], modes, stores);

        var before = ErsCoach.Advise(plan, 2500, 2_000_000, DeployModes.Overtake, 250);
        Assert.Equal((2, DeployModes.Overtake, DeployModes.None, "300 M"), (before.Zone!.Number, before.NowMode, before.NextMode, before.NextAt)); // switch at 2800 m

        var after = ErsCoach.Advise(plan, 100, 2_000_000, DeployModes.None, 300);
        Assert.Equal((2, DeployModes.None), (after.Zone!.Number, after.NowMode)); // 400 m into the zone, past its switch
        Assert.Equal(DeployModes.Overtake, after.NextMode); // the next zone
    }
}
