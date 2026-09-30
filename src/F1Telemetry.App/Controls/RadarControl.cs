using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using F1Telemetry.Core.Models;

namespace F1Telemetry.App.Controls;

/// <summary>
/// Top-down proximity radar: player at the centre facing up. Other cars are solid, dark-outlined blocks:
/// red = danger (alongside), amber = near, translucent white = far; a red side bar flags side-by-side.
/// </summary>
public sealed class RadarControl : Control
{
    public static readonly StyledProperty<RadarFrame?> FrameProperty =
        AvaloniaProperty.Register<RadarControl, RadarFrame?>(nameof(Frame));

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#101216"));
    private static readonly IPen OuterRing = new Pen(new SolidColorBrush(Color.Parse("#2C313B")), 3);
    private static readonly IPen InnerRing = new Pen(new SolidColorBrush(Color.Parse("#2C313B")), 2);
    private static readonly IPen CarOutline = new Pen(new SolidColorBrush(Color.Parse("#07080A")), 2);
    private static readonly IBrush PlayerBrush = new SolidColorBrush(Color.Parse("#F4F6F9"));
    private static readonly IBrush ArrowBrush = new SolidColorBrush(Color.Parse("#07080A"));
    private static readonly IBrush FarBrush = new SolidColorBrush(Color.Parse("#F4F6F9"), 0.55);
    private static readonly IBrush NearBrush = new SolidColorBrush(Color.Parse("#FFC23D"));
    private static readonly IBrush DangerBrush = new SolidColorBrush(Color.Parse("#FF5A4F"));

    static RadarControl() => AffectsRender<RadarControl>(FrameProperty);

    public RadarFrame? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    /// <summary>Diameter used when the parent doesn't constrain the size (e.g. a size-to-content overlay window).</summary>
    public const double DefaultDiameter = 240;

    /// <summary>
    /// Always square: the largest circle that fits, or <see cref="DefaultDiameter"/> when unconstrained. Without
    /// this the radar measured as 0×0 in the size-to-content overlay window and collapsed to a sliver, leaving
    /// only minimum-size car blocks piled up in the centre.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var diameter = Math.Min(availableSize.Width, availableSize.Height);
        if (double.IsInfinity(diameter))
        {
            diameter = DefaultDiameter;
        }

        return new Size(diameter, diameter);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
        {
            return;
        }

        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = size / 2 - 2;
        context.DrawEllipse(Background, OuterRing, centre, radius, radius);
        context.DrawEllipse(null, InnerRing, centre, radius * 0.66, radius * 0.66);
        context.DrawEllipse(null, InnerRing, centre, radius * 0.33, radius * 0.33);

        var frame = Frame;
        var scale = radius / (frame?.Range ?? 25f);
        // True-to-scale widths so side-by-side cars never visually overlap; length slightly exaggerated for legibility.
        var carW = Math.Max(9, 1.9 * scale);
        var carL = Math.Max(26, 5.4 * scale * 1.1);
        var barW = Math.Max(10, size * 0.05);

        if (frame is not null)
        {
            if (frame.LeftDanger)
            {
                context.DrawRectangle(DangerBrush, null, new Rect(centre.X - radius * 0.62, centre.Y - radius * 0.36, barW, radius * 0.72), barW / 2, barW / 2);
            }

            if (frame.RightDanger)
            {
                context.DrawRectangle(DangerBrush, null, new Rect(centre.X + radius * 0.62 - barW, centre.Y - radius * 0.36, barW, radius * 0.72), barW / 2, barW / 2);
            }

            foreach (var car in frame.Cars)
            {
                var brush = car.Severity switch
                {
                    RadarSeverity.Danger => DangerBrush,
                    RadarSeverity.Near => NearBrush,
                    _ => FarBrush,
                };
                var position = new Point(centre.X + car.RelativeX * scale, centre.Y - car.RelativeZ * scale);
                using (context.PushTransform(Matrix.CreateRotation(car.RelativeYawDegrees * Math.PI / 180) * Matrix.CreateTranslation(position.X, position.Y)))
                {
                    context.DrawRectangle(brush, CarOutline, new Rect(-carW / 2, -carL / 2, carW, carL), 5, 5);
                }
            }
        }

        context.DrawRectangle(PlayerBrush, CarOutline, new Rect(centre.X - carW / 2, centre.Y - carL / 2, carW, carL), 5, 5);
        var arrow = new StreamGeometry();
        using (var g = arrow.Open())
        {
            g.BeginFigure(new Point(centre.X, centre.Y - carL / 2 + 6), true);
            g.LineTo(new Point(centre.X + 6, centre.Y - carL / 2 + 14));
            g.LineTo(new Point(centre.X - 6, centre.Y - carL / 2 + 14));
            g.EndFigure(true);
        }

        context.DrawGeometry(ArrowBrush, null, arrow);
    }
}
