using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using F1Telemetry.Core.Energy;
using F1Telemetry.Core.Models;
using F1Telemetry.Core.Tracks;

namespace F1Telemetry.App.Controls;

/// <summary>What the coloured ribbon along the active lap shows (when <see cref="TrackMapControl.ColorByInputs"/> is on).</summary>
public enum MapColoring
{
    /// <summary>Throttle, partial, braking, coasting.</summary>
    Inputs,

    /// <summary>ERS deploy mode, in the deploy-mode ramp.</summary>
    DeployMode,

    /// <summary>Battery level in the ERS charge bands: 50 %+, 20–49 %, under 20 %.</summary>
    Battery,
}

/// <summary>
/// Circuit map: track limits from the SRTT outline, a faint reference lap, and the active lap as a
/// trajectory or coloured by driver input (green full throttle, yellow partial, red braking, white coasting), by ERS
/// deploy mode or by battery level (see <see cref="Coloring"/>).
/// On the deploy-mode map, <see cref="BatteryAtModeChanges"/> labels each change of mode with the battery there.
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

    public static readonly StyledProperty<MapColoring> ColoringProperty =
        AvaloniaProperty.Register<TrackMapControl, MapColoring>(nameof(Coloring));

    public static readonly StyledProperty<bool> BatteryAtModeChangesProperty =
        AvaloniaProperty.Register<TrackMapControl, bool>(nameof(BatteryAtModeChanges));

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
    private static readonly IPen[] InputPens = Pens(RibbonWidth, "#2EE88F", "#FFC23D", "#FF5A4F", "#8D97A6");

    // The energy map is about reading colour along the whole lap, so its ribbon is wider. No deployment is a thin grey
    // line, like coasting on the input map: the darkest step of the mode ramp would vanish into the tarmac.
    private const double EnergyRibbonWidth = 4;
    private static readonly IPen[] DeployModePens =
    [
        Pens(RibbonWidth, "#8D97A6")[0],
        .. Pens(EnergyRibbonWidth, ViewModels.Palette.DeployModeHex[1..]),
    ];
    private static readonly IPen[] BatteryPens = Pens(EnergyRibbonWidth, "#2EE88F", "#FFC23D", "#FF5A4F");

    // Battery labels at the mode changes: a dot on the line, a leader, and the level in a tag edged in the new mode's colour.
    // A run of one mode shorter than MinModeRun is noise in an averaged lap and gets no label of its own.
    private const double MinModeRun = 75;
    private const double LabelGap = 10;
    private static readonly Typeface LabelFace = new(new FontFamily("avares://F1Telemetry/Assets/Fonts#JetBrains Mono"), FontStyle.Normal, FontWeight.Bold);
    private static readonly IBrush LabelBack = new SolidColorBrush(Color.Parse("#0B0C0F"), 0.88);
    private static readonly IBrush LabelText = new SolidColorBrush(Color.Parse("#F4F6F9"));
    private static readonly IPen LeaderPen = new Pen(new SolidColorBrush(Color.Parse("#F4F6F9"), 0.5), 1);
    private static readonly IPen[] LabelEdges = Pens(1.5, ["#8D97A6", .. ViewModels.Palette.DeployModeHex[1..]]);

    private double _zoom = 1;
    private Vector _pan;
    private Point? _dragStart;

    static TrackMapControl()
    {
        AffectsRender<TrackMapControl>(OutlineProperty, ActiveLapProperty, ReferenceLapProperty, ColorByInputsProperty, ColoringProperty,
            BatteryAtModeChangesProperty);
        ClipToBoundsProperty.OverrideDefaultValue<TrackMapControl>(true);
    }

    public TrackOutline? Outline { get => GetValue(OutlineProperty); set => SetValue(OutlineProperty, value); }
    public IReadOnlyList<TelemetrySample>? ActiveLap { get => GetValue(ActiveLapProperty); set => SetValue(ActiveLapProperty, value); }
    public IReadOnlyList<TelemetrySample>? ReferenceLap { get => GetValue(ReferenceLapProperty); set => SetValue(ReferenceLapProperty, value); }
    public bool ColorByInputs { get => GetValue(ColorByInputsProperty); set => SetValue(ColorByInputsProperty, value); }
    public MapColoring Coloring { get => GetValue(ColoringProperty); set => SetValue(ColoringProperty, value); }

    /// <summary>On the deploy-mode map, label every change of mode with the battery level there, in % of the battery.</summary>
    public bool BatteryAtModeChanges { get => GetValue(BatteryAtModeChangesProperty); set => SetValue(BatteryAtModeChangesProperty, value); }

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
                switch (Coloring)
                {
                    case MapColoring.DeployMode:
                        DrawRibbon(context, active, Map, DeployModePens, s => Math.Clamp(s.ErsDeployMode, 0, DeployModePens.Length - 1));
                        break;
                    case MapColoring.Battery:
                        DrawRibbon(context, active, Map, BatteryPens, s => (s.ErsStoreEnergy / EnergyAnalyzer.Capacity * 100) switch { >= 50 => 0, >= 20 => 1, _ => 2 });
                        break;
                    default:
                        DrawRibbon(context, active, Map, InputPens, InputClass);
                        break;
                }
            }
            else
            {
                context.DrawGeometry(null, TrajectoryPen, Polyline(active.Select(s => Map(s.WorldPosX, s.WorldPosZ)), closed: false));
            }

            var last = Map(active[^1].WorldPosX, active[^1].WorldPosZ);
            context.DrawEllipse(null, MarkerHalo, last, MarkerRadius * 2, MarkerRadius * 2);
            context.DrawEllipse(MarkerFill, MarkerPen, last, MarkerRadius, MarkerRadius);

            if (ColorByInputs && Coloring == MapColoring.DeployMode && BatteryAtModeChanges)
            {
                DrawBatteryLabels(context, active, Map, Map(cx, cz));
            }
        }
    }

    /// <summary>
    /// The battery at every change of deploy mode, in a tag beside the line: outside the circuit where there is room,
    /// inside otherwise, left out where both would cover another tag (zoom in to see it).
    /// </summary>
    private static void DrawBatteryLabels(DrawingContext context, List<TelemetrySample> samples, Func<double, double, Point> map, Point centre)
    {
        var taken = new List<Rect>();
        foreach (var i in ModeChanges(samples))
        {
            var at = map(samples[i].WorldPosX, samples[i].WorldPosZ);
            var behind = map(samples[Math.Max(0, i - 3)].WorldPosX, samples[Math.Max(0, i - 3)].WorldPosZ);
            var ahead = map(samples[Math.Min(samples.Count - 1, i + 3)].WorldPosX, samples[Math.Min(samples.Count - 1, i + 3)].WorldPosZ);
            var along = ahead - behind;
            var length = Math.Sqrt(along.X * along.X + along.Y * along.Y);
            if (length < 1e-6)
            {
                continue;
            }

            var normal = new Vector(-along.Y / length, along.X / length);
            var outward = at - centre;
            if (normal.X * outward.X + normal.Y * outward.Y < 0)
            {
                normal = -normal;
            }

            var percent = Math.Clamp(samples[i].ErsStoreEnergy / EnergyAnalyzer.Capacity * 100, 0, 100);
            var text = new FormattedText(percent.ToString("0", CultureInfo.InvariantCulture) + "%", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, LabelFace, 11, LabelText);
            var size = new Size(text.Width + 8, text.Height + 2);
            foreach (var side in new[] { normal, -normal })
            {
                // Far enough out that the tag clears the line whichever way it faces.
                var reach = LabelGap + Math.Abs(side.X) * size.Width / 2 + Math.Abs(side.Y) * size.Height / 2;
                var middle = at + side * reach;
                var tag = new Rect(middle.X - size.Width / 2, middle.Y - size.Height / 2, size.Width, size.Height);
                if (taken.Any(r => r.Intersects(tag.Inflate(2))))
                {
                    continue;
                }

                taken.Add(tag);
                context.DrawLine(LeaderPen, at, at + side * LabelGap);
                context.DrawEllipse(MarkerFill, MarkerPen, at, 2.5, 2.5);
                context.DrawRectangle(LabelBack, LabelEdges[Math.Clamp(samples[i].ErsDeployMode, 0, LabelEdges.Length - 1)], tag, 3, 3);
                context.DrawText(text, new Point(tag.X + 4, tag.Y + 1));
                break;
            }
        }
    }

    /// <summary>
    /// The samples where the deploy mode changes, the lap's start aside. A run shorter than <see cref="MinModeRun"/> m
    /// is folded into the run before it.
    /// </summary>
    private static List<int> ModeChanges(List<TelemetrySample> samples)
    {
        var runs = new List<int>();
        for (var i = 0; i < samples.Count; i++)
        {
            if (runs.Count == 0 || samples[i].ErsDeployMode != samples[runs[^1]].ErsDeployMode)
            {
                runs.Add(i);
            }
        }

        var kept = new List<int>();
        for (var r = 0; r < runs.Count; r++)
        {
            var end = r + 1 < runs.Count ? samples[runs[r + 1]].LapDistance : samples[^1].LapDistance;
            if (kept.Count > 0 && (end - samples[runs[r]].LapDistance < MinModeRun
                                   || samples[runs[r]].ErsDeployMode == samples[kept[^1]].ErsDeployMode))
            {
                continue;
            }

            kept.Add(runs[r]);
        }

        return kept.Skip(1).ToList();
    }

    private static void DrawRibbon(DrawingContext context, List<TelemetrySample> samples, Func<double, double, Point> map, IPen[] pens, Func<TelemetrySample, int> classOf)
    {
        // Batch consecutive samples of the same class into one polyline per run.
        var start = 0;
        for (var i = 1; i <= samples.Count; i++)
        {
            if (i < samples.Count && classOf(samples[i]) == classOf(samples[start]))
            {
                continue;
            }

            var run = samples.Skip(Math.Max(0, start - 1)).Take(i - start + 1).Select(s => map(s.WorldPosX, s.WorldPosZ));
            context.DrawGeometry(null, pens[classOf(samples[start])], Polyline(run, closed: false));
            start = i;
        }
    }

    private static IPen[] Pens(double width, params string[] colors) =>
        [.. colors.Select(c => (IPen)new Pen(new SolidColorBrush(Color.Parse(c)), width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round))];

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
