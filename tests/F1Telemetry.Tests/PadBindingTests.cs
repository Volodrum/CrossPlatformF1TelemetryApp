using F1Telemetry.Core.Input;

namespace F1Telemetry.Tests;

public sealed class PadBindingTests
{
    [Theory]
    [InlineData("South", null, PadButton.South)]
    [InlineData("Touchpad+DPadRight", PadButton.Touchpad, PadButton.DPadRight)]
    [InlineData(" misc1 + north ", PadButton.Misc1, PadButton.North)]
    public void Bindings_parse_and_round_trip(string text, PadButton? modifier, PadButton button)
    {
        var binding = PadBinding.TryParse(text);

        Assert.Equal(new PadBinding(modifier, button), binding);
        Assert.Equal(binding, PadBinding.TryParse(binding!.ToString()));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Cross")]
    [InlineData("South+South")]
    [InlineData("South+East+West")]
    public void Invalid_bindings_are_unbound(string? text) => Assert.Null(PadBinding.TryParse(text));

    [Fact]
    public void Names_follow_the_controller()
    {
        var binding = new PadBinding(PadButton.Touchpad, PadButton.South);

        Assert.Equal("Touchpad + Cross", binding.Describe(PadFamily.DualSense));
        Assert.Equal("Touchpad + A", binding.Describe(PadFamily.Xbox));
        Assert.Equal("Mic", PadBinding.Name(PadButton.Misc1, PadFamily.DualSense));
        Assert.Equal("Create", PadBinding.Name(PadButton.Back, PadFamily.DualSense));
        Assert.Equal("R1", PadBinding.Name(PadButton.RightShoulder, PadFamily.PlayStation));
    }

    [Fact]
    public void A_single_button_fires_on_press()
    {
        var matcher = new PadBindingMatcher { Bindings = [null, null, new PadBinding(null, PadButton.Misc1)] };
        var fired = new List<int>();

        matcher.Down(PadButton.South, fired);
        matcher.Up(PadButton.South, fired);
        Assert.Empty(fired);

        matcher.Down(PadButton.Misc1, fired);
        Assert.Equal([2], fired);
        matcher.Up(PadButton.Misc1, fired);
        Assert.Equal([2], fired);
    }

    [Fact]
    public void A_combo_fires_only_with_its_modifier_held()
    {
        var matcher = new PadBindingMatcher { Bindings = [new PadBinding(PadButton.Touchpad, PadButton.DPadRight)] };
        var fired = new List<int>();

        matcher.Down(PadButton.DPadRight, fired);
        matcher.Up(PadButton.DPadRight, fired);
        Assert.Empty(fired);

        matcher.Down(PadButton.Touchpad, fired);
        matcher.Down(PadButton.DPadRight, fired);
        Assert.Equal([0], fired);
        matcher.Up(PadButton.DPadRight, fired);
        matcher.Up(PadButton.Touchpad, fired);
        Assert.Equal([0], fired);
    }

    [Fact]
    public void A_modifier_with_its_own_binding_fires_on_release_unless_used_for_a_combo()
    {
        var matcher = new PadBindingMatcher
        {
            Bindings = [new PadBinding(null, PadButton.Touchpad), new PadBinding(PadButton.Touchpad, PadButton.North)],
        };
        var fired = new List<int>();

        // Combo: only the combo's action.
        matcher.Down(PadButton.Touchpad, fired);
        matcher.Down(PadButton.North, fired);
        matcher.Up(PadButton.North, fired);
        matcher.Up(PadButton.Touchpad, fired);
        Assert.Equal([1], fired);

        // Tap: the single action, on release.
        fired.Clear();
        matcher.Down(PadButton.Touchpad, fired);
        Assert.Empty(fired);
        matcher.Up(PadButton.Touchpad, fired);
        Assert.Equal([0], fired);
    }

    [Fact]
    public void Capture_takes_a_single_button_on_release()
    {
        var matcher = new PadBindingMatcher { Bindings = [new PadBinding(null, PadButton.Misc1)] };
        var fired = new List<int>();
        matcher.BeginCapture();

        Assert.Null(matcher.Down(PadButton.Misc1, fired));
        Assert.Equal(new PadBinding(null, PadButton.Misc1), matcher.Up(PadButton.Misc1, fired));
        Assert.False(matcher.Capturing);
        Assert.Empty(fired);
    }

    [Fact]
    public void Capture_takes_a_combo_when_a_second_button_goes_down()
    {
        var matcher = new PadBindingMatcher();
        var fired = new List<int>();
        matcher.BeginCapture();

        Assert.Null(matcher.Down(PadButton.Touchpad, fired));
        Assert.Equal(new PadBinding(PadButton.Touchpad, PadButton.DPadLeft), matcher.Down(PadButton.DPadLeft, fired));
        Assert.Null(matcher.Up(PadButton.DPadLeft, fired));
        Assert.Null(matcher.Up(PadButton.Touchpad, fired));
        Assert.False(matcher.Capturing);
    }
}
