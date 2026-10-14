namespace F1Telemetry.App.ViewModels;

/// <summary>
/// Shared x range and cursor for a group of <c>TelemetryPlot</c>s on the same x axis (the charts of one lap):
/// zooming or panning one moves the others, and the pointer's position shows as a cursor line on all of them.
/// Set it on a container with <c>TelemetryPlot.Sync</c>; every chart inside inherits it.
/// </summary>
public sealed class ChartSync
{
    /// <summary>The x range the user zoomed or panned to; null = each chart shows its whole lap.</summary>
    public (double Left, double Right)? XRange { get; private set; }

    /// <summary>The x value under the pointer in one of the charts; null when the pointer is outside them all.</summary>
    public double? CursorX { get; private set; }

    /// <summary>Raised with the chart that changed the range (null after <see cref="Reset"/>).</summary>
    public event Action<object?>? XRangeChanged;

    /// <summary>Raised with the chart the pointer is over.</summary>
    public event Action<object?>? CursorChanged;

    public void SetXRange(object source, double left, double right)
    {
        if (XRange is { } current && Math.Abs(current.Left - left) < 1e-9 && Math.Abs(current.Right - right) < 1e-9)
        {
            return;
        }

        XRange = (left, right);
        XRangeChanged?.Invoke(source);
    }

    public void SetCursor(object source, double? x)
    {
        if (CursorX == x)
        {
            return;
        }

        CursorX = x;
        CursorChanged?.Invoke(source);
    }

    /// <summary>A new lap or x axis: every chart goes back to its whole lap, and the cursor goes.</summary>
    public void Reset()
    {
        XRange = null;
        CursorX = null;
        XRangeChanged?.Invoke(null);
        CursorChanged?.Invoke(null);
    }
}
