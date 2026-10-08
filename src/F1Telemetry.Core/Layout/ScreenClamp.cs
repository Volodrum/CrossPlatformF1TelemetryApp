namespace F1Telemetry.Core.Layout;

/// <summary>
/// Keeps a window fully inside a screen: a window that would stick out past an edge is pushed back in. A window larger
/// than the screen is aligned to its top-left so the overlay's start stays visible. Pure integer maths on physical pixels.
/// </summary>
public static class ScreenClamp
{
    /// <returns>The top-left closest to (<paramref name="x"/>, <paramref name="y"/>) that keeps the window on screen.</returns>
    public static (int X, int Y) Apply(int x, int y, int width, int height, int screenX, int screenY, int screenWidth, int screenHeight) =>
        (Axis(x, width, screenX, screenWidth), Axis(y, height, screenY, screenHeight));

    private static int Axis(int position, int length, int start, int extent) =>
        Math.Max(start, Math.Min(position, start + extent - length));
}
