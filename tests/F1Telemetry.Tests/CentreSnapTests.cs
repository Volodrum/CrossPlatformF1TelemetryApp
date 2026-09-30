using F1Telemetry.Core.Layout;

namespace F1Telemetry.Tests;

public class CentreSnapTests
{
    // 1920×1080 screen at the origin: centre lines at x = 960, y = 540. Window 400×200.
    private static SnapResult Snap(int x, int y) => CentreSnap.Apply(x, y, 400, 200, 0, 0, 1920, 1080, threshold: 24);

    [Fact]
    public void Window_near_both_centre_lines_snaps_to_the_exact_centre()
    {
        var r = Snap(770, 450); // centre (970, 550): 10 px off each line

        Assert.Equal(new SnapResult(760, 440, true, true), r);
    }

    [Fact]
    public void Only_the_close_axis_snaps()
    {
        var r = Snap(775, 100); // centre x 975 (15 px off), y far away

        Assert.Equal(new SnapResult(760, 100, true, false), r);
    }

    [Fact]
    public void Beyond_the_threshold_the_window_is_released()
    {
        var r = Snap(785, 300); // centre x 985: 25 px off

        Assert.Equal(new SnapResult(785, 300, false, false), r);
    }

    [Fact]
    public void Uses_the_centre_of_a_secondary_screen()
    {
        // Second monitor to the right: 2560×1440 at x = 1920, centre (3200, 720).
        var r = CentreSnap.Apply(3005, 610, 400, 200, 1920, 0, 2560, 1440, threshold: 24);

        Assert.Equal(new SnapResult(3000, 620, true, true), r);
    }
}
