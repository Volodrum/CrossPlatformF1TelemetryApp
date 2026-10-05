using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using F1Telemetry.App.ViewModels;
using ScottPlot;
using ScottPlot.Avalonia;

namespace F1Telemetry.App.Controls;

/// <summary>
/// Binds a <see cref="ChartModel"/> to a ScottPlot surface (Skia rendered, handles 10k+ points smoothly), styled
/// with the race HUD tokens: panel background, JetBrains Mono ticks, 3 px traces, dashed grey reference lap.
/// </summary>
public sealed class TelemetryPlot : ContentControl
{
    public static readonly StyledProperty<ChartModel?> ModelProperty =
        AvaloniaProperty.Register<TelemetryPlot, ChartModel?>(nameof(Model));

    private const string DataFont = "JetBrains Mono";
    private const string LabelFont = "Chakra Petch";
    private static readonly Color Bg0 = Color.FromHex("#07080A");
    private static readonly Color Panel = Color.FromHex("#101216");
    private static readonly Color Raised = Color.FromHex("#181B21");
    private static readonly Color Divider = Color.FromHex("#22262E");
    private static readonly Color Line = Color.FromHex("#2C313B");
    private static readonly Color TextHi = Color.FromHex("#F4F6F9");
    private static readonly Color TextMid = Color.FromHex("#C3CAD5");
    private static readonly Color TextLo = Color.FromHex("#8D97A6");

    private readonly AvaPlot _plot = new();

    static TelemetryPlot() => RegisterFonts();

    public TelemetryPlot()
    {
        Content = _plot;
        MinHeight = 200;
        ApplyStyle(_plot.Plot);

        // Legend outside the data area (so it never hides the trace). Added once: each call adds another panel.
        _plot.Plot.ShowLegend(Edge.Right);
        _plot.UserInputProcessor.DoubleLeftClickBenchmark(false);

        // Tunnel so this runs before ScottPlot sees the wheel.
        AddHandler(PointerWheelChangedEvent, OnPreviewPointerWheel, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Inside a scrollable page a plain wheel scrolls the page, so a stack of charts never traps the pointer;
    /// Ctrl + wheel zooms along the lap. Charts outside a scrollable page zoom with a plain wheel as before.
    /// </summary>
    private void OnPreviewPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        var scroller = this.FindAncestorOfType<ScrollViewer>();
        if (scroller is null || scroller.Extent.Height <= scroller.Viewport.Height)
        {
            return;
        }

        // 50 px per wheel notch, the same step ScrollViewer uses; Offset is clamped by the ScrollViewer.
        scroller.Offset = scroller.Offset.WithY(scroller.Offset.Y - e.Delta.Y * 50);
        e.Handled = true;
    }

    public ChartModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModelProperty)
        {
            Rebuild(Model);
        }
    }

    private void Rebuild(ChartModel? model)
    {
        var plot = _plot.Plot;
        plot.Clear();
        if (model is null)
        {
            _plot.Refresh();
            return;
        }

        plot.Title(model.Title.ToUpperInvariant(), size: 14);
        plot.Axes.Title.Label.FontName = LabelFont;
        plot.Axes.Title.Label.Bold = true;
        plot.Axes.Title.Label.ForeColor = TextMid;
        plot.Axes.Bottom.Label.Text = model.XLabel.ToUpperInvariant();
        plot.Axes.Bottom.Label.FontName = LabelFont;
        plot.Axes.Bottom.Label.FontSize = 13;
        plot.Axes.Bottom.Label.ForeColor = TextLo;

        foreach (var series in model.Series)
        {
            if (series.X.Length == 0)
            {
                continue;
            }

            var scatter = plot.Add.Scatter(series.X, series.Y);
            scatter.LegendText = series.InLegend ? series.Name : "";
            scatter.Color = series.IsReference ? TextLo : Color.FromHex(series.Color);
            // A line of one or two points would be invisible or easy to miss.
            scatter.MarkerSize = series.MarkerSize ?? (series.X.Length < 3 ? 7 : 0);
            scatter.MarkerShape = series.OpenMarkers ? MarkerShape.OpenCircle : MarkerShape.FilledCircle;
            scatter.MarkerLineWidth = 2;
            scatter.LineWidth = series.IsReference ? 2 : series.Line == ChartLine.None ? 0 : series.LineWidth;
            if (series.IsReference || series.Line == ChartLine.Dashed)
            {
                scatter.LinePattern = LinePattern.Dashed;
            }

            if (series.Step)
            {
                scatter.ConnectStyle = ConnectStyle.StepHorizontal;
            }

            if (series.EndLabel.Length > 0)
            {
                var label = AddLabel(plot, series.EndLabel, series.X[^1], series.Y[^1], DataFont, 12, TextHi);
                label.LabelAlignment = series.EndLabelBelow ? Alignment.UpperRight : Alignment.LowerRight;
                label.LabelOffsetY = series.EndLabelBelow ? 8 : -8;
            }
        }

        if (model.ZeroLine)
        {
            var zero = plot.Add.HorizontalLine(0, 2, TextLo, LinePattern.Dashed);
            zero.ExcludeFromLegend = true;
        }

        foreach (var marker in model.Markers ?? [])
        {
            var line = plot.Add.VerticalLine(marker.X, 2, Color.FromHex(marker.Color), LinePattern.Dotted);
            line.LegendText = marker.Label;
            line.ExcludeFromLegend = marker.Label.Length == 0;
        }

        plot.Axes.Left.TickGenerator = model.YIsTime ? new TimeTickGenerator()
            : model.IntegerY ? new ScottPlot.TickGenerators.NumericAutomatic { IntegerTicksOnly = true, LabelFormatter = v => model.YPrefix + v.ToString("0") }
            : new ScottPlot.TickGenerators.NumericAutomatic();
        plot.Axes.Bottom.TickGenerator = model.XIsTime
            ? new TimeTickGenerator()
            : new ScottPlot.TickGenerators.NumericAutomatic { IntegerTicksOnly = model.IntegerX };
        plot.Axes.AutoScale();

        // Mouse drag / wheel only pans and zooms along the lap-distance axis; the value axis stays fixed,
        // and the view can't leave the recorded distance range.
        var limits = plot.Axes.GetLimits();
        var (bottom, top) = model.InvertY ? (limits.Top, limits.Bottom) : (limits.Bottom, limits.Top);
        if (!model.InvertY && model.Series.Any(s => s.EndLabel.Length > 0))
        {
            // Labels sit above or below a line's last point: keep them inside the plot.
            var span = top - bottom;
            (bottom, top) = (bottom - span * 0.06, top + span * 0.08);
        }

        var withData = model.Series.Where(s => s.X.Length > 0).ToList();
        var (left, right) = withData.Count > 0 ? (withData.Min(s => s.X.Min()), withData.Max(s => s.X.Max())) : (limits.Left, limits.Right);
        IReadOnlyList<ChartTimelineRow> timeline = model.InvertY ? [] : model.Timeline ?? [];
        var segments = timeline.SelectMany(r => r.Segments).ToList();
        if (segments.Count > 0)
        {
            left = Math.Min(left, segments.Min(s => s.X0));
            right = Math.Max(right, segments.Max(s => s.X1));
            left -= (right - left) * 0.035; // room for the race badges at the start of each row
        }

        if (right <= left)
        {
            right = left + 1;
        }

        if (segments.Count > 0)
        {
            bottom = AddTimeline(plot, timeline, bottom, top, left, right);
        }

        plot.Axes.SetLimits(left, right, bottom, top);
        plot.Axes.Rules.Clear();
        plot.Axes.Rules.Add(new ScottPlot.AxisRules.LockedVertical(plot.Axes.Left, bottom, top));
        plot.Axes.Rules.Add(new ScottPlot.AxisRules.MaximumBoundary(plot.Axes.Bottom, plot.Axes.Left,
            new AxisLimits(left, right, Math.Min(bottom, top), Math.Max(bottom, top))));

        _plot.Refresh();
    }

    /// <summary>
    /// Draws a strategy timeline under the data on the same x axis: one row per entry, each stint a bar edged in its
    /// compound colour with a compound ring, its text and its value (as far as they fit), a PIT chip between stints,
    /// and a dotted line from each chip up through the data so the stop can be read against the laps.
    /// Value ticks stop at the data. Returns the new bottom of the value axis.
    /// </summary>
    private double AddTimeline(Plot plot, IReadOnlyList<ChartTimelineRow> rows, double dataBottom, double dataTop, double left, double right)
    {
        var rowHeight = (dataTop - dataBottom) * 0.17;
        var divider = dataBottom - rowHeight * 0.3;
        var bottom = divider - rowHeight * rows.Count - rowHeight * 0.1;
        if (plot.Axes.Left.TickGenerator is TimeTickGenerator ticks)
        {
            ticks.Min = dataBottom;
        }

        var rule = plot.Add.HorizontalLine(divider, 2, Line);
        rule.ExcludeFromLegend = true;

        // Pixels per x unit, to decide what text fits a stint: the plot is about its width minus axis and legend.
        var dataPixels = Math.Max(400, (Bounds.Width > 0 ? Bounds.Width : 1300) - 200);
        var pixelsPerUnit = dataPixels / (right - left);
        const double CharWidth = 7.5;

        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var y1 = divider - rowHeight * (r + 0.15);
            var y0 = y1 - rowHeight * 0.75;
            var mid = (y0 + y1) / 2;
            var raceColor = Color.FromHex(row.Color);

            var badge = AddLabel(plot, row.Label, left, mid, LabelFont, 14, Panel);
            badge.LabelAlignment = Alignment.MiddleLeft;
            badge.LabelOffsetX = 4;
            badge.LabelBackgroundColor = raceColor;
            badge.LabelBorderRadius = 4;
            badge.LabelPadding = 5;

            for (var s = 0; s < row.Segments.Count; s++)
            {
                var segment = row.Segments[s];
                var edge = Color.FromHex(segment.Edge);
                var bar = plot.Add.Rectangle(segment.X0, segment.X1, y0, y1);
                bar.FillColor = Raised;
                bar.LineColor = edge;
                bar.LineWidth = 2;

                // Keep clear of the PIT chips that sit on the stint's ends (about 18 px into each side).
                var startInset = s > 0 ? 22.0 : 6.0;
                var endInset = s < row.Segments.Count - 1 ? 24.0 : 8.0;
                var room = (segment.X1 - segment.X0) * pixelsPerUnit - startInset - endInset;
                var ring = AddLabel(plot, segment.Badge, segment.X0, mid, LabelFont, 11, edge);
                ring.LabelAlignment = Alignment.MiddleLeft;
                ring.LabelOffsetX = (float)startInset;
                ring.LabelBackgroundColor = Bg0;
                ring.LabelBorderColor = edge;
                ring.LabelBorderWidth = 2;
                ring.LabelBorderRadius = 10;
                ring.LabelPadding = 4;

                var textWidth = segment.Text.Length * CharWidth;
                var valueWidth = segment.Value.Length * CharWidth;
                if (room >= 26 + textWidth)
                {
                    var text = AddLabel(plot, segment.Text, segment.X0, mid, LabelFont, 12, TextHi);
                    text.LabelAlignment = Alignment.MiddleLeft;
                    text.LabelOffsetX = (float)(startInset + 26);
                }

                if (room >= 26 + textWidth + 16 + valueWidth)
                {
                    var value = AddLabel(plot, segment.Value, segment.X1, mid, DataFont, 12, TextMid);
                    value.LabelAlignment = Alignment.MiddleRight;
                    value.LabelOffsetX = (float)-endInset;
                }
            }

            foreach (var pit in row.Pits)
            {
                // The proximity line: from the chip up through the data, behind the laps.
                var line = plot.Add.Line(pit, mid, pit, dataTop);
                line.LineColor = raceColor;
                line.LineWidth = 1.5f;
                line.LinePattern = LinePattern.Dotted;
                line.LegendText = "";
                plot.MoveToBack(line);

                var chip = AddLabel(plot, "PIT", pit, mid, LabelFont, 11, TextHi);
                chip.LabelAlignment = Alignment.MiddleCenter;
                chip.LabelBackgroundColor = Bg0;
                chip.LabelBorderColor = raceColor;
                chip.LabelBorderWidth = 2;
                chip.LabelBorderRadius = 4;
                chip.LabelPadding = 4;
            }
        }

        return bottom;
    }

    private static ScottPlot.Plottables.Text AddLabel(Plot plot, string text, double x, double y, string font, float size, Color color)
    {
        var label = plot.Add.Text(text, x, y);
        label.LabelFontName = font;
        label.LabelFontSize = size;
        label.LabelBold = true;
        label.LabelFontColor = color;
        return label;
    }

    private static void ApplyStyle(Plot plot)
    {
        plot.FigureBackground.Color = Panel;
        plot.DataBackground.Color = Panel;
        plot.Axes.Color(TextLo);
        plot.Axes.FrameColor(Line);
        plot.Grid.MajorLineColor = Divider;
        plot.Grid.MajorLineWidth = 1;
        foreach (var axis in plot.Axes.GetAxes())
        {
            axis.TickLabelStyle.FontName = DataFont;
            axis.TickLabelStyle.FontSize = 13;
            axis.TickLabelStyle.Bold = true;
            axis.TickLabelStyle.ForeColor = TextLo;
            axis.MajorTickStyle.Color = Line;
            axis.MinorTickStyle.Color = Divider;
        }

        plot.Legend.BackgroundColor = Panel;
        plot.Legend.OutlineColor = Line;
        plot.Legend.FontColor = TextMid;
        plot.Legend.FontName = LabelFont;
        plot.Legend.FontSize = 13;
    }

    /// <summary>Ticks for an axis in seconds, on clock-friendly steps (5 s, 30 s, 1 min, 5 min…) labelled m:ss.</summary>
    private sealed class TimeTickGenerator : ITickGenerator
    {
        private static readonly double[] Steps = [0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200];

        public Tick[] Ticks { get; set; } = [];
        public int MaxTickCount { get; set; } = 1000;

        /// <summary>No ticks below <see cref="Min"/> or above <see cref="Max"/> (a timeline under the data has no values).</summary>
        public double Min { get; set; } = double.NegativeInfinity;

        public double Max { get; set; } = double.PositiveInfinity;

        public void Regenerate(CoordinateRange range, Edge edge, PixelLength size, Paint paint, LabelStyle labelStyle)
        {
            var span = range.Max - range.Min;
            if (!(span > 0) || double.IsInfinity(span))
            {
                Ticks = [];
                return;
            }

            // Roughly one label per 90 px keeps m:ss.t labels from colliding side by side; stacked, 40 px is enough.
            var maxTicks = Math.Max(2, size.Length / (edge is Edge.Left or Edge.Right ? 40 : 90));
            var step = Steps.FirstOrDefault(s => span / s <= maxTicks, Steps[^1]);
            var first = Math.Ceiling(Math.Max(range.Min, Min) / step);
            var ticks = new List<Tick>();
            for (var i = first; i * step <= Math.Min(range.Max, Max) && ticks.Count < MaxTickCount; i++)
            {
                var value = i * step;
                ticks.Add(Tick.Major(value, LapAxis.FormatTime(value)));
            }

            Ticks = [.. ticks];
        }
    }

    /// <summary>Skia can't read Avalonia's embedded resources, so the fonts ship as files next to the exe too.</summary>
    private static void RegisterFonts()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "fonts");
        void Add(string name, string file, bool bold)
        {
            var path = Path.Combine(dir, file);
            if (File.Exists(path))
            {
                Fonts.AddFontFile(name, path, bold, italic: false);
            }
        }

        Add(DataFont, "JetBrainsMono-Medium.ttf", bold: false);
        Add(DataFont, "JetBrainsMono-ExtraBold.ttf", bold: true);
        Add(LabelFont, "ChakraPetch-SemiBold.ttf", bold: false);
        Add(LabelFont, "ChakraPetch-Bold.ttf", bold: true);
    }
}
