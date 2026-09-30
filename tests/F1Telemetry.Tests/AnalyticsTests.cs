using F1Telemetry.Core.Analytics;
using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.Tests;

public class AnalyticsTests
{
    private static LapRecord Lap(int n, uint ms, string compound = "Medium", int stint = 0, LapType type = LapType.Regular) =>
        new() { LapNumber = n, LapTimeMs = ms, Compound = compound, StintIndex = stint, LapType = type };

    [Fact]
    public void Race_marks_in_and_out_lap_around_stint_change()
    {
        var laps = new[] { Lap(1, 90_000), Lap(2, 90_100), Lap(3, 95_000), Lap(4, 97_000, "Hard", 1), Lap(5, 90_500, "Hard", 1) };
        var classified = LapClassifier.Classify(laps, sessionType: 15);
        Assert.Equal([LapType.Regular, LapType.Regular, LapType.Pit, LapType.Pit, LapType.Regular], classified.Select(l => l.LapType));
    }

    [Fact]
    public void Race_detects_same_compound_stop_via_stint_index()
    {
        var laps = new[] { Lap(1, 90_000), Lap(2, 96_000), Lap(3, 97_000, stint: 1), Lap(4, 90_000, stint: 1) };
        var classified = LapClassifier.Classify(laps, 15);
        Assert.Equal(LapType.Pit, classified[1].LapType);
        Assert.Equal(LapType.Pit, classified[2].LapType);
    }

    [Fact]
    public void Non_race_laps_without_time_are_pit_laps()
    {
        var laps = new[] { Lap(1, 0, "Unknown", -1), Lap(2, 80_000), Lap(3, 0) };
        var classified = LapClassifier.Classify(laps, sessionType: 5);
        Assert.Equal([LapType.Pit, LapType.Regular, LapType.Pit], classified.Select(l => l.LapType));
        Assert.Equal("Medium", classified[0].Compound); // back-filled out-lap compound
    }

    [Fact]
    public void Linear_regression_slope()
    {
        Assert.Equal(2.0, LinearRegression.Slope([(1, 1), (2, 3), (3, 5)]), 9);
        Assert.Equal(0, LinearRegression.Slope([(1, 1)]));
        Assert.Equal(0, LinearRegression.Slope([(1, 1), (1, 2)]));
    }

    [Fact]
    public void Stint_analysis_computes_wear_fuel_and_degradation()
    {
        var laps = new[] { Lap(1, 90_000), Lap(2, 90_200), Lap(3, 90_400), Lap(4, 91_000, "Hard", 1, LapType.Pit), Lap(5, 89_000, "Hard", 1) };
        var aggregates = Enumerable.Range(1, 5).Select(n =>
        {
            double wearStart = n <= 3 ? (n - 1) * 2.0 : (n - 4) * 1.0;
            double wearEnd = wearStart + (n <= 3 ? 2.0 : 1.0);
            return new LapAggregate(n, 50 - n * 1.6, 50 - (n - 1) * 1.6, wearStart, wearEnd, wearStart, wearEnd + 1, wearStart, wearEnd, wearStart, wearEnd);
        }).ToList();

        var analysis = StintAnalyzer.Analyze(laps, aggregates, new WheelValues(1, 2, 1, 1), 15)!;
        Assert.Equal(2, analysis.Stints.Count);

        var first = analysis.Stints[0];
        Assert.Equal(3, first.RegularLapCount);
        Assert.Equal(1.6, first.AverageFuelPerLap, 6);
        Assert.Equal(2.0, first.AverageWearPerLap.Fl, 6);
        Assert.Equal(3.0, first.AverageWearPerLap.Fr, 6);
        Assert.Equal(0.2, first.PaceDegradationPerLap, 6); // +200 ms per lap
        Assert.Equal("Front Right", first.LimitingTyre!.Name);
        Assert.Equal(90_000u, first.BestLapMs);

        Assert.Equal("Hard", analysis.Stints[1].Compound);
        Assert.Equal(1, analysis.Stints[1].RegularLapCount);
    }

    [Fact]
    public void Delta_reports_sector_and_lap_deltas_against_personal_best()
    {
        var calc = new DeltaCalculator();
        var pb = new LapRecord { LapNumber = 1, LapTimeMs = 90_000, Sector1Ms = 30_000, Sector2Ms = 30_000, Sector3Ms = 30_000, IsBestLap = true };

        Assert.Null(calc.Update([pb, new LapRecord { LapNumber = 2 }]));

        var s1 = calc.Update([pb, new LapRecord { LapNumber = 2, Sector1Ms = 29_800 }])!;
        Assert.Equal((1, -200, DeltaColor.PersonalBest), (s1.SectorNumber, s1.LapDeltaMs, s1.LapColor));

        var s2 = calc.Update([pb, new LapRecord { LapNumber = 2, Sector1Ms = 29_800, Sector2Ms = 30_500 }])!;
        Assert.Equal((2, 300, 500, DeltaColor.Slower), (s2.SectorNumber, s2.LapDeltaMs, s2.SectorDeltaMs, s2.SectorColor));

        var finished = new LapRecord { LapNumber = 2, LapTimeMs = 89_900, Sector1Ms = 29_800, Sector2Ms = 30_500, Sector3Ms = 29_600, IsBestLap = true };
        var lap = calc.Update([pb with { IsBestLap = false }, finished, new LapRecord { LapNumber = 3 }])!;
        Assert.Equal((3, -100, DeltaColor.SessionBest), (lap.SectorNumber, lap.LapDeltaMs, lap.LapColor));
    }

    [Fact]
    public void Radar_flags_side_by_side_danger_and_ignores_inactive_cars()
    {
        var layout = FormatLayout.F1_25;
        var cars = new CarMotion[layout.MaxCars];
        cars[0] = new CarMotion(0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0); // facing +Z, right = +X
        cars[1] = cars[0] with { WorldPositionX = 2.0f, WorldPositionZ = 0.5f };      // alongside, right
        cars[2] = cars[0] with { WorldPositionZ = 15f };                               // ahead
        cars[3] = cars[0] with { WorldPositionX = -2f };                               // inactive
        var header = new PacketHeader(2025, 25, 1, 0, 1, PacketId.Motion, 1, 0, 0, 0, 0, 255);
        var laps = new LapData[layout.MaxCars];
        laps[1] = laps[1] with { ResultStatus = ResultStatus.Active };
        laps[2] = laps[2] with { ResultStatus = ResultStatus.Active };
        laps[3] = laps[3] with { ResultStatus = ResultStatus.Inactive };

        var frame = RadarCalculator.Compute(new MotionPacket(header, cars), new LapDataPacket(header with { PacketId = PacketId.LapData }, laps, 255, 255));

        Assert.Equal(2, frame.Cars.Count);
        Assert.True(frame.RightDanger);
        Assert.False(frame.LeftDanger);
        var ahead = frame.Cars.Single(c => c.CarIndex == 2);
        Assert.Equal(15f, ahead.RelativeZ, 3);
        Assert.Equal(RadarSeverity.Far, ahead.Severity);
    }

    [Fact]
    public void Consumption_tracker_averages_clean_laps_and_resets_on_tyre_change()
    {
        var tracker = new ConsumptionTracker(new StrategyOptions());
        CarStatus Status(float fuel) => default(CarStatus) with { FuelInTank = fuel };
        CarDamage Damage(float w) => default(CarDamage) with { TyresWear = new(w, w, w, w + 1) };

        tracker.EnsureStarted(Status(20), Damage(0));
        tracker.OnLapCompleted("Medium", true, Status(18.2f), Damage(2));
        tracker.OnLapCompleted("Medium", false, Status(16.0f), Damage(5)); // pit lap: ignored
        tracker.OnLapCompleted("Medium", true, Status(14.4f), Damage(7));

        Assert.Equal(1.7, tracker.AverageFuelPerLap("Medium")!.Value, 3);
        Assert.Equal(2.0, tracker.AverageWearPerLap("Medium")!.Value.Fr, 3);
        Assert.Equal(14.4 / 1.7, tracker.FuelLapsRemaining("Medium", 14.4)!.Value, 3);

        tracker.OnLapCompleted("Medium", true, Status(12.7f), Damage(0)); // fresh tyres
        Assert.Null(tracker.AverageWearPerLap("Medium"));
    }
}
