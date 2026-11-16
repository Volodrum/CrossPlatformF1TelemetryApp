using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using F1Telemetry.Core.Models;

namespace F1Telemetry.App.Controls;

/// <summary>
/// Top-down proximity radar: the player at the centre facing up, on a see-through disc so the road shows behind it.
/// <para>
/// Other cars are drawn to be caught from the corner of the eye rather than read: a solid block turned the way the car
/// faces, that grows and changes colour as it closes in. Translucent white = further away, amber =
/// near, red with a glow = alongside. The rim lights up red on the side a car is beside you.
/// </para>
/// </summary>
public sealed class RadarControl : Control
{
    public static readonly StyledProperty<RadarFrame?> FrameProperty =
        AvaloniaProperty.Register<RadarControl, RadarFrame?>(nameof(Frame));

    // The disc is see-through: the road behind it stays visible without fading the cars on it.
    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#101216"), 0.55);
    private static readonly IPen OuterRing = new Pen(new SolidColorBrush(Color.Parse("#3A404C"), 0.9), 3);
    private static readonly IPen InnerRing = new Pen(new SolidColorBrush(Color.Parse("#3A404C"), 0.6), 2);
    private static readonly IPen CarOutline = new Pen(new SolidColorBrush(Color.Parse("#07080A")), 2.5, lineJoin: PenLineJoin.Round);
    private static readonly IBrush PlayerBrush = new SolidColorBrush(Color.Parse("#F4F6F9"));
    private static readonly IBrush ArrowBrush = new SolidColorBrush(Color.Parse("#07080A"));
    private static readonly IBrush FarBrush = new SolidColorBrush(Color.Parse("#F4F6F9"), 0.75);
    private static readonly IBrush NearBrush = new SolidColorBrush(Color.Parse("#FFC23D"));
    private static readonly IBrush DangerBrush = new SolidColorBrush(Color.Parse("#FF5A4F"));
    private static readonly IBrush GlowOuter = new SolidColorBrush(Color.Parse("#FF5A4F"), 0.22);
    private static readonly IBrush GlowInner = new SolidColorBrush(Color.Parse("#FF5A4F"), 0.40);
    private static readonly IPen DangerRim = new Pen(DangerBrush, 9, lineCap: PenLineCap.Round);

    static RadarControl() => AffectsRender<RadarControl>(FrameProperty);

    public RadarFrame? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    /// <summary>
    /// Metres from the centre to the rim. Closer in than the 25 m the radar looks at, so the cars that matter are drawn
    /// big; one further out shows only as it reaches the rim.
    /// </summary>
    public const float DisplayRange = 16f;

    /// <summary>Diameter used when the parent doesn't constrain the size (e.g. a size-to-content overlay window).</summary>
    public const double DefaultDiameter = 240;

    /// <summary>
    /// Always square: the largest circle that fits, or <see cref="DefaultDiameter"/> when unconstrained. Without
    /// this the radar measured as 0×0 in the size-to-content overlay window and collapsed to a sliver.
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
        var radius = size / 2 - 6; // room for the rim's glow
        context.DrawEllipse(Background, OuterRing, centre, radius, radius);
        context.DrawEllipse(null, InnerRing, centre, radius * 0.66, radius * 0.66);
        context.DrawEllipse(null, InnerRing, centre, radius * 0.33, radius * 0.33);

        var frame = Frame;
        var scale = radius / DisplayRange;
        // Close to true-to-scale widths so side-by-side cars hardly overlap; length slightly exaggerated for legibility.
        var carW = Math.Max(12, 1.9 * scale);
        var carL = Math.Max(30, 5.4 * scale * 1.1);

        if (frame is not null)
        {
            if (frame.LeftDanger)
            {
                DrawRim(context, centre, radius, 180);
            }

            if (frame.RightDanger)
            {
                DrawRim(context, centre, radius, 0);
            }

            // Furthest first, so the closest car is drawn on top; a car at the edge is cut by the rim, not drawn over it.
            using var clip = context.PushGeometryClip(new EllipseGeometry(new Rect(centre.X - radius, centre.Y - radius, 2 * radius, 2 * radius)));
            foreach (var car in frame.Cars.OrderBy(c => c.Severity))
            {
                var (brush, grow) = car.Severity switch
                {
                    RadarSeverity.Danger => (DangerBrush, 1.2),
                    RadarSeverity.Near => (NearBrush, 1.1),
                    _ => (FarBrush, 1.0),
                };
                var position = new Point(centre.X + car.RelativeX * scale, centre.Y - car.RelativeZ * scale);
                using (context.PushTransform(Matrix.CreateRotation(car.RelativeYawDegrees * Math.PI / 180) * Matrix.CreateTranslation(position.X, position.Y)))
                {
                    var w = carW * grow;
                    var l = carL * grow;
                    if (car.Severity == RadarSeverity.Danger)
                    {
                        context.DrawRectangle(GlowOuter, null, new Rect(-w / 2 - 9, -l / 2 - 9, w + 18, l + 18), CarCorner + 9, CarCorner + 9);
                        context.DrawRectangle(GlowInner, null, new Rect(-w / 2 - 4, -l / 2 - 4, w + 8, l + 8), CarCorner + 4, CarCorner + 4);
                    }

                    context.DrawRectangle(brush, CarOutline, new Rect(-w / 2, -l / 2, w, l), CarCorner, CarCorner);
                }
            }
        }

        // The player: the same block in white, with a dark arrow at its front so it never reads as another car.
        context.DrawRectangle(PlayerBrush, CarOutline, new Rect(centre.X - carW / 2, centre.Y - carL / 2, carW, carL), CarCorner, CarCorner);
        var arrow = new StreamGeometry();
        using (var g = arrow.Open())
        {
            g.BeginFigure(new Point(centre.X, centre.Y - carL / 2 + 5), true);
            g.LineTo(new Point(centre.X + carW / 2 - 2.5, centre.Y - carL / 2 + 5 + carW * 0.7));
            g.LineTo(new Point(centre.X - carW / 2 + 2.5, centre.Y - carL / 2 + 5 + carW * 0.7));
            g.EndFigure(true);
        }

        context.DrawGeometry(ArrowBrush, null, arrow);
    }

    /// <summary>A thick red arc on the rim, centred on <paramref name="degrees"/> (0 = right, 180 = left).</summary>
    private static void DrawRim(DrawingContext context, Point centre, double radius, double degrees)
    {
        const double half = 38;
        var from = (degrees - half) * Math.PI / 180;
        var to = (degrees + half) * Math.PI / 180;
        var arc = new StreamGeometry();
        using (var g = arc.Open())
        {
            g.BeginFigure(new Point(centre.X + radius * Math.Cos(from), centre.Y + radius * Math.Sin(from)), false);
            g.ArcTo(new Point(centre.X + radius * Math.Cos(to), centre.Y + radius * Math.Sin(to)), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            g.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(GlowOuter, 22, lineCap: PenLineCap.Round), arc);
        context.DrawGeometry(null, DangerRim, arc);
    }

    /// <summary>Corner radius of a car block: rounded enough to read as a car, square enough to read as a box.</summary>
    private const double CarCorner = 4;
}
