using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using F1Telemetry.App.Platform;

namespace F1Telemetry.App.Views.Overlays;

/// <summary>
/// One edge of the screen going dark while a car is alongside on that side (as Gran Turismo does it). A borderless,
/// always-on-top window down the edge of the screen, click-through at all times, holding a gradient from dark at the
/// edge to clear. Its darkness follows <see cref="Amount"/>, eased so it doesn't flicker as the gap moves.
/// </summary>
public sealed class SideShadeWindow : Window
{
    /// <summary>Share of the screen's width the shade reaches in from its edge.</summary>
    public const double WidthShare = 0.24;

    /// <summary>Darkness at the very edge with a car wheel to wheel.</summary>
    public const double MaxDarkness = 0.78;

    private readonly bool _left;
    private readonly Border _shade;

    public SideShadeWindow(bool left)
    {
        _left = left;
        Title = $"HUD overlay – side shade ({(left ? "left" : "right")})";
        CanResize = false;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _shade = new Border
        {
            Opacity = 0,
            Background = Gradient(left),
            Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) }],
        };
        Content = _shade;
        Opened += (_, _) => OverlayInterop.SetClickThrough(this, true);
    }

    /// <summary>Dark at the screen's edge, fading to clear towards its middle.</summary>
    public static IBrush Gradient(bool left) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(left ? 0 : 1, 0.5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(left ? 1 : 0, 0.5, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb((byte)(255 * MaxDarkness), 0, 0, 0), 0),
            new GradientStop(Color.FromArgb((byte)(255 * MaxDarkness * 0.45), 0, 0, 0), 0.45),
            new GradientStop(Color.FromArgb(0, 0, 0, 0), 1),
        },
    };

    /// <summary>How dark, 0 (clear) to 1.</summary>
    public double Amount
    {
        get => _amount;
        set
        {
            // Small steps are left alone: re-setting the target restarts the ease, and the radar updates 20 times a second.
            var amount = Math.Clamp(value, 0, 1);
            if (Math.Abs(_amount - amount) > 0.02 || (amount == 0 && _amount != 0))
            {
                _amount = amount;
                _shade.Opacity = amount;
            }
        }
    }

    private double _amount;

    /// <summary>Covers its edge of <paramref name="screen"/> (physical pixels), the full height.</summary>
    public void PlaceOn(PixelRect screen, double scaling)
    {
        var width = (int)(screen.Width * WidthShare);
        Position = new PixelPoint(_left ? screen.X : screen.Right - width, screen.Y);
        Width = width / scaling;
        Height = screen.Height / scaling;
    }
}
