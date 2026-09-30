namespace F1Telemetry.Core.Layout;

/// <summary>Result of <see cref="CentreSnap.Apply"/>: the (possibly moved) top-left and which axes snapped.</summary>
public readonly record struct SnapResult(int X, int Y, bool SnappedX, bool SnappedY);

/// <summary>
/// Magnetic centre lines for dragging overlays: when a window's centre comes within <c>threshold</c> pixels of the
/// screen's vertical or horizontal centre line, it is pulled exactly onto it. Pure integer maths on physical pixels.
/// </summary>
public static class CentreSnap
{
    /// <param name="x">Proposed window left.</param>
    /// <param name="y">Proposed window top.</param>
    /// <param name="width">Window width.</param>
    /// <param name="height">Window height.</param>
    /// <param name="screenX">Screen bounds left.</param>
    /// <param name="screenY">Screen bounds top.</param>
    /// <param name="screenWidth">Screen bounds width.</param>
    /// <param name="screenHeight">Screen bounds height.</param>
    /// <param name="threshold">Snap distance.</param>
    public static SnapResult Apply(int x, int y, int width, int height, int screenX, int screenY, int screenWidth, int screenHeight, double threshold)
    {
        var centreX = x + width / 2;
        var centreY = y + height / 2;
        var screenCentreX = screenX + screenWidth / 2;
        var screenCentreY = screenY + screenHeight / 2;

        var snapX = Math.Abs(centreX - screenCentreX) <= threshold;
        var snapY = Math.Abs(centreY - screenCentreY) <= threshold;
        return new SnapResult(
            snapX ? screenCentreX - width / 2 : x,
            snapY ? screenCentreY - height / 2 : y,
            snapX,
            snapY);
    }
}
