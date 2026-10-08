using F1Telemetry.Core.Energy;
using F1Telemetry.Core.Models;

namespace F1Telemetry.Tests;

public class EnergyAnalyzerTests
{
    private const double Limit = 7_100_000;

    /// <summary>A lap built up in 0.1 s steps of 10 m each.</summary>
    private sealed class LapBuilder
    {
        private double _time = 100;
        private double _distance;

        public List<TelemetrySample> Samples { get; } = [];
        public double Store { get; set; } = 2_000_000;
        public double Harvested { get; set; }
        public double Deployed { get; set; }

        public LapBuilder Drive(int steps, int mode, double throttle, double brake, double storeStep = 0, double harvestStep = 0, double deployStep = 0)
        {
            for (var i = 0; i < steps; i++)
            {
                Samples.Add(new TelemetrySample
                {
                    LapNumber = 3, SessionTime = _time, LapDistance = _distance, Throttle = throttle, Brake = brake, ErsDeployMode = mode,
                    ErsStoreEnergy = Store, ErsHarvestedMguk = Harvested, ErsDeployed = Deployed, ErsHarvestLimit = Limit,
                });
                _time += 0.1;
                _distance += 10;
                Store = Math.Clamp(Store + storeStep, 0, 4_000_000);
                Harvested += harvestStep;
                Deployed += deployStep;
            }

            return this;
        }
    }

    [Fact]
    public void Lap_energy_ignores_the_previous_laps_counters_and_finds_the_waste()
    {
        var lap = new LapBuilder { Harvested = Limit, Deployed = 6_500_000 }; // last lap's totals, not reset yet
        lap.Drive(2, DeployModes.Medium, 1, 0);
        lap.Harvested = 0;
        lap.Deployed = 0;
        lap.Drive(20, DeployModes.Overtake, 1, 0, storeStep: -100_000, deployStep: 100_000);  // 2.0 → 0.1 MJ, then 0
        lap.Drive(10, DeployModes.Overtake, 1, 0);                                            // 1 s flat
        lap.Drive(40, DeployModes.None, 0, 0.8, storeStep: 100_000, harvestStep: 100_000);    // 4.0 MJ, 4.0 harvested
        lap.Drive(10, DeployModes.None, 0, 0.8);                                              // 1 s braking while full
        lap.Store = 3_000_000;
        lap.Drive(31, DeployModes.Medium, 0, 0.8, harvestStep: 100_000);                      // up to the limit
        lap.Drive(15, DeployModes.Medium, 0, 0.8);                                            // 1.5 s braking at the limit, from 1130 m
        lap.Drive(5, DeployModes.Medium, 1, 0);

        var energy = EnergyAnalyzer.Analyze(3, lap.Samples)!;

        Assert.Equal(Limit, energy.Harvested, 0);
        Assert.Equal(2_000_000, energy.Deployed, 0);
        Assert.Equal(2_000_000, energy.StartStore);
        Assert.Equal(3_000_000, energy.EndStore);
        Assert.Equal(0, energy.MinStore);
        Assert.Equal(4_000_000, energy.MaxStore);
        Assert.Equal(Limit, energy.HarvestLimit);
        Assert.Equal(1130, energy.LimitReachedAt);

        Assert.Equal([EnergyWaste.Flat, EnergyWaste.Full, EnergyWaste.Capped], energy.Issues.Select(i => i.Kind));
        Assert.Equal(1.0, energy.Issues[0].Seconds, 1);
        Assert.Equal(1.0, energy.Issues[1].Seconds, 1);
        Assert.Equal(1.5, energy.Issues[2].Seconds, 1);

        Assert.Equal([DeployModes.Medium, DeployModes.Overtake, DeployModes.None, DeployModes.Medium], energy.Modes.Select(m => m.Mode));
        Assert.Equal(new ModeRun(DeployModes.Overtake, 20, 320), energy.Modes[1]);
    }

    [Fact]
    public void Short_waste_and_a_missing_limit_are_not_reported()
    {
        var lap = new LapBuilder { Store = 4_000_000 };
        lap.Drive(3, DeployModes.None, 0, 0.8);         // 0.3 s braking while full: below the threshold
        lap.Drive(20, DeployModes.Medium, 1, 0, storeStep: -50_000, deployStep: 50_000);
        foreach (var s in lap.Samples)
        {
            s.ErsHarvestLimit = 0; // 2025 format
        }

        var energy = EnergyAnalyzer.Analyze(3, lap.Samples)!;
        Assert.Empty(energy.Issues);
        Assert.Null(energy.LimitReachedAt);
        Assert.Equal(-950_000, energy.NetChange, 0); // 4.00 → 3.05 MJ
    }

    [Fact]
    public void Laps_are_grouped_and_samples_before_the_line_are_left_out_of_the_mode_runs()
    {
        var samples = new List<TelemetrySample>
        {
            new() { LapNumber = 0, SessionTime = 1 },
            new() { LapNumber = 1, SessionTime = 2, LapDistance = -40, ErsDeployMode = DeployModes.None },
            new() { LapNumber = 1, SessionTime = 3, LapDistance = 5, ErsDeployMode = DeployModes.Medium },
            new() { LapNumber = 1, SessionTime = 4, LapDistance = 900, ErsDeployMode = DeployModes.Medium },
            new() { LapNumber = 2, SessionTime = 5, LapDistance = 3, ErsDeployMode = DeployModes.Overtake },
            new() { LapNumber = 2, SessionTime = 6, LapDistance = 80, ErsDeployMode = DeployModes.Overtake },
        };

        var laps = EnergyAnalyzer.AnalyzeLaps(samples);
        Assert.Equal([1, 2], laps.Select(l => l.LapNumber));
        Assert.Equal([new ModeRun(DeployModes.Medium, 5, 900)], laps[0].Modes);
    }
}
