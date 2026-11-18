using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using F1Telemetry.App.ViewModels;
using F1Telemetry.Core.Analytics;

namespace F1Telemetry.App.Controls;

/// <summary>
/// A small looping animation that shows what a data guide item asks for, so it is understood at a glance rather than
/// read: a car lapping a little circuit while lap tiles light up (a long run), two compounds in one session, the same tyre at two fuel
/// loads, a wear bar crossing the cliff as the lap time bends up, a stop against a car that stays out, a stop under the
/// safety car. Drawn in a 240 × 100 design space and scaled to fit.
/// <para>It moves only while <see cref="IsAnimating"/> is set (the next run, or a card under the pointer); otherwise it
/// holds a frame that tells the story on its own, so a page of them doesn't pull the eye.</para>
/// </summary>
public sealed class GuideIllustration : Control
{
    public static readonly StyledProperty<GuideKind> KindProperty =
        AvaloniaProperty.Register<GuideIllustration, GuideKind>(nameof(Kind));

    /// <summary>The compound the item is about (long runs); the others pick their own.</summary>
    public static readonly StyledProperty<string?> CompoundProperty =
        AvaloniaProperty.Register<GuideIllustration, string?>(nameof(Compound));

    public static readonly StyledProperty<bool> IsAnimatingProperty =
        AvaloniaProperty.Register<GuideIllustration, bool>(nameof(IsAnimating));

    /// <summary>Draws this point of the loop (0–1) and holds it: for screenshots.</summary>
    public static readonly StyledProperty<double?> PhaseProperty =
        AvaloniaProperty.Register<GuideIllustration, double?>(nameof(Phase));

    public const double DesignWidth = 240;
    public const double DesignHeight = 100;

    /// <summary>Seconds one loop of the animation takes.</summary>
    private const double LoopSeconds = 4.5;

    /// <summary>The frame held while not animating: late in the loop, when the picture is complete.</summary>
    private const double RestPhase = 0.9;

    private static readonly IPen TrackPen = new Pen(Palette.LineStrong, 3, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    private static readonly IPen ThinPen = new Pen(Palette.LineStrong, 2, lineCap: PenLineCap.Round);
    private static readonly IPen DashPen = new Pen(Palette.TextLo, 1.5, new DashStyle([3, 3], 0));
    private static readonly IPen CarOutline = new Pen(Palette.OnColor, 1.5);
    private static readonly IBrush Ghost = new SolidColorBrush(Color.Parse("#F4F6F9"), 0.35);
    private static readonly IBrush FuelBrush = Palette.Info;
    private static readonly Typeface LetterFace = new(new FontFamily("avares://F1Telemetry/Assets/Fonts#Chakra Petch"), FontStyle.Normal, FontWeight.Bold);

    private DispatcherTimer? _timer;
    private readonly DateTime _start = DateTime.UtcNow;

    static GuideIllustration()
    {
        AffectsRender<GuideIllustration>(KindProperty, CompoundProperty, IsAnimatingProperty, PhaseProperty);
    }

    public GuideKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public string? Compound { get => GetValue(CompoundProperty); set => SetValue(CompoundProperty, value); }
    public bool IsAnimating { get => GetValue(IsAnimatingProperty); set => SetValue(IsAnimatingProperty, value); }
    public double? Phase { get => GetValue(PhaseProperty); set => SetValue(PhaseProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? DesignWidth : availableSize.Width;
        return new Size(width, width * DesignHeight / DesignWidth);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer?.Stop();
        _timer = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsAnimatingProperty || change.Property == PhaseProperty)
        {
            UpdateTimer();
        }
    }

    /// <summary>About 30 frames a second while animating and on screen; nothing at all otherwise.</summary>
    private void UpdateTimer()
    {
        var run = IsAnimating && Phase is null && VisualRoot is not null;
        if (run && _timer is null)
        {
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) =>
            {
                if (IsEffectivelyVisible)
                {
                    InvalidateVisual();
                }
            });
            _timer.Start();
        }
        else if (!run && _timer is not null)
        {
            _timer.Stop();
            _timer = null;
        }
    }

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var t = Phase ?? (IsAnimating ? (DateTime.UtcNow - _start).TotalSeconds / LoopSeconds % 1 : RestPhase);
        var scale = Math.Min(Bounds.Width / DesignWidth, Bounds.Height / DesignHeight);
        var offset = new Vector((Bounds.Width - DesignWidth * scale) / 2, (Bounds.Height - DesignHeight * scale) / 2);
        using var _ = context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset));
        switch (Kind)
        {
            case GuideKind.LongRun:
                LongRun(context, t, Compound ?? "Medium");
                break;
            case GuideKind.TwoCompounds:
                TwoCompounds(context, t);
                break;
            case GuideKind.FuelLoads:
                FuelLoads(context, t);
                break;
            case GuideKind.Cliff:
                Cliff(context, t);
                break;
            case GuideKind.PitStop:
                PitStop(context, t, safetyCar: false);
                break;
            case GuideKind.SafetyCar:
                PitStop(context, t, safetyCar: true);
                break;
        }
    }

    /// <summary>
    /// A little circuit of straights and sharp corners (no ovals), the start line on its bottom straight; the car laps
    /// it counter-clockwise from the line.
    /// </summary>
    private static readonly Point[] Circuit =
    [
        new(60, 86), new(92, 86), new(102, 76), new(102, 48), new(82, 42), new(74, 28), new(74, 18), new(66, 10),
        new(28, 10), new(16, 22), new(16, 52), new(28, 62), new(20, 78), new(28, 86),
    ];

    private static readonly Geometry CircuitGeometry = Polyline(Circuit, closed: true);

    private static readonly double[] CircuitLengths = [.. Circuit.Select((point, i) => Distance(point, Circuit[(i + 1) % Circuit.Length]))];

    /// <summary>
    /// A car laps the circuit; each time it crosses the line the next of six equal lap tiles lights up, then the full run
    /// holds for a moment.
    /// </summary>
    private static void LongRun(DrawingContext context, double t, string compound)
    {
        const int laps = 6;
        const double lapShare = 0.14; // of the loop; the rest holds the full run
        var colour = Palette.Compound(compound);
        context.DrawGeometry(null, TrackPen, CircuitGeometry);
        context.DrawLine(ThinPen, new Point(60, 80), new Point(60, 92));
        Badge(context, new Point(50, 46), 12, compound);

        var done = Math.Min(laps, (int)(t / lapShare));
        var (at, heading) = AlongCircuit(done >= laps ? 0 : t / lapShare % 1);
        using (context.PushTransform(Matrix.CreateRotation(heading) * Matrix.CreateTranslation(at.X, at.Y)))
        {
            context.DrawRectangle(Palette.TextHi, CarOutline, new Rect(-8, -4.5, 16, 9), 2.5, 2.5);
        }

        for (var i = 0; i < laps; i++)
        {
            var tile = new Rect(118 + i * 19, 30, 15, 40);
            var lit = i < done;
            context.DrawRectangle(lit ? colour : null, new Pen(lit ? colour : Palette.LineStrong, 2), tile, 3, 3);
        }

        // All six the same height: one steady pace.
        if (done >= laps)
        {
            context.DrawLine(DashPen, new Point(114, 30), new Point(234, 30));
        }
    }

    /// <summary>Where a car <paramref name="fraction"/> of the way round the circuit is, and which way it points (radians).</summary>
    private static (Point At, double Heading) AlongCircuit(double fraction)
    {
        var left = fraction * CircuitLengths.Sum();
        for (var i = 0; i < Circuit.Length; i++)
        {
            var (from, to) = (Circuit[i], Circuit[(i + 1) % Circuit.Length]);
            if (left <= CircuitLengths[i] || i == Circuit.Length - 1)
            {
                var k = CircuitLengths[i] > 0 ? Math.Clamp(left / CircuitLengths[i], 0, 1) : 0;
                return (new Point(from.X + (to.X - from.X) * k, from.Y + (to.Y - from.Y) * k), Math.Atan2(to.Y - from.Y, to.X - from.X));
            }

            left -= CircuitLengths[i];
        }

        return (Circuit[0], 0);
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    private static StreamGeometry Polyline(IReadOnlyList<Point> points, bool closed)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(points[0], false);
        foreach (var point in points.Skip(1))
        {
            g.LineTo(point);
        }

        g.EndFigure(closed);
        return geometry;
    }

    /// <summary>One session (the dashed frame): a stint on one compound, a stop, a stint on another.</summary>
    private static void TwoCompounds(DrawingContext context, double t)
    {
        context.DrawRectangle(null, DashPen, new Rect(6, 18, 228, 64), 10, 10);
        Stint(context, new Point(26, 50), "Soft", 44, Ease(t / 0.42) * 62);
        if (t > 0.44)
        {
            context.DrawLine(new Pen(Palette.TextMid, 2), new Point(115, 38), new Point(115, 62));
        }

        Stint(context, new Point(138, 50), "Medium", 156, Ease((t - 0.5) / 0.42) * 62);
    }

    /// <summary>The same compound twice: a fuel tank draining from full, then from much lower.</summary>
    private static void FuelLoads(DrawingContext context, double t)
    {
        Stint(context, new Point(22, 28), "Medium", 40, Ease(t / 0.45) * 130);
        Tank(context, new Rect(200, 10, 26, 36), 0.95 - 0.35 * Ease(t / 0.45));
        Stint(context, new Point(22, 72), "Medium", 40, Ease((t - 0.5) / 0.45) * 130);
        Tank(context, new Rect(200, 54, 26, 36), t < 0.5 ? 0.45 : 0.45 - 0.35 * Ease((t - 0.5) / 0.45));
    }

    /// <summary>
    /// Tyre wear fills a bar towards and past the cliff (dashed, 60 %); the lap time above climbs gently, then steeply
    /// once the wear is past it.
    /// </summary>
    private static void Cliff(DrawingContext context, double t)
    {
        const double left = 14, right = 226, cliffShare = 0.6;
        var width = right - left;
        var cliffX = left + width * cliffShare;
        var wear = Ease(t / 0.85);
        var head = left + width * wear;

        context.DrawRectangle(null, new Pen(Palette.LineStrong, 2), new Rect(left, 80, width, 10), 5, 5);
        if (wear > 0)
        {
            context.DrawRectangle(Palette.Faster, null, new Rect(left, 80, Math.Min(head, cliffX) - left, 10), 5, 5);
            if (head > cliffX)
            {
                context.DrawRectangle(Palette.Danger, null, new Rect(cliffX, 80, head - cliffX, 10), 3, 3);
            }
        }

        context.DrawLine(DashPen, new Point(cliffX, 8), new Point(cliffX, 94));

        double LapY(double x) => x <= cliffX ? 62 - (x - left) / (cliffX - left) * 8 : 54 - Math.Pow((x - cliffX) / (right - cliffX), 1.6) * 44;
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(new Point(left, LapY(left)), false);
            for (var x = left + 4; x <= head; x += 4)
            {
                g.LineTo(new Point(x, LapY(x)));
            }

            g.EndFigure(false);
        }

        var climbing = head > cliffX;
        context.DrawGeometry(null, new Pen(climbing ? Palette.Danger : Palette.TextHi, 3, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
        if (wear > 0)
        {
            context.DrawRectangle(climbing ? Palette.Danger : Palette.TextHi, null, new Rect(head - 4, LapY(head) - 4, 8, 8), 1.5, 1.5);
        }
    }

    /// <summary>
    /// A stop: the car leaves the track for the pit lane, stops at its box and rejoins, while a ghost car that stays out
    /// keeps going; the gap between them is what the stop costs. Under a safety car the field crawls behind it, so the
    /// ghost (and the gap) hardly moves.
    /// </summary>
    private static void PitStop(DrawingContext context, double t, bool safetyCar)
    {
        const double trackY = 30, laneY = 72, entry = 44, boxX = 104, exit = 164;
        context.DrawLine(TrackPen, new Point(8, trackY), new Point(232, trackY));
        var lane = new StreamGeometry();
        using (var g = lane.Open())
        {
            g.BeginFigure(new Point(entry, trackY), false);
            g.CubicBezierTo(new Point(entry + 14, trackY), new Point(entry + 16, laneY), new Point(entry + 34, laneY));
            g.LineTo(new Point(exit - 34, laneY));
            g.CubicBezierTo(new Point(exit - 16, laneY), new Point(exit - 14, trackY), new Point(exit, trackY));
            g.EndFigure(false);
        }

        context.DrawGeometry(null, ThinPen, lane);
        var stopping = t is > 0.45 and < 0.62;
        context.DrawRectangle(stopping ? Palette.Slower : null, new Pen(Palette.Slower, 2), new Rect(boxX - 9, laneY + 6, 18, 10), 2, 2);

        // The car that stays out: steady along the track (slow under a safety car).
        var pace = safetyCar ? 0.45 : 1.0;
        const double speed = 224 * 1.15; // the track, per loop, at racing speed
        var ghost = Math.Min(232, 8 + speed * pace * t);
        if (safetyCar)
        {
            var sc = Math.Min(232, ghost + 22);
            Car(context, new Point(sc, trackY), Palette.Slower);
            if (t % 0.25 < 0.15)
            {
                Chip(context, "SC", new Point(sc, trackY - 17));
            }
        }

        Car(context, new Point(ghost, trackY), Ghost);

        // The car that stops: in at the entry, along the lane to its box, a pause, out again.
        Point you;
        // In the lane the car is held to the pit limiter, so the car on the track pulls away from it.
        if (t < 0.2)
        {
            you = new Point(8 + (entry - 8) * (t / 0.2), trackY);
        }
        else if (t < 0.45)
        {
            var k = Ease((t - 0.2) / 0.25);
            you = new Point(entry + (boxX - entry) * k, trackY + (laneY - trackY) * Math.Min(1, k * 1.8));
        }
        else if (t < 0.62)
        {
            you = new Point(boxX, laneY);
        }
        else if (t < 0.84)
        {
            var k = Ease((t - 0.62) / 0.22);
            you = new Point(boxX + (exit - boxX) * k, laneY - (laneY - trackY) * Math.Max(0, k * 1.8 - 0.8));
        }
        else
        {
            you = new Point(Math.Min(232, exit + speed * (t - 0.84)), trackY);
        }

        Car(context, you, Palette.TextHi);

        // The time the stop costs: the gap to the car that stayed out, once both are back on the track.
        if (t > 0.84 && ghost > you.X + 6)
        {
            var bracket = new Pen(safetyCar ? Palette.Faster : Palette.Danger, 2);
            context.DrawLine(bracket, new Point(you.X + 8, trackY - 12), new Point(ghost - 8, trackY - 12));
            context.DrawLine(bracket, new Point(you.X + 8, trackY - 16), new Point(you.X + 8, trackY - 8));
            context.DrawLine(bracket, new Point(ghost - 8, trackY - 16), new Point(ghost - 8, trackY - 8));
        }
    }

    private static void Stint(DrawingContext context, Point badge, string compound, double barX, double length)
    {
        Badge(context, badge, 11, compound);
        if (length > 0)
        {
            context.DrawRectangle(Palette.Compound(compound), null, new Rect(barX, badge.Y - 5, length, 10), 5, 5);
        }
    }

    private static void Tank(DrawingContext context, Rect rect, double level)
    {
        level = Math.Clamp(level, 0, 1);
        context.DrawRectangle(null, new Pen(Palette.TextMid, 2), rect, 4, 4);
        var fill = (rect.Height - 6) * level;
        context.DrawRectangle(FuelBrush, null, new Rect(rect.X + 3, rect.Bottom - 3 - fill, rect.Width - 6, fill), 2, 2);
    }

    /// <summary>The compound as a square chip with its letter.</summary>
    private static void Badge(DrawingContext context, Point centre, double radius, string compound)
    {
        var colour = Palette.Compound(compound);
        context.DrawRectangle(null, new Pen(colour, 2.5), new Rect(centre.X - radius, centre.Y - radius, 2 * radius, 2 * radius), 4, 4);
        var letter = new FormattedText(Palette.CompoundLetter(compound), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LetterFace, radius * 1.1, colour);
        context.DrawText(letter, new Point(centre.X - letter.Width / 2, centre.Y - letter.Height / 2));
    }

    /// <summary>A car from above, nose to the right.</summary>
    private static void Car(DrawingContext context, Point at, IBrush fill) =>
        context.DrawRectangle(fill, CarOutline, new Rect(at.X - 9, at.Y - 5, 18, 10), 3, 3);

    private static void Chip(DrawingContext context, string text, Point centre)
    {
        var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LetterFace, 11, Palette.OnColor);
        var box = new Rect(centre.X - label.Width / 2 - 5, centre.Y - label.Height / 2 - 1, label.Width + 10, label.Height + 2);
        context.DrawRectangle(Palette.Slower, null, box, 3, 3);
        context.DrawText(label, new Point(box.X + 5, box.Y + 1));
    }

    /// <summary>0 before the step, 1 after it, eased in and out between.</summary>
    private static double Ease(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }
}
