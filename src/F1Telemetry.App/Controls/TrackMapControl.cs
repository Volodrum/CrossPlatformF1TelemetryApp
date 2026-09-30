using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using F1Telemetry.Core.Models;
using F1Telemetry.Core.Tracks;

namespace F1Telemetry.App.Controls;

/// <summary>
/// Circuit map: track limits from the SRTT outline, a faint reference lap, and the active lap as a
/// trajectory or coloured by driver input (green full throttle, yellow partial, red braking, white coasting).
/// Wheel zooms around the cursor, drag pans, double-click resets. Falls back to fitting the trajectory
/// when no outline is available for the track.
/// <para>
/// Zoom and pan are applied to the coordinates, not as a render transform, so lines keep their on-screen width at
/// any zoom: zooming in spreads two laps' lines apart instead of making them fatter. The tarmac is drawn at its real
/// width between the track limits.
/// </para>
/// </summary>
public sealed class TrackMapControl : Control
{
    public static readonly StyledProperty<TrackOutline?> OutlineProperty =
        AvaloniaProperty.Register<TrackMapControl, TrackOutline?>(nameof(Outline));

    public static readonly StyledProperty<IReadOnlyList<TelemetrySample>?> ActiveLapProperty =
        AvaloniaProperty.Register<TrackMapControl, IReadOnlyList<TelemetrySample>?>(nameof(ActiveLap));

    public static readonly StyledProperty<IReadOnlyList<TelemetrySample>?> ReferenceLapProperty =
        AvaloniaProperty.Register<TrackMapControl, IReadOnlyList<TelemetrySample>?>(nameof(ReferenceLap));

    public static readonly StyledProperty<bool> ColorByInputsProperty =
        AvaloniaProperty.Register<TrackMapControl, bool>(nameof(ColorByInputs), true);

    // Race HUD styling: dark ground, tarmac at true width (at least 6 px) with 1 px limits, a thin input ribbon (green full throttle,
    // amber partial, red braking, grey coasting), a thin solid grey reference lap and a small white car marker.
    // Widths are screen pixels at every zoom level.
    private const double RibbonWidth = 2.5;
    private const double MarkerRadius = 4.5;

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#0B0C0F"));
    private static readonly IBrush Tarmac = new SolidColorBrush(Color.Parse("#232730"));
    // Keeps the track visible when zoomed out, where its true width is only a pixel or two; zoomed in, the true-width
    // tarmac is wider than this and takes over.
    private static readonly IPen MinimumBandPen = new Pen(Tarmac, 6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    private static readonly IPen LimitPen = new Pen(new SolidColorBrush(Color.Parse("#4A5160")), 1);
    private static readonly IPen ReferencePen = new Pen(new SolidColorBrush(Color.Parse("#8D97A6"), 0.85), 1.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    private static readonly IPen TrajectoryPen = new Pen(new SolidColorBrush(Color.Parse("#F4F6F9")), 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    private static readonly IPen MarkerPen = new Pen(new SolidColorBrush(Color.Parse("#07080A")), 1.5);
    private static readonly IPen MarkerHalo = new Pen(new SolidColorBrush(Color.Parse("#F4F6F9"), 0.35), 1.5);
    private static readonly IBrush MarkerFill = new SolidColorBrush(Color.Parse("#F4F6F9"));
    private static readonly IPen[] InputPens =
    [
        new Pen(new SolidColorBrush(Color.Parse("#2EE88F")), RibbonWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round),
        new Pen(new SolidColorBrush(Color.Parse("#FFC23D")), RibbonWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round),
        new Pen(new SolidColorBrush(Color.Parse("#FF5A4F")), RibbonWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round),
        new Pen(new SolidColorBrush(Color.Parse("#8D97A6")), RibbonWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round),
    ];

    private double _zoom = 1;
    private Vector _pan;
    private Point? _dragStart;

    static TrackMapControl()
    {
        AffectsRender<TrackMapControl>(OutlineProperty, ActiveLapProperty, ReferenceLapProperty, ColorByInputsProperty);
        ClipToBoundsProperty.OverrideDefaultValue<TrackMapControl>(true);
    }

    public TrackOutline? Outline { get => GetValue(OutlineProperty); set => SetValue(OutlineProperty, value); }
    public IReadOnlyList<TelemetrySample>? ActiveLap { get => GetValue(ActiveLapProperty); set => SetValue(ActiveLapProperty, value); }
    public IReadOnlyList<TelemetrySample>? ReferenceLap { get => GetValue(ReferenceLapProperty); set => SetValue(ReferenceLapProperty, value); }
    public bool ColorByInputs { get => GetValue(ColorByInputsProperty); set => SetValue(ColorByInputsProperty, value); }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Background, new Rect(Bounds.Size));
        var outline = Outline;
        var active = ActiveLap?.Where(s => s.WorldPosX != 0 || s.WorldPosZ != 0).ToList() ?? [];
        var reference = ReferenceLap?.Where(s => s.WorldPosX != 0 || s.WorldPosZ != 0).ToList() ?? [];

        if (!TryGetBounds(outline, active, reference, out var minX, out var maxX, out var minZ, out var maxZ))
        {
            return;
        }

        const double padding = 24;
        var scale = Math.Min((Bounds.Width - 2 * padding) / Math.Max(maxX - minX, 1), (Bounds.Height - 2 * padding) / Math.Max(maxZ - minZ, 1));
        var cx = (minX + maxX) / 2;
        var cz = (minZ + maxZ) / 2;
        // Same mapping a Scale(zoom) · Translate(pan) render transform would give, but on the points only.
        Point Map(double x, double z) => new(
            (Bounds.Width / 2 + (x - cx) * scale) * _zoom + _pan.X,
            (Bounds.Height / 2 + (z - cz) * scale) * _zoom + _pan.Y);

        if (outline is not null)
        {
            var tarmac = new StreamGeometry();
            using (var g = tarmac.Open())
            {
                g.BeginFigure(Map(outline.Left[0].X, outline.Left[0].Z), true);
                foreach (var p in outline.Left.Skip(1))
                {
                    g.LineTo(Map(p.X, p.Z));
                }

                foreach (var p in outline.Right.Reverse())
                {
                    g.LineTo(Map(p.X, p.Z));
                }

                g.EndFigure(true);
            }

            context.DrawGeometry(null, MinimumBandPen, Polyline(outline.Centerline.Select(p => Map(p.X, p.Z)), closed: true));
            context.DrawGeometry(Tarmac, null, tarmac);
            context.DrawGeometry(null, LimitPen, Polyline(outline.Left.Select(p => Map(p.X, p.Z)), closed: true));
            context.DrawGeometry(null, LimitPen, Polyline(outline.Right.Select(p => Map(p.X, p.Z)), closed: true));
        }

        if (reference.Count > 1)
        {
            context.DrawGeometry(null, ReferencePen, Polyline(reference.Select(s => Map(s.WorldPosX, s.WorldPosZ)), closed: false));
        }

        if (active.Count > 1)
        {
            if (ColorByInputs)
            {
                DrawInputRibbon(context, active, Map);
            }
            else
            {
                context.DrawGeometry(null, TrajectoryPen, Polyline(active.Select(s => Map(s.WorldPosX, s.WorldPosZ)), closed: false));
            }

            var last = Map(active[^1].WorldPosX, active[^1].WorldPosZ);
            context.DrawEllipse(null, MarkerHalo, last, MarkerRadius * 2, MarkerRadius * 2);
            context.DrawEllipse(MarkerFill, MarkerPen, last, MarkerRadius, MarkerRadius);
        }
    }

    private static void DrawInputRibbon(DrawingContext context, List<TelemetrySample> samples, Func<double, double, Point> map)
    {
        // Batch consecutive samples with the same input class into one polyline per class run.
        var start = 0;
        for (var i = 1; i <= samples.Count; i++)
        {
            if (i < samples.Count && InputClass(samples[i]) == InputClass(samples[start]))
            {
                continue;
            }

            var run = samples.Skip(Math.Max(0, start - 1)).Take(i - start + 1).Select(s => map(s.WorldPosX, s.WorldPosZ));
            context.DrawGeometry(null, InputPens[InputClass(samples[start])], Polyline(run, closed: false));
            start = i;
        }
    }

    private static int InputClass(TelemetrySample s) => s.Brake > 0.1 ? 2 : s.Throttle > 0.8 ? 0 : s.Throttle > 0.1 ? 1 : 3;

    private static StreamGeometry Polyline(IEnumerable<Point> points, bool closed)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        var first = true;
        foreach (var p in points)
        {
            if (first)
            {
                g.BeginFigure(p, false);
                first = false;
            }
            else
            {
                g.LineTo(p);
            }
        }

        if (!first)
        {
            g.EndFigure(closed);
        }

        return geometry;
    }

    private static bool TryGetBounds(TrackOutline? outline, List<TelemetrySample> active, List<TelemetrySample> reference,
        out double minX, out double maxX, out double minZ, out double maxZ)
    {
        if (outline is not null)
        {
            (minX, maxX, minZ, maxZ) = (outline.MinX, outline.MaxX, outline.MinZ, outline.MaxZ);
            return true;
        }

        var all = active.Count > 0 ? active : reference;
        if (all.Count < 2)
        {
            minX = maxX = minZ = maxZ = 0;
            return false;
        }

        (minX, maxX, minZ, maxZ) = (all.Min(s => s.WorldPosX), all.Max(s => s.WorldPosX), all.Min(s => s.WorldPosZ), all.Max(s => s.WorldPosZ));
        return true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var factor = e.Delta.Y > 0 ? 1.2 : 1 / 1.2;
        // Up to 60×: enough to see where two laps sit within the track width.
        var newZoom = Math.Clamp(_zoom * factor, 0.5, 60);
        var cursor = e.GetPosition(this);
        _pan = (Vector)cursor - ((Vector)cursor - _pan) * (newZoom / _zoom);
        _zoom = newZoom;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _zoom = 1;
            _pan = default;
            InvalidateVisual();
            return;
        }

        _dragStart = e.GetPosition(this) - _pan;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_dragStart is { } start)
        {
            _pan = e.GetPosition(this) - start;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _dragStart = null;
        e.Pointer.Capture(null);
    }
}
