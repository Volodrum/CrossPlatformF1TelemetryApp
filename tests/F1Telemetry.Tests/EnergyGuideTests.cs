using F1Telemetry.Core.Analytics;
using F1Telemetry.Core.Energy;
using F1Telemetry.Protocol;

namespace F1Telemetry.Tests;

/// <summary>The energy data guide on the made-up track of <see cref="ErsPlanTests"/>: Medium and Overtake mapped, Hotlap never run.</summary>
public class EnergyGuideTests
{
    private static LapProfile Lap(double store) => LapProfile.From(ErsPlanTests.DriveLap(store), GameFormat.F1_26)!;

    private static GuideItem Item(DataGuide guide, GuideKind kind, int? mode = null) => guide.Items.Single(i => i.Kind == kind && i.Mode == mode);

    [Fact]
    public void With_no_power_data_everything_is_to_do_and_the_first_run_is_recording_laps()
    {
        var empty = new ErsCarModel(GameFormat.F1_26, 99, 0, 0, DeployMap.Empty, [], []);

        var guide = EnergyGuideBuilder.Build(empty, null, null);

        Assert.All(guide.Items, i => Assert.Equal(GuideState.Missing, i.State));
        Assert.DoesNotContain(guide.Items, i => i.Kind == GuideKind.Replay); // nothing to replay
        Assert.Equal(GuideKind.PowerLaps, guide.Next!.Kind);
        Assert.Equal(0, guide.Done);
    }

    [Fact]
    public void A_mode_counts_where_its_curve_covers_the_lap_speeds()
    {
        var guide = EnergyGuideBuilder.Build(ErsPlanTests.Model, Lap(2_000_000), null);

        Assert.Equal(GuideState.Done, Item(guide, GuideKind.ModeSpeeds, DeployModes.Medium).State);
        Assert.Equal(GuideState.Done, Item(guide, GuideKind.ModeSpeeds, DeployModes.Overtake).State);
        var hotlap = Item(guide, GuideKind.ModeSpeeds, DeployModes.Hotlap);
        Assert.Equal(GuideState.Missing, hotlap.State);
        Assert.Contains("Hotlap", hotlap.Do);
    }

    [Fact]
    public void An_exact_model_replays_the_lap_and_is_trusted()
    {
        var guide = EnergyGuideBuilder.Build(ErsPlanTests.Model, Lap(2_000_000), null);

        var replay = Item(guide, GuideKind.Replay);
        Assert.Equal(GuideState.Done, replay.State);
        Assert.Equal(1, replay.Progress);
    }

    [Fact]
    public void The_race_run_counts_the_typical_laps_averaged()
    {
        var one = EnergyGuideBuilder.Build(ErsPlanTests.Model, Lap(2_000_000), null);
        var five = EnergyGuideBuilder.Build(ErsPlanTests.Model, LapProfile.Average([.. Enumerable.Range(0, 5).Select(_ => Lap(2_000_000))]), null);

        Assert.Equal(GuideState.Missing, Item(one, GuideKind.RaceRun).State);
        Assert.Equal(GuideState.Done, Item(five, GuideKind.RaceRun).State);
    }

    [Theory]
    [InlineData(4_000_000, GuideState.Done)]
    [InlineData(2_800_000, GuideState.Partial)]
    [InlineData(1_000_000, GuideState.Missing)]
    public void The_qualifying_lap_should_start_full(double store, GuideState expected)
    {
        var guide = EnergyGuideBuilder.Build(ErsPlanTests.Model, Lap(2_000_000), Lap(store));

        var item = Item(guide, GuideKind.FullBattery);
        Assert.Equal(expected, item.State);
        Assert.Equal(GuideSession.Qualifying, item.Session);
    }

    [Fact]
    public void A_single_aero_state_with_its_fit_is_measured()
    {
        var guide = EnergyGuideBuilder.Build(ErsPlanTests.Model, Lap(2_000_000), null);

        Assert.Equal(GuideState.Done, Item(guide, GuideKind.AeroStates).State);
    }
}
