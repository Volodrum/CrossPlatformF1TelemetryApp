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
}
