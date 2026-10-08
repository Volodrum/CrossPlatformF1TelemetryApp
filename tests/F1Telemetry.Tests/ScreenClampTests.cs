using F1Telemetry.Core.Layout;

namespace F1Telemetry.Tests;

public class ScreenClampTests
{
    // 1920×1080 screen at the origin. Window 400×200.
    private static (int X, int Y) Clamp(int x, int y) => ScreenClamp.Apply(x, y, 400, 200, 0, 0, 1920, 1080);

    [Fact]
    public void A_window_inside_the_screen_stays_put()
    {
        Assert.Equal((300, 500), Clamp(300, 500));
    }

    [Fact]
    public void A_window_past_the_left_and_top_edges_is_pushed_back_in()
    {
        Assert.Equal((0, 0), Clamp(-150, -40));
    }

    [Fact]
    public void A_window_past_the_right_and_bottom_edges_is_pushed_back_in()
    {
        Assert.Equal((1520, 880), Clamp(1700, 1000));
    }

    [Fact]
    public void Only_the_axis_that_sticks_out_moves()
    {
        Assert.Equal((1520, 300), Clamp(1600, 300));
    }

    [Fact]
    public void Uses_the_bounds_of_a_secondary_screen()
    {
        // Second monitor to the left: 2560×1440 at x = -2560, y = -200.
        Assert.Equal((-2560, -200), ScreenClamp.Apply(-2700, -300, 400, 200, -2560, -200, 2560, 1440));
        Assert.Equal((-400, 1040), ScreenClamp.Apply(100, 1100, 400, 200, -2560, -200, 2560, 1440));
    }

    [Fact]
    public void A_window_bigger_than_the_screen_keeps_its_top_left_on_screen()
    {
        Assert.Equal((0, 0), ScreenClamp.Apply(-50, 30, 2000, 1200, 0, 0, 1920, 1080));
    }
}
