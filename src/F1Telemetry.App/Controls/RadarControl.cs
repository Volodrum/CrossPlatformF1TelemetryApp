using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Models;

namespace F1Telemetry.App.Controls;

/// <summary>
/// Proximity radar, in one of two styles (<see cref="RadarStyle"/>).
/// <para>
/// <see cref="Core.Models.RadarStyle.Spotter"/>: nothing is drawn between two side lanes, so the road stays visible. A
/// lane appears when a car is on that side; the car is a block that moves along it (above the two ticks: ahead of you,
/// between them: beside you). Amber = coming alongside, red with a glow = overlapping, don't turn in. A strip at the
/// bottom shows a car in line behind. Made to be read out of the corner of the eye: a colour at the side, not a picture
/// to look at.
/// </para>
/// <para>
/// <see cref="Core.Models.RadarStyle.Disc"/>: top-down, player at the centre facing up. Other cars are solid,
/// dark-outlined blocks: red = danger (alongside), amber = near, translucent white = far; a red side bar flags
/// side-by-side.
/// </para>
/// </summary>
public sealed class RadarControl : Control
{
    public static readonly StyledProperty<RadarFrame?> FrameProperty =
        AvaloniaProperty.Register<RadarControl, RadarFrame?>(nameof(Frame));

    public static readonly StyledProperty<RadarStyle> RadarStyleProperty =
        AvaloniaProperty.Register<RadarControl, RadarStyle>(nameof(RadarStyle));

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#101216"));
    private static readonly IPen OuterRing = new Pen(new SolidColorBrush(Color.Parse("#2C313B")), 3);
    private static readonly IPen InnerRing = new Pen(new SolidColorBrush(Color.Parse("#2C313B")), 2);
    private static readonly IPen CarOutline = new Pen(new SolidColorBrush(Color.Parse("#07080A")), 2);
    private static readonly IBrush PlayerBrush = new SolidColorBrush(Color.Parse("#F4F6F9"));
    private static readonly IBrush ArrowBrush = new SolidColorBrush(Color.Parse("#07080A"));
    private static readonly IBrush FarBrush = new SolidColorBrush(Color.Parse("#F4F6F9"), 0.55);
    private static readonly IBrush NearBrush = new SolidColorBrush(Color.Parse("#FFC23D"));
    private static readonly IBrush DangerBrush = new SolidColorBrush(Color.Parse("#FF5A4F"));

    // Spotter bars.
    private static readonly IBrush LaneBrush = new SolidColorBrush(Color.Parse("#07080A"), 0.72);
    private static readonly IPen LaneOutline = new Pen(new SolidColorBrush(Color.Parse("#07080A")), 2);
    private static readonly IBrush NearTint = new SolidColorBrush(Color.Parse("#FFC23D"), 0.22);
    private static readonly IBrush AlertTint = new SolidColorBrush(Color.Parse("#FF5A4F"), 0.42);
    private static readonly IPen AlertOutline = new Pen(DangerBrush, 3);
    private static readonly IBrush GlowOuter = new SolidColorBrush(Color.Parse("#FF5A4F"), 0.18);
    private static readonly IBrush GlowInner = new SolidColorBrush(Color.Parse("#FF5A4F"), 0.34);
    private static readonly IBrush OwnCarBrush = new SolidColorBrush(Color.Parse("#F4F6F9"), 0.10);
    private static readonly IPen OwnCarTick = new Pen(new SolidColorBrush(Color.Parse("#F4F6F9"), 0.9), 2);

    private const double LaneWidth = 30;
    private const double LaneInset = 12; // room for the glow around a lane
    private const double RearWidth = 190;
    private const double RearHeight = 14;

    static RadarControl()
    {
        AffectsRender<RadarControl>(FrameProperty, RadarStyleProperty);
        AffectsMeasure<RadarControl>(RadarStyleProperty);
    }

    public RadarFrame? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    public RadarStyle RadarStyle
    {
        get => GetValue(RadarStyleProperty);
        set => SetValue(RadarStyleProperty, value);
    }

    /// <summary>Diameter of the disc when the parent doesn't constrain the size (e.g. a size-to-content overlay window).</summary>
    public const double DefaultDiameter = 240;

    /// <summary>
    /// Size a style is drawn at when unconstrained. The spotter is wide and hollow: its lanes sit well to either side of
    /// where the driver looks, with the road showing between them.
    /// </summary>
    public static Size DesignSize(RadarStyle style) => style == RadarStyle.Spotter ? new Size(560, 216) : new Size(DefaultDiameter, DefaultDiameter);

    /// <summary>
    /// The style's design size, or the largest box of its proportions that fits. Without this the radar measured as 0×0
    /// in the size-to-content overlay window and collapsed to a sliver.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var design = DesignSize(RadarStyle);
        var fit = Math.Min(
            double.IsInfinity(availableSize.Width) ? 1 : availableSize.Width / design.Width,
            double.IsInfinity(availableSize.Height) ? 1 : availableSize.Height / design.Height);
        return double.IsInfinity(availableSize.Width) && double.IsInfinity(availableSize.Height) ? design : design * fit;
    }

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        if (RadarStyle == RadarStyle.Spotter)
        {
            RenderSpotter(context);
        }
        else
        {
            RenderDisc(context);
        }
    }

    private void RenderSpotter(DrawingContext context)
    {
        var marks = Spotter.Marks(Frame);
        if (marks.Count == 0)
        {
            return; // nobody to call: nothing on screen at all
        }

        var design = DesignSize(RadarStyle.Spotter);
        var fit = Math.Min(Bounds.Width / design.Width, Bounds.Height / design.Height);
        var origin = new Point((Bounds.Width - design.Width * fit) / 2, (Bounds.Height - design.Height * fit) / 2);
        using var transform = context.PushTransform(Matrix.CreateScale(fit, fit) * Matrix.CreateTranslation(origin.X, origin.Y));

        var laneTop = LaneInset;
        var laneHeight = design.Height - 2 * LaneInset;
        DrawLane(context, new Rect(LaneInset, laneTop, LaneWidth, laneHeight), marks, SpotterZone.Left);
        DrawLane(context, new Rect(design.Width - LaneInset - LaneWidth, laneTop, LaneWidth, laneHeight), marks, SpotterZone.Right);
        DrawRear(context, new Rect((design.Width - RearWidth) / 2, design.Height - LaneInset - RearHeight, RearWidth, RearHeight), marks);
    }

    /// <summary>
    /// One side lane: <see cref="Spotter.LaneRange"/> m ahead at its top to as far behind at its bottom, the player's own
    /// car between the two ticks at mid-height.
    /// </summary>
    private void DrawLane(DrawingContext context, Rect lane, IReadOnlyList<SpotterMark> marks, SpotterZone zone)
    {
        var occupied = false;
        var alert = false;
        var closest = double.MaxValue;
        foreach (var mark in marks)
        {
            if (mark.Zone == zone)
            {
                occupied = true;
                alert |= mark.Alert;
                closest = Math.Min(closest, mark.Gap);
            }
        }

        if (!occupied)
        {
            return; // an empty side shows nothing: the road stays clear
        }

        if (alert)
        {
            // The closer the car, the wider the glow: size is what the edge of vision picks up.
            var near = Math.Clamp((Spotter.AlongsideLateral - closest) / (Spotter.AlongsideLateral - 2.0), 0, 1);
            var spread = 5 + 6 * near;
            context.DrawRectangle(GlowOuter, null, lane.Inflate(spread), 10 + spread, 10 + spread);
            context.DrawRectangle(GlowInner, null, lane.Inflate(spread / 2), 10 + spread / 2, 10 + spread / 2);
        }

        // The whole lane takes the colour, so the side reads amber or red before the eye finds the block.
        context.DrawRectangle(LaneBrush, null, lane, 10, 10);
        context.DrawRectangle(alert ? AlertTint : NearTint, alert ? AlertOutline : LaneOutline, lane, 10, 10);

        var perMetre = lane.Height / (2 * Spotter.LaneRange);
        var centre = lane.Y + lane.Height / 2;
        var half = Spotter.CarLength / 2 * perMetre;
        using (context.PushClip(new RoundedRect(lane.Deflate(1), 9)))
        {
            context.DrawRectangle(OwnCarBrush, null, new Rect(lane.X, centre - half, lane.Width, 2 * half));
            foreach (var mark in marks)
            {
                if (mark.Zone != zone)
                {
                    continue;
                }

                var y = centre - mark.Offset * perMetre;
                context.DrawRectangle(mark.Alert ? DangerBrush : NearBrush, CarOutline, new Rect(lane.X + 4, y - half, lane.Width - 8, 2 * half), 6, 6);
            }

            context.DrawLine(OwnCarTick, new Point(lane.X, centre - half), new Point(lane.Right, centre - half));
            context.DrawLine(OwnCarTick, new Point(lane.X, centre + half), new Point(lane.Right, centre + half));
        }
    }

    /// <summary>The car in line behind: a block that slides to the side it is looking at.</summary>
    private void DrawRear(DrawingContext context, Rect strip, IReadOnlyList<SpotterMark> marks)
    {
        SpotterMark? behind = null;
        foreach (var mark in marks)
        {
            if (mark.Zone == SpotterZone.Behind && (behind is null || mark.Gap < behind.Value.Gap))
            {
                behind = mark;
            }
        }

        if (behind is not { } car)
        {
            return;
        }

        context.DrawRectangle(LaneBrush, null, strip, 7, 7);
        context.DrawRectangle(car.Alert ? AlertTint : NearTint, car.Alert ? AlertOutline : LaneOutline, strip, 7, 7);
        const double blockWidth = 70;
        var travel = (strip.Width - blockWidth) / 2 - 4;
        var x = strip.X + strip.Width / 2 + Math.Clamp(car.Offset / Spotter.InLine, -1, 1) * travel;
        context.DrawRectangle(car.Alert ? DangerBrush : NearBrush, CarOutline, new Rect(x - blockWidth / 2, strip.Y + 2, blockWidth, strip.Height - 4), 5, 5);
    }

    private void RenderDisc(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
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
