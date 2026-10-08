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

    private static readonly ErsCarModel Model = new(GameFormat.F1_26, 99, 1, 1, Map, [new StraightLineFit(0, Efficiency, Drag, 0, 1, 0, 1000)], []);

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

    /// <summary>One lap, Medium on the straights, from <paramref name="store"/> J; harvests 400 kW under braking.</summary>
    private static List<TelemetrySample> DriveLap(double store)
    {
        var samples = new List<TelemetrySample>();
        double t = 0, d = 0, v = CornerSpeed, harvested = 0, deployed = 0;
        const double dt = 0.02;
        while (d < Length)
        {
            var corner = InCorner(d);
            var braking = !corner && v >= Envelope(d) - 0.01;
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

            samples.Add(new TelemetrySample
            {
                LapNumber = 2, SessionTime = t, LapDistance = d, Speed = (int)Math.Round(v * 3.6), Throttle = full ? 1 : corner ? 0.5 : 0,
                Brake = braking ? 1 : 0, ErsDeployMode = DeployModes.Medium, ErsStoreEnergy = store, ErsHarvestedMguk = harvested, ErsDeployed = deployed,
                EnginePowerIce = full ? Ice : 100_000, EnginePowerMguk = mguk, GForceLon = a / 9.81, FuelInTank = Fuel,
            });

            var next = Math.Max(1, v + a * dt);
            if (!corner)
            {
                next = Math.Min(next, Envelope(d + v * dt));
            }

            d += (v + next) / 2 * dt;
            v = corner && !InCorner(d) ? v : next;
            t += dt;
        }

        return samples;
    }

    private static LapSimulator Simulator(double startStore, double reserve = 0)
    {
        var samples = DriveLap(startStore);
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

        var fast = ErsCoach.Advise(plan, 500, 2_900_000, DeployModes.Overtake, 260);
        Assert.Equal((DeployModes.Medium, false, DeployModes.Overtake, "1250 M"), (fast.NowMode, fast.OnPlan, fast.NextMode, fast.NextAt));

        var between = ErsCoach.Advise(plan, 1500, 2_000_000, DeployModes.None, 150);
        Assert.Equal((null, true, DeployModes.Overtake, "250 M"), (between.NowMode, between.OnPlan, between.NextMode, between.NextAt));
        Assert.Null(between.Zone);

        var partial = ErsCoach.Advise(plan, 1825, 2_000_000, DeployModes.Overtake, 200);
        Assert.Equal((DeployModes.Overtake, DeployModes.None, "300 M"), (partial.NowMode, partial.NextMode, partial.NextAt)); // switch at 2125 m

        var last = ErsCoach.Advise(plan, 2900, 2_000_000, DeployModes.None, 150);
        Assert.Equal((DeployModes.Overtake, "400 M"), (last.NextMode, last.NextAt)); // the first zone of the next lap
    }
}
