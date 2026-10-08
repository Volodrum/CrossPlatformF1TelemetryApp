using F1Telemetry.Core.Energy;
using F1Telemetry.Core.Models;

namespace F1Telemetry.Tests;

public class SessionLapsTests
{
    private static LapRecord Lap(int number, uint ms, LapType type = LapType.Regular) => new() { LapNumber = number, LapTimeMs = ms, LapType = type };

    [Fact]
    public void Typical_laps_leave_out_pit_safety_car_untimed_and_slow_laps()
    {
        LapRecord[] laps =
        [
            Lap(1, 98_000),                         // standing start: 6% off the median (92.25 s)
            Lap(2, 92_100), Lap(3, 92_400), Lap(4, 91_900),
            Lap(5, 99_500, LapType.Pit),
            Lap(6, 92_000),
            Lap(7, 120_000, LapType.SafetyCar),
            Lap(8, 94_600),                         // a mistake: 2.5% off, still typical
            Lap(9, 0),                              // not timed
        ];

        var typical = SessionLaps.Representative(laps);

        Assert.Equal([2, 3, 4, 6, 8], typical.Select(l => l.LapNumber));
        Assert.Equal(4, SessionLaps.Fastest(laps)!.LapNumber);
        Assert.Empty(SessionLaps.Representative([Lap(1, 0), Lap(2, 90_000, LapType.Pit)]));
        Assert.Null(SessionLaps.Fastest([Lap(1, 0)]));
    }

    [Fact]
    public void The_typical_lap_averages_the_battery_and_takes_the_mode_most_laps_ran()
    {
        // Three laps of 100 m: the battery falls 1 MJ from a different start each lap; laps 1 and 2 run Overtake on the
        // second half, lap 3 Medium. Lap 4 is not asked for.
        var samples = new List<TelemetrySample>();
        foreach (var (lap, start, mode) in new[] { (1, 3e6, DeployModes.Overtake), (2, 2e6, DeployModes.Overtake), (3, 1e6, DeployModes.Medium), (4, 0.0, DeployModes.None) })
        {
            for (var d = 0; d <= 100; d += 5)
            {
                samples.Add(new TelemetrySample
                {
                    LapNumber = lap, LapDistance = d, ErsStoreEnergy = start - d * 10_000,
                    ErsDeployMode = d < 50 ? DeployModes.None : mode,
                });
            }
        }

        var typical = SessionLaps.Typical(samples, [1, 2, 3])!;

        Assert.Equal(3, typical.LapCount);
        Assert.Equal(5, typical.Level.Length); // 0, 25, 50, 75, 100 m
        Assert.Equal([2e6, 1.75e6, 1.5e6, 1.25e6, 1e6], typical.Level);
        Assert.Equal([DeployModes.None, DeployModes.None, DeployModes.Overtake, DeployModes.Overtake, DeployModes.Overtake], typical.Mode);
        Assert.Equal([new ModeRun(DeployModes.None, 0, 50), new ModeRun(DeployModes.Overtake, 50, 125)], typical.Runs());
        Assert.Equal(3, typical.IndexAt(80));
        Assert.Null(SessionLaps.Typical(samples, [9]));
    }
}
