using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using F1Telemetry.Core.Engine;

namespace F1Telemetry.App.Controls;

/// <summary>
/// Scrolling throttle/brake trace over the last few seconds, with a 0–100 % scale and live pedal bars.
/// <para>
/// Built for latency: it reads <see cref="InputHistory"/> (filled at packet rate on the ingest thread) directly,
/// bypassing bindings and the 20 Hz UI tick, and redraws on every display frame via
/// <see cref="TopLevel.RequestAnimationFrame"/> while data is flowing. When input stops it draws one last frame
/// and idles; a light poll restarts it as soon as a new packet lands. All buffers are preallocated.
/// </para>
/// </summary>
public sealed class InputTraceControl : Control
{
    public static readonly StyledProperty<InputHistory?> HistoryProperty =
        AvaloniaProperty.Register<InputTraceControl, InputHistory?>(nameof(History));

    public static readonly StyledProperty<double> WindowSecondsProperty =
        AvaloniaProperty.Register<InputTraceControl, double>(nameof(WindowSeconds), 6);

    public static readonly StyledProperty<bool> PreviewProperty =
        AvaloniaProperty.Register<InputTraceControl, bool>(nameof(Preview));

    private const double ScaleGutter = 46;
    private const double BarWidth = 14;
    private const double BarGap = 6;
    private const double BarsWidth = BarGap * 3 + BarWidth * 2;
    private const double IdleAfterSeconds = 0.5;

    private static readonly IBrush PlotBackground = new SolidColorBrush(Color.Parse("#0B0C0F"));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#22262E")), 1);
    private static readonly IPen SecondPen = new Pen(new SolidColorBrush(Color.Parse("#1A1D23")), 1);
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#8D97A6"));
    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.Parse("#22262E"));
    private static readonly Color Throttle = Color.Parse("#2EE88F");
    private static readonly Color Brake = Color.Parse("#FF5A4F");
    private static readonly IBrush ThrottleBrush = new SolidColorBrush(Throttle);
    private static readonly IBrush BrakeBrush = new SolidColorBrush(Brake);
    private static readonly IBrush ThrottleFill = new SolidColorBrush(Throttle, 0.16);
    private static readonly IBrush BrakeFill = new SolidColorBrush(Brake, 0.24);
    private static readonly IPen ThrottlePen = new Pen(ThrottleBrush, 2.5, lineJoin: PenLineJoin.Round);
    private static readonly IPen BrakePen = new Pen(BrakeBrush, 2.5, lineJoin: PenLineJoin.Round);

    private readonly double[] _time = new double[InputHistory.BufferSize];
    private readonly float[] _throttle = new float[InputHistory.BufferSize];
    private readonly float[] _brake = new float[InputHistory.BufferSize];
    private readonly FormattedText?[] _labels = new FormattedText?[5];
    private DispatcherTimer? _wakeTimer;
    private bool _frameRequested;
    private DateTime _frameRequestedAt;
    private long _renderedVersion = -1;

    static InputTraceControl()
    {
        AffectsRender<InputTraceControl>(HistoryProperty, WindowSecondsProperty, PreviewProperty);
        ClipToBoundsProperty.OverrideDefaultValue<InputTraceControl>(true);
    }

    public InputHistory? History { get => GetValue(HistoryProperty); set => SetValue(HistoryProperty, value); }
    public double WindowSeconds { get => GetValue(WindowSecondsProperty); set => SetValue(WindowSecondsProperty, value); }
    public bool Preview { get => GetValue(PreviewProperty); set => SetValue(PreviewProperty, value); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _wakeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Render, (_, _) => Wake());
        _wakeTimer.Start();
        Wake();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _wakeTimer?.Stop();
        _wakeTimer = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Starts the per-frame loop when new input has arrived since the last drawn frame.</summary>
    private void Wake()
    {
        if (_frameRequested && DateTime.UtcNow - _frameRequestedAt > TimeSpan.FromMilliseconds(500))
        {
            // A hidden window never delivers its frame callback; don't stay stuck waiting for it.
            _frameRequested = false;
        }

        if (!_frameRequested && IsEffectivelyVisible && History is { } history && history.Version != _renderedVersion)
        {
            RequestFrame();
        }
    }

    private void RequestFrame()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        _frameRequested = true;
        _frameRequestedAt = DateTime.UtcNow;
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan _)
    {
        _frameRequested = false;
        InvalidateVisual();

        // Keep animating while packets flow; once they stop, the frame just queued is the last one.
        if (IsEffectivelyVisible && History is { } history && history.Now() - history.LastArrival < IdleAfterSeconds)
        {
            RequestFrame();
        }
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= ScaleGutter + BarsWidth + 10 || height <= 20)
        {
            return;
        }

        var window = Math.Clamp(WindowSeconds, 1, InputHistory.MaxWindowSeconds);
        const double top = 8;
        var bottom = height - 8;
        var plot = new Rect(ScaleGutter, top, width - ScaleGutter - BarsWidth, bottom - top);
        context.FillRectangle(PlotBackground, plot, 6);

        // Scale: 0 / 25 / 50 / 75 / 100 %.
        for (var k = 0; k <= 4; k++)
        {
            var y = plot.Bottom - plot.Height * k / 4;
            context.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = _labels[k] ??= new FormattedText($"{k * 25}%", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(this.FindResource("DataFont") as FontFamily ?? FontFamily.Default, FontStyle.Normal, FontWeight.Bold), 12, LabelBrush);
            context.DrawText(label, new Point(ScaleGutter - 6 - label.Width, y - label.Height / 2));
        }

        // One faint line per second of history.
        for (var s = 1; s < window; s++)
        {
            var x = plot.Right - plot.Width * s / window;
            context.DrawLine(SecondPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
        }

        var count = Preview ? FillPreview(window) : History?.CopyWindow(window, _time, _throttle, _brake) ?? 0;
        _renderedVersion = History?.Version ?? -1;

        float throttleNow = 0, brakeNow = 0;
        if (count > 0)
        {
            using (context.PushClip(plot))
            {
                DrawTrace(context, plot, window, count, _throttle, ThrottleFill, ThrottlePen);
                DrawTrace(context, plot, window, count, _brake, BrakeFill, BrakePen);
            }

            throttleNow = _throttle[count - 1];
            brakeNow = _brake[count - 1];
        }

        DrawBar(context, new Rect(plot.Right + BarGap, plot.Top, BarWidth, plot.Height), throttleNow, ThrottleBrush);
        DrawBar(context, new Rect(plot.Right + BarGap * 2 + BarWidth, plot.Top, BarWidth, plot.Height), brakeNow, BrakeBrush);
    }

    private void DrawTrace(DrawingContext context, Rect plot, double window, int count, float[] values, IBrush fill, IPen pen)
    {
        double X(double t) => plot.Right + t / window * plot.Width;
        double Y(float v) => plot.Bottom - Math.Clamp(v, 0f, 1f) * plot.Height;

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var l = line.Open())
        using (var a = area.Open())
        {
            var start = new Point(X(_time[0]), Y(values[0]));
            l.BeginFigure(start, false);
            a.BeginFigure(new Point(start.X, plot.Bottom), true);
            a.LineTo(start);
            for (var i = 1; i < count; i++)
            {
                var p = new Point(X(_time[i]), Y(values[i]));
                l.LineTo(p);
                a.LineTo(p);
            }

            // Hold the newest value up to "now" so the trace always touches the right edge.
            var end = new Point(plot.Right, Y(values[count - 1]));
            l.LineTo(end);
            a.LineTo(end);
            a.LineTo(new Point(plot.Right, plot.Bottom));
            l.EndFigure(false);
            a.EndFigure(true);
        }

        context.DrawGeometry(fill, null, area);
        context.DrawGeometry(null, pen, line);
    }

    private static void DrawBar(DrawingContext context, Rect track, float value, IBrush brush)
    {
        context.FillRectangle(TrackBrush, track, 4);
        var h = track.Height * Math.Clamp(value, 0f, 1f);
        if (h > 0.5)
        {
            context.FillRectangle(brush, new Rect(track.X, track.Bottom - h, track.Width, h), 4);
        }
    }

    /// <summary>Representative corner pattern (full throttle, brake spike, trail-off, re-apply) for positioning.</summary>
    private int FillPreview(double window)
    {
        var count = Math.Min((int)(window * 60) + 1, _time.Length);
        for (var i = 0; i < count; i++)
        {
            var t = -window + i * window / (count - 1);
            var phase = ((t % 3.0) + 3.0) % 3.0;
            (_throttle[i], _brake[i]) = phase switch
            {
                < 1.5 => (1f, 0f),
                < 1.6 => ((float)(1 - (phase - 1.5) * 10), (float)((phase - 1.5) * 10)),
                < 2.2 => (0f, (float)(1 - (phase - 1.6) * 1.2)),
                _ => ((float)Math.Min(1, (phase - 2.2) / 0.8 * 1.1), 0f),
            };
            _time[i] = t;
        }

        return count;
    }
}
