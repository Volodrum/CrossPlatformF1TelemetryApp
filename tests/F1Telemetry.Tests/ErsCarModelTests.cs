using F1Telemetry.Core.Energy;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol;

namespace F1Telemetry.Tests;

public class ErsCarModelTests
{
    private static ModelSample FullThrottle(int mode, float kmh, float mguk, float store = 2_000_000) =>
        new(1, 1, 0, 0, kmh, 1, 0, 0, store, (byte)mode, 0, 0, 410_000, mguk, 0, 10, 0);

    [Fact]
    public void Deploy_map_finds_where_overtake_fades_and_drops_modes_that_gave_nothing()
    {
        var samples = new List<ModelSample>();
        for (var kmh = 150; kmh < 330; kmh++)
        {
            for (var i = 0; i < 3; i++)
            {
                samples.Add(FullThrottle(DeployModes.Overtake, kmh, kmh < 270 ? 315_000 : 135_000));
                samples.Add(FullThrottle(DeployModes.Medium, kmh, 126_000));
                samples.Add(FullThrottle(DeployModes.Hotlap, kmh, 0));
                samples.Add(FullThrottle(DeployModes.Medium, kmh, 0, store: 100_000)); // empty battery: left out
            }
        }

        var map = ErsModelBuilder.BuildDeployMap(samples);

        Assert.Equal([DeployModes.Medium, DeployModes.Overtake], map.Curves.Select(c => c.Mode));
        Assert.Equal(270, map.FadeSpeed(DeployModes.Overtake));
        Assert.Null(map.FadeSpeed(DeployModes.Medium));
        Assert.Equal(126_000, map.Curve(DeployModes.Medium)!.PeakW);
        Assert.Equal(315_000, map.Curve(DeployModes.Overtake)!.PowerAt(240));
        Assert.Equal(135_000, map.Curve(DeployModes.Overtake)!.PowerAt(305));
        Assert.All(map.Curves.SelectMany(c => c.Points), p => Assert.Equal(30, p.Samples));
    }

    /// <summary>
    /// What a mode costs comes from the game's deploy counter, not the MGU-K output: the battery pays about a tenth more.
    /// None gets a curve where it still deploys, flat out at high speed, and the overtake override is left out.
    /// </summary>
    [Fact]
    public void Deploy_map_learns_what_each_mode_takes_from_the_battery()
    {
        var samples = new List<ModelSample>();
        double t = 0;
        float deployed = 0;
        void Drive(int mode, float kmh, float output, float draw, bool overtakeOverride = false)
        {
            for (var i = 0; i < 30; i++)
            {
                t += 0.1;
                deployed += draw * 0.1f;
                samples.Add(new ModelSample(1, 1, t, 0, kmh, 1, 0, 0, 2_000_000, (byte)mode, 0, deployed, 410_000, output, 0, 10, 0, overtakeOverride));
            }
        }

        Drive(DeployModes.Medium, 200, 180_000, 198_000);
        Drive(DeployModes.Overtake, 250, 104_000, 116_000);
        Drive(DeployModes.Overtake, 250, 190_000, 210_000, overtakeOverride: true); // the override: left out
        Drive(DeployModes.None, 150, 0, 0);
        Drive(DeployModes.None, 240, 106_000, 119_000);

        var map = ErsModelBuilder.BuildDeployMap(samples);

        Assert.Equal([DeployModes.None, DeployModes.Medium, DeployModes.Overtake], map.Curves.Select(c => c.Mode));
        Assert.Equal(198_000, map.Curve(DeployModes.Medium)!.DrawAt(205)!.Value, 1_000.0);
        Assert.Equal(180_000, map.Curve(DeployModes.Medium)!.PowerAt(205)!.Value, 1.0);
        Assert.Equal(104_000, map.Curve(DeployModes.Overtake)!.PowerAt(255)!.Value, 1.0);
        Assert.Equal(116_000, map.Curve(DeployModes.Overtake)!.DrawAt(255)!.Value, 1_000.0);
        Assert.Equal(119_000, map.Curve(DeployModes.None)!.DrawAt(245)!.Value, 1_000.0);
        Assert.Null(map.FadeSpeed(DeployModes.None));
    }

    [Fact]
    public void Deploy_curve_reads_between_bands_in_a_straight_line()
    {
        var curve = new DeployCurve(DeployModes.Medium, [new DeployPoint(130, 40_000, 50), new DeployPoint(140, 140_000, 50), new DeployPoint(170, 180_000, 50)], null);

        Assert.Equal(90_000, curve.PowerAt(140)!.Value, 1.0); // half way from the middle of 130–140 to the middle of 140–150
        Assert.Equal(40_000, curve.PowerAt(100)!.Value, 1.0); // below the curve: its first band
        Assert.Equal(140_000, curve.PowerAt(150)!.Value, 1.0); // no bands measured between 150 and 170: the nearer
        Assert.Equal(180_000, curve.PowerAt(190)!.Value, 1.0);
    }

    [Fact]
    public void Straight_line_fit_recovers_the_cars_efficiency_drag_and_resistance()
    {
        const double efficiency = 0.9, drag = 0.6, resistance = 0.3;
        var random = new Random(3);
        var samples = new List<ModelSample>();
        for (var i = 0; i < 2000; i++)
        {
            var kmh = 130 + random.Next(190);
            var mguk = random.Next(3) * 150_000;
            var fuel = 5 + random.Next(80);
            var v = kmh / 3.6;
            var mass = ErsModelBuilder.CarMass(GameFormat.F1_26) + fuel;
            var a = efficiency * (410_000 + mguk) / (mass * v) - drag * v * v / mass - resistance + (random.NextDouble() - 0.5) * 0.2;
            samples.Add(new ModelSample(1, 1, i, 0, kmh, 1, 0, 0.01f, 2_000_000, 1, 0, 0, 410_000, mguk, (float)(a / 9.81), fuel, 1));
        }

        var fit = Assert.Single(ErsModelBuilder.FitStraightLine(GameFormat.F1_26, samples));

        Assert.Equal(1, fit.Aero);
        Assert.Equal(efficiency, fit.Efficiency, 0.02);
        Assert.Equal(drag, fit.Drag, 0.02);
        Assert.Equal(resistance, fit.Resistance, 0.05);
        Assert.True(fit.RSquared > 0.95);
        Assert.Equal(2 * drag / 1.225, fit.DragArea, 0.05);
    }

    [Fact]
    public void Energy_along_the_lap_averages_whole_laps_and_skips_counter_resets()
    {
        var samples = new List<ModelSample>();
        for (var lap = 1; lap <= 2; lap++)
        {
            float harvested = 7_000_000; // the previous lap's total, until the counter resets
            float deployed = 6_000_000;
            for (var d = 0; d < 1000; d += 10)
            {
                if (d == 20)
                {
                    harvested = 0;
                    deployed = 0;
                }

                if (d is >= 300 and < 400)
                {
                    harvested += 10_000 * lap; // 100 kJ on lap 1, 200 kJ on lap 2, all in the 300 m and 350 m bins
                }

                if (d >= 500)
                {
                    deployed += 5_000;
                }

                samples.Add(new ModelSample(1, lap, lap * 1000 + d / 10.0, d, 200, 1, 0, 0, 2_000_000, 1, harvested, deployed, 0, 0, 0, 10, 0));
            }
        }

        samples.Add(new ModelSample(1, 3, 5000, 0, 200, 1, 0, 0, 2_000_000, 1, 0, 0, 0, 0, 0, 10, 0)); // a lap that barely started

        var bins = ErsModelBuilder.AlongLap(samples);
        Assert.All(bins, b => Assert.Equal(2, b.Laps));
        Assert.Equal(150_000, bins.Sum(b => b.HarvestedJ), 0);
        Assert.Equal(75_000, bins.Single(b => b.From == 300).HarvestedJ, 0);
        Assert.Equal(0, bins.Single(b => b.From == 0).HarvestedJ); // the reset is not a harvest
        Assert.Equal(250_000, bins.Sum(b => b.DeployedJ), 0);
    }

    [Fact]
    public void Deploying_above_the_fade_speed_is_flagged()
    {
        var map = new DeployMap([new DeployCurve(DeployModes.Overtake, [new DeployPoint(260, 315_000, 50), new DeployPoint(270, 135_000, 50)], 270)]);
        var lap = new List<TelemetrySample>();
        for (var i = 0; i < 40; i++)
        {
            lap.Add(new TelemetrySample
            {
                LapNumber = 2, SessionTime = 10 + i * 0.1, LapDistance = i * 8, Throttle = 1, ErsDeployMode = DeployModes.Overtake,
                Speed = i < 15 ? 260 : 285, EnginePowerMguk = i < 15 ? 315_000 : 135_000, ErsStoreEnergy = 2_000_000,
            });
        }

        var fade = Assert.Single(EnergyAnalyzer.Analyze(2, lap, map)!.Issues);
        Assert.Equal(EnergyWaste.Fade, fade.Kind);
        Assert.Equal(2.4, fade.Seconds, 1); // 25 samples, the last one ends the lap
        Assert.Empty(EnergyAnalyzer.Analyze(2, lap)!.Issues); // no map, no fade
    }

    /// <summary>
    /// The game runs its own map under the mode: in None at 250 km/h it deploys 250 kW on the first half of this
    /// straight and nothing on the second, and clips (harvests 70 kW flat out) near its end. A speed-only curve can't
    /// tell them apart; the position map does. Clipping is only learned while the harvest limit has room.
    /// </summary>
    [Fact]
    public void Position_map_learns_what_none_does_at_each_point_of_the_lap()
    {
        const float limit = 6_000_000;
        var samples = new List<ModelSample>();
        for (var lap = 1; lap <= 3; lap++)
        {
            double t = lap * 100;
            float deployed = 0, harvested = lap == 3 ? limit : 0; // lap 3 reached the limit before this straight
            for (float d = 0; d < 1000; d += 3.5f)
            {
                t += 0.05;
                var output = d < 500 ? 250_000f : 0;
                var clip = d is >= 800 and < 900 && lap < 3 ? 70_000f : 0;
                (deployed, harvested) = (deployed + output * 1.1f * 0.05f, harvested + clip * 0.05f);
                samples.Add(new ModelSample(1, lap, t, d, 250, 1, 0, 0, 2_000_000, DeployModes.None, harvested, deployed, 410_000, output, 0, 10, 0,
                    HarvestLimit: limit));
            }
        }

        var map = ErsModelBuilder.BuildPositionMap(samples);
        var curve = ErsModelBuilder.BuildDeployMap(samples).Curve(DeployModes.None);

        Assert.True(map.Deploy(DeployModes.None, 5, 250, curve)!.Value.Output > 200_000);
        Assert.True(map.Deploy(DeployModes.None, 30, 250, curve)!.Value.Output < 60_000);
        Assert.Equal(275_000, map.Deploy(DeployModes.None, 5, 250, null)!.Value.Draw, 1_000.0); // the deploy counter: a tenth more
        Assert.Equal(70_000, map.Clip(DeployModes.None, 34)!.Value, 1_000.0);
        Assert.Equal(0, map.Clip(DeployModes.None, 10)!.Value);
        Assert.Null(map.Clip(DeployModes.Overtake, 34)); // never run there
        Assert.Null(map.Deploy(DeployModes.Overtake, 5, 250, null));
    }

    /// <summary>
    /// The share of a change of power that becomes acceleration, from samples at the same place and speed: here half
    /// of it at 150 km/h (traction) and all of it above 200 km/h, whatever each place's own grip and drag add.
    /// </summary>
    [Fact]
    public void Traction_is_learned_from_samples_at_the_same_place_and_speed()
    {
        var mass = ErsModelBuilder.CarMass(GameFormat.F1_26) + 10;
        var samples = new List<ModelSample>();
        double t = 0;
        foreach (var (kmh, share) in new[] { (150f, 0.5), (250f, 1.0) })
        {
            for (var d = 0; d < 3000; d += 10)
            {
                var place = Math.Sin(d / 300.0) * 3; // each place's own grip, drag and slope
                foreach (var output in new[] { 100_000f, 300_000f, 200_000f })
                {
                    t += 0.05;
                    var a = share * output / (mass * kmh / 3.6) + place;
                    samples.Add(new ModelSample(1, 1, t, d, kmh, 1, 0, 0, 2_000_000, DeployModes.Overtake, 0, 0, 410_000, output, (float)(a / 9.81), 10, 0));
                }
            }
        }

        var traction = ErsModelBuilder.FitTraction(GameFormat.F1_26, samples);

        Assert.Equal(0.5, traction.Factor(155, 0), 0.02);
        Assert.Equal(1.0, traction.Factor(255, 0), 0.02);
        Assert.Equal(1.0, traction.Factor(80, 0)); // no samples that slow: all of it
        Assert.Equal(1.0, TractionMap.Unlimited.Factor(120, 2));
    }
}
