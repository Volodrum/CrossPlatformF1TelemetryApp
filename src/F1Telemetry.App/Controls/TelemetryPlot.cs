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
    private static readonly Color Panel = Color.FromHex("#101216");
    private static readonly Color Divider = Color.FromHex("#22262E");
    private static readonly Color Line = Color.FromHex("#2C313B");
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
        var withData = model.Series.Where(s => s.X.Length > 0).ToList();
        var (left, right) = withData.Count > 0 ? (withData.Min(s => s.X.Min()), withData.Max(s => s.X.Max())) : (limits.Left, limits.Right);
        if (right <= left)
        {
            right = left + 1;
        }

        // Tags sit in a band of headroom above the data, one row per line, so they never cover a trace.
        IReadOnlyList<ChartLabel> labels = model.InvertY ? [] : model.Labels ?? [];
        if (labels.Count > 0)
        {
            var rows = labels.Max(l => l.Row) + 1;
            top += (top - bottom) * 0.13 * rows;
            foreach (var label in labels)
            {
                var text = plot.Add.Text(label.Text, label.X, top);
                text.LabelFontName = LabelFont;
                text.LabelFontSize = 12;
                text.LabelBold = true;
                text.LabelFontColor = Panel;
                text.LabelBackgroundColor = Color.FromHex(label.Color);
                text.LabelBorderRadius = 3;
                text.LabelPadding = 3;
                text.LabelAlignment = Alignment.UpperLeft;
                text.LabelOffsetX = 3;
                text.LabelOffsetY = 6 + label.Row * 24;
            }
        }

        plot.Axes.SetLimits(left, right, bottom, top);
        plot.Axes.Rules.Clear();
        plot.Axes.Rules.Add(new ScottPlot.AxisRules.LockedVertical(plot.Axes.Left, bottom, top));
        plot.Axes.Rules.Add(new ScottPlot.AxisRules.MaximumBoundary(plot.Axes.Bottom, plot.Axes.Left,
            new AxisLimits(left, right, Math.Min(bottom, top), Math.Max(bottom, top))));

        _plot.Refresh();
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
            var first = Math.Ceiling(range.Min / step);
            var ticks = new List<Tick>();
            for (var i = first; i * step <= range.Max && ticks.Count < MaxTickCount; i++)
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
