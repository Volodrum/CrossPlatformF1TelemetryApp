using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using F1Telemetry.App.ViewModels;
using F1Telemetry.Core.Analytics;
using F1Telemetry.Core.Energy;

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

    /// <summary>The ERS deploy mode the item is about (the energy guide's speed curves).</summary>
    public static readonly StyledProperty<int?> ModeProperty =
        AvaloniaProperty.Register<GuideIllustration, int?>(nameof(Mode));

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
    private static readonly Typeface LetterFace = new(new FontFamily("avares://F1Telemetry/Assets/Fonts#Chakra Petch, avares://F1Telemetry/Assets/Fonts#IBM Plex Sans"), FontStyle.Normal, FontWeight.Bold);

    private DispatcherTimer? _timer;
    private readonly DateTime _start = DateTime.UtcNow;

    static GuideIllustration()
    {
        AffectsRender<GuideIllustration>(KindProperty, CompoundProperty, ModeProperty, IsAnimatingProperty, PhaseProperty);
    }

    public GuideKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public string? Compound { get => GetValue(CompoundProperty); set => SetValue(CompoundProperty, value); }
    public int? Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
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
            case GuideKind.PowerLaps:
                Laps(context, t, 10, Palette.Info, null);
                break;
            case GuideKind.RaceRun:
                Laps(context, t, 5, Palette.TextHi, null);
                break;
            case GuideKind.ModeSpeeds:
                ModeSpeeds(context, t, Mode ?? DeployModes.Medium);
                break;
            case GuideKind.ModeStraights:
                ModeStraights(context, t);
                break;
            case GuideKind.BrakingHarvest:
                BrakingHarvest(context, t);
                break;
            case GuideKind.AeroStates:
                AeroStates(context, t);
                break;
            case GuideKind.FullBattery:
                FullBattery(context, t);
                break;
            case GuideKind.Replay:
                Replay(context, t);
                break;
        }
    }

    // ---- The energy guide ----

    /// <summary>
    /// A car laps the circuit while lap tiles light up, <paramref name="count"/> of them in two rows when there are many:
    /// laps recorded with power data, or a run at race pace.
    /// </summary>
    private static void Laps(DrawingContext context, double t, int count, IBrush colour, string? compound)
    {
        context.DrawGeometry(null, TrackPen, CircuitGeometry);
        context.DrawLine(ThinPen, new Point(60, 80), new Point(60, 92));
        if (compound is not null)
        {
            Badge(context, new Point(50, 46), 12, compound);
        }

        var lapShare = 0.8 / count;
        var done = Math.Min(count, (int)(t / lapShare));
        var (at, heading) = AlongCircuit(done >= count ? 0 : t / lapShare % 1);
        using (context.PushTransform(Matrix.CreateRotation(heading) * Matrix.CreateTranslation(at.X, at.Y)))
        {
            context.DrawRectangle(Palette.TextHi, CarOutline, new Rect(-8, -4.5, 16, 9), 2.5, 2.5);
        }

        var perRow = count > 6 ? (count + 1) / 2 : count;
        var rows = (count + perRow - 1) / perRow;
        var width = 110.0 / perRow;
        var height = rows == 1 ? 40.0 : 26.0;
        for (var i = 0; i < count; i++)
        {
            var (row, column) = (i / perRow, i % perRow);
            var tile = new Rect(120 + column * width, (rows == 1 ? 30 : 18) + row * (height + 8), width - 4, height);
            var lit = i < done;
            context.DrawRectangle(lit ? colour : null, new Pen(lit ? colour : Palette.LineStrong, 2), tile, 3, 3);
        }
    }

    /// <summary>
    /// A deploy mode's output against speed: as the car runs flat out up the speed axis, a dot lights in each speed band
    /// it passes, tracing the mode's curve (rising, flat, fading near top speed).
    /// </summary>
    private static void ModeSpeeds(DrawingContext context, double t, int mode)
    {
        const double left = 40, right = 230, bottom = 84, top = 14;
        var colour = Palette.DeployMode(mode);
        ModeChip(context, mode, new Point(18, 50));
        context.DrawLine(ThinPen, new Point(left, bottom), new Point(right, bottom));
        context.DrawLine(ThinPen, new Point(left, bottom), new Point(left, top));

        static double Output(double k) => k < 0.25 ? 0.55 + k * 1.8 : k < 0.75 ? 1 : 1 - (k - 0.75) * 2.4; // 0–1 along the speeds
        const int bands = 14;
        var reached = Ease(t / 0.8) * bands;
        for (var i = 0; i < bands; i++)
        {
            var k = (i + 0.5) / bands;
            var centre = new Point(left + 8 + k * (right - left - 16), bottom - 6 - Output(k) * (bottom - top - 12));
            var lit = i < reached;
            context.DrawEllipse(lit ? colour : null, new Pen(lit ? colour : Palette.LineStrong, 1.5), centre, 4, 4);
        }

        // The car running up the speed axis.
        var x = left + 8 + Math.Min(1, reached / bands) * (right - left - 16);
        Car(context, new Point(x, bottom + 8), Palette.TextHi);
    }

    /// <summary>Four rows, one per mode: the lap's straights fill in as each mode is run on them, a lap per mode.</summary>
    private static void ModeStraights(DrawingContext context, double t)
    {
        int[] modes = [DeployModes.None, DeployModes.Medium, DeployModes.Hotlap, DeployModes.Overtake];
        const int straights = 5;
        for (var r = 0; r < modes.Length; r++)
        {
            var y = 14 + r * 23;
            ModeChip(context, modes[r], new Point(18, y));
            var filled = Ease((t - r * 0.2) / 0.2) * straights;
            for (var s = 0; s < straights; s++)
            {
                var bar = new Rect(36 + s * 40, y - 5, 34, 10);
                var lit = s < filled;
                var colour = Palette.DeployMode(modes[r]);
                context.DrawRectangle(lit ? colour : null, new Pen(lit ? colour : Palette.LineStrong, 1.5), bar, 4, 4);
            }
        }
    }

    /// <summary>
    /// Deploy hard on the straight (the battery drains), then brake (red markers): the battery has room, so it fills with
    /// what the braking harvests.
    /// </summary>
    private static void BrakingHarvest(DrawingContext context, double t)
    {
        const double trackY = 56, brakeFrom = 128, brakeTo = 186;
        context.DrawLine(TrackPen, new Point(8, trackY), new Point(196, trackY));
        for (var x = brakeFrom; x <= brakeTo; x += 14)
        {
            context.DrawLine(new Pen(Palette.Danger, 3, lineCap: PenLineCap.Round), new Point(x, trackY + 10), new Point(x + 6, trackY + 10));
        }

        // Fast to the braking zone, then slowing through it.
        var x0 = t < 0.45 ? 10 + (brakeFrom - 10) * (t / 0.45) : brakeFrom + (brakeTo - brakeFrom) * Math.Sin(Math.Min(1, (t - 0.45) / 0.45) * Math.PI / 2);
        Car(context, new Point(x0, trackY), Palette.TextHi);

        var level = t < 0.45 ? 0.6 - 0.4 * (t / 0.45) : 0.2 + 0.7 * Ease((t - 0.45) / 0.45);
        Battery(context, new Rect(204, 24, 28, 56), level, t >= 0.45 ? Palette.Faster : Palette.Info);
        if (t >= 0.45 && t < 0.92)
        {
            var arrow = new Pen(Palette.Faster, 2.5, lineCap: PenLineCap.Round);
            context.DrawLine(arrow, new Point(x0 + 12, trackY - 10), new Point(198, 40));
        }
    }

    /// <summary>Two straights flat out: the rear wing open (low drag) pulls away from the one shut.</summary>
    private static void AeroStates(DrawingContext context, double t)
    {
        var k = Ease(t / 0.85);
        for (var r = 0; r < 2; r++)
        {
            var open = r == 0;
            var y = open ? 30 : 74;
            context.DrawLine(ThinPen, new Point(30, y + 9), new Point(232, y + 9));
            Wing(context, new Point(14, y), open);
            var x = 40 + k * (open ? 180 : 140);
            Car(context, new Point(x, y), open ? Palette.Info : Palette.TextHi);
            if (open && t % 0.2 < 0.12)
            {
                context.DrawLine(DashPen, new Point(x - 30, y), new Point(x - 14, y)); // the faster car's streak
            }
        }
    }

    /// <summary>The out-lap fills the battery to the top; the push lap then spends all of it.</summary>
    private static void FullBattery(DrawingContext context, double t)
    {
        var level = t < 0.45 ? 0.3 + 0.7 * Ease(t / 0.4) : 1 - Ease((t - 0.5) / 0.4);
        Battery(context, new Rect(20, 16, 34, 70), level, t < 0.45 ? Palette.Faster : Palette.Info);
        context.DrawRectangle(null, new Pen(Palette.LineStrong, 2), new Rect(76, 44, 150, 12), 6, 6);
        var outLap = Ease(t / 0.45) * 70;
        context.DrawRectangle(Palette.Faster, null, new Rect(76, 44, outLap, 12), 6, 6);
        if (t > 0.5)
        {
            context.DrawRectangle(Palette.Info, null, new Rect(150, 44, Ease((t - 0.5) / 0.4) * 76, 12), 6, 6);
        }

        context.DrawLine(DashPen, new Point(148, 30), new Point(148, 70)); // the line: out-lap ends, push lap starts
    }

    /// <summary>The lap as driven (dashed) and the model's replay of it (solid) drawn together, ending side by side.</summary>
    private static void Replay(DrawingContext context, double t)
    {
        const double left = 14, right = 226;
        static double Driven(double k) => 50 + Math.Sin(k * Math.PI * 3) * 22 - k * 10;
        static double Model(double k) => Driven(k) + Math.Sin(k * Math.PI * 7) * 7 * (1 - k); // wanders early, then settles on the lap
        var head = Ease(t / 0.85);
        StreamGeometry Trace(Func<double, double> y)
        {
            var g = new StreamGeometry();
            using var c = g.Open();
            c.BeginFigure(new Point(left, y(0)), false);
            for (var k = 0.02; k <= head; k += 0.02)
            {
                c.LineTo(new Point(left + k * (right - left), y(k)));
            }

            c.EndFigure(false);
            return g;
        }

        context.DrawGeometry(null, new Pen(Palette.TextMid, 2, new DashStyle([4, 3], 0)), Trace(Driven));
        context.DrawGeometry(null, new Pen(Palette.Info, 2.5, lineCap: PenLineCap.Round), Trace(Model));
        if (t > 0.86)
        {
            var at = new Point(right, Driven(1));
            context.DrawEllipse(Palette.Faster, null, at, 5, 5);
        }
    }

    private static void ModeChip(DrawingContext context, int mode, Point centre)
    {
        var letter = new FormattedText(DeployModes.Letter(mode), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LetterFace, 11, Palette.OnDeployMode(mode));
        context.DrawRectangle(Palette.DeployMode(mode), null, new Rect(centre.X - 9, centre.Y - 8, 18, 16), 4, 4);
        context.DrawText(letter, new Point(centre.X - letter.Width / 2, centre.Y - letter.Height / 2));
    }

    private static void Battery(DrawingContext context, Rect rect, double level, IBrush fill)
    {
        level = Math.Clamp(level, 0, 1);
        context.DrawRectangle(Palette.TextMid, null, new Rect(rect.Center.X - 5, rect.Y - 4, 10, 5), 1.5, 1.5);
        context.DrawRectangle(null, new Pen(Palette.TextMid, 2), rect, 4, 4);
        var height = (rect.Height - 6) * level;
        context.DrawRectangle(fill, null, new Rect(rect.X + 3, rect.Bottom - 3 - height, rect.Width - 6, height), 2, 2);
    }

    /// <summary>A rear wing from the side: the flap lies flat when open (low drag), stands up when shut.</summary>
    private static void Wing(DrawingContext context, Point at, bool open)
    {
        var pen = new Pen(open ? Palette.Info : Palette.TextMid, 3, lineCap: PenLineCap.Round);
        context.DrawLine(pen, new Point(at.X - 8, at.Y + 6), new Point(at.X + 8, at.Y + 6)); // main plane
        context.DrawLine(pen, open ? new Point(at.X - 6, at.Y - 1) : new Point(at.X + 2, at.Y - 8), open ? new Point(at.X + 8, at.Y - 1) : new Point(at.X + 8, at.Y + 2));
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
        var label = new FormattedText(F1Telemetry.Core.Localization.Loc.T(text), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LetterFace, 11, Palette.OnColor);
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
