using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using F1Telemetry.App.Platform;
using F1Telemetry.App.Services;
using F1Telemetry.Core.Layout;

namespace F1Telemetry.App.Views.Overlays;

/// <summary>
/// Borderless, transparent, always-on-top, non-activating HUD window. Click-through while racing; in edit
/// mode it becomes draggable with a highlighted frame so it can be positioned on screen directly. The content is
/// laid out at its design size and scaled as a whole, so each overlay can be resized from the settings page.
/// <para>
/// While dragged, the overlay's centre is magnetic to the screen's centre lines (horizontal and vertical); a blue
/// guide shows through the overlay while it is snapped.
/// </para>
/// <para>
/// In edit mode a <see cref="Hole"/> can be cut through the window: nothing is drawn there and clicks inside it
/// reach the window underneath. That keeps the settings page's preview toggle visible and clickable even when
/// an overlay has been dragged on top of it.
/// </para>
/// </summary>
public sealed class OverlayWindow : Window
{
    private const int WmNcHitTest = 0x0084;
    private const int WmMoving = 0x0216;
    private const int WmExitSizeMove = 0x0232;
    private const int HtTransparent = -1;

    // How close (in DIPs) the overlay's centre must come to a screen centre line to snap onto it.
    private const double SnapDistance = 16;

    private static readonly IBrush GuideBrush = new SolidColorBrush(Color.Parse("#4DB5FF"));

    private readonly Border _frame;
    private readonly LayoutTransformControl _scaler;
    private readonly Rectangle _holeOutline;
    private readonly Rectangle _verticalGuide;
    private readonly Rectangle _horizontalGuide;
    private bool _editMode;
    private PixelRect? _hole;

    // Pointer-driven drag (non-Windows): cursor and window position when the drag began.
    private PixelPoint? _dragCursorStart;
    private PixelPoint _dragWindowStart;

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left, Top, Right, Bottom;
    }

    public OverlayWindow(OverlayKind kind, Control content, double scale, double opacity = 1)
    {
        Kind = kind;
        Title = $"HUD overlay – {kind}";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _scaler = new LayoutTransformControl { Child = content };
        Scale = scale;
        ContentOpacity = opacity;
        _frame = new Border { Child = _scaler, BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent };
        _holeOutline = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.Parse("#E10600")),
            StrokeThickness = 2,
            StrokeDashArray = [3, 2],
            IsHitTestVisible = false,
            IsVisible = false,
        };
        _verticalGuide = new Rectangle { Fill = GuideBrush, Width = 2, IsHitTestVisible = false, IsVisible = false };
        _horizontalGuide = new Rectangle { Fill = GuideBrush, Height = 2, IsHitTestVisible = false, IsVisible = false };

        // The canvas measures as 0×0, so the outline and guides never affect the window's size-to-content.
        Content = new Panel
        {
            Children =
            {
                _frame,
                new Canvas { Children = { _holeOutline, _verticalGuide, _horizontalGuide }, IsHitTestVisible = false },
            },
        };

        Opened += (_, _) =>
        {
            ApplyInteractivity();
            ApplyHole();
        };
        PositionChanged += (_, _) => ApplyHole();
        _frame.SizeChanged += (_, _) => ApplyHole();
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += (_, _) => EndPointerDrag();
        PointerCaptureLost += (_, _) => EndPointerDrag();

        if (OperatingSystem.IsWindows())
        {
            Win32Properties.AddWndProcHookCallback(this, WndProcHook);
        }
    }

    public OverlayKind Kind { get; }

    /// <summary>Size multiplier applied to the overlay content (1 = design size).</summary>
    public double Scale
    {
        get => (_scaler.LayoutTransform as ScaleTransform)?.ScaleX ?? 1;
        set => _scaler.LayoutTransform = new ScaleTransform(value, value);
    }

    /// <summary>Opacity of the overlay content (the edit-mode frame, hole outline and guides stay fully opaque).</summary>
    public double ContentOpacity
    {
        get => _scaler.Opacity;
        set => _scaler.Opacity = value;
    }

    /// <summary>
    /// Screen area (physical pixels) to leave see-through and click-through, or null for none. Only applied in edit
    /// mode (outside it the whole window is click-through anyway).
    /// </summary>
    public PixelRect? Hole
    {
        get => _hole;
        set
        {
            if (_hole != value)
            {
                _hole = value;
                ApplyHole();
            }
        }
    }

    /// <summary>Raised when the user finishes dragging the overlay in edit mode.</summary>
    public event Action<OverlayWindow>? Moved;

    public bool EditMode
    {
        get => _editMode;
        set
        {
            _editMode = value;
            _frame.BorderBrush = value ? new SolidColorBrush(Color.Parse("#E10600")) : Brushes.Transparent;
            Cursor = value ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;
            ShowGuides(false, false);
            ApplyInteractivity();
            ApplyHole();
        }
    }

    /// <summary>
    /// Pulls a window rectangle (physical pixels) onto the screen's centre lines when its centre is within
    /// <see cref="SnapDistance"/> of them. Returns the snapped top-left.
    /// </summary>
    private PixelPoint Snap(PixelPoint topLeft, PixelSize size, out bool snappedX, out bool snappedY)
    {
        snappedX = snappedY = false;
        var centre = new PixelPoint(topLeft.X + size.Width / 2, topLeft.Y + size.Height / 2);
        if ((Screens.ScreenFromPoint(centre) ?? Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } screen)
        {
            return topLeft;
        }

        var b = screen.Bounds;
        var snap = CentreSnap.Apply(topLeft.X, topLeft.Y, size.Width, size.Height, b.X, b.Y, b.Width, b.Height, SnapDistance * screen.Scaling);
        (snappedX, snappedY) = (snap.SnappedX, snap.SnappedY);
        return new PixelPoint(snap.X, snap.Y);
    }

    /// <summary>Snapped on the vertical centre line → vertical guide; on the horizontal one → horizontal guide.</summary>
    private void ShowGuides(bool vertical, bool horizontal)
    {
        var size = Bounds.Size;
        _verticalGuide.IsVisible = vertical;
        _verticalGuide.Height = size.Height;
        Canvas.SetLeft(_verticalGuide, size.Width / 2 - 1);
        _horizontalGuide.IsVisible = horizontal;
        _horizontalGuide.Width = size.Width;
        Canvas.SetTop(_horizontalGuide, size.Height / 2 - 1);
    }

    /// <summary>The hole in window-local physical pixels, when it overlaps this window in edit mode.</summary>
    private PixelRect? LocalHole()
    {
        if (!_editMode || _hole is not { } hole || !IsVisible)
        {
            return null;
        }

        var size = PixelSize.FromSize(ClientSize, RenderScaling);
        var local = new PixelRect(hole.X - Position.X, hole.Y - Position.Y, hole.Width, hole.Height)
            .Intersect(new PixelRect(size));
        return local.Width > 0 && local.Height > 0 ? local : null;
    }

    private void ApplyHole()
    {
        var local = LocalHole();
        if (local is { } px)
        {
            var dip = px.ToRect(RenderScaling);
            _frame.Clip = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(-10, -10, 100_000, 100_000)), new RectangleGeometry(dip));
            _holeOutline.Width = dip.Width + 2;
            _holeOutline.Height = dip.Height + 2;
            Canvas.SetLeft(_holeOutline, dip.X - 1);
            Canvas.SetTop(_holeOutline, dip.Y - 1);
            _holeOutline.IsVisible = true;
        }
        else
        {
            _frame.Clip = null;
            _holeOutline.IsVisible = false;
        }

        // Windows handles the input side in WndProcHook; X11 needs the hole cut out of the input shape. Outside edit
        // mode the input shape is empty (fully click-through) and must stay that way.
        if (IsVisible && _editMode && !OperatingSystem.IsWindows())
        {
            OverlayInterop.SetInputHole(this, local);
        }
    }

    /// <summary>
    /// Windows: inside the hole, report HTTRANSPARENT so the click goes to the window underneath owned by the same
    /// thread (the main window with the preview toggle). During the native move loop, WM_MOVING hands over the
    /// proposed rectangle, which is snapped in place — Windows recomputes it from the cursor on every move, so the
    /// overlay lets go again once dragged past the snap distance.
    /// </summary>
    private unsafe IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmNcHitTest when _editMode && _hole is { } hole:
                var x = (short)(lParam.ToInt64() & 0xFFFF);
                var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                if (hole.Contains(new PixelPoint(x, y)))
                {
                    handled = true;
                    return HtTransparent;
                }

                break;

            case WmMoving when _editMode && lParam != IntPtr.Zero:
                var rect = (Win32Rect*)lParam;
                var size = new PixelSize(rect->Right - rect->Left, rect->Bottom - rect->Top);
                var snapped = Snap(new PixelPoint(rect->Left, rect->Top), size, out var sx, out var sy);
                rect->Left = snapped.X;
                rect->Top = snapped.Y;
                rect->Right = snapped.X + size.Width;
                rect->Bottom = snapped.Y + size.Height;
                ShowGuides(sx, sy);
                handled = true;
                return new IntPtr(1);

            case WmExitSizeMove:
                ShowGuides(false, false);
                break;
        }

        return IntPtr.Zero;
    }

    private void ApplyInteractivity()
    {
        if (IsVisible)
        {
            OverlayInterop.SetClickThrough(this, !_editMode);
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_editMode || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            // Native move loop (smooth, OS-driven); snapping happens in WM_MOVING. Returns when the drag ends.
            BeginMoveDrag(e);
            Moved?.Invoke(this);
            return;
        }

        // Elsewhere the OS drag gives no hook to adjust the position, so move the window ourselves.
        _dragCursorStart = this.PointToScreen(e.GetPosition(this));
        _dragWindowStart = Position;
        e.Pointer.Capture(this);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragCursorStart is not { } start)
        {
            return;
        }

        var cursor = this.PointToScreen(e.GetPosition(this));
        var proposed = new PixelPoint(_dragWindowStart.X + cursor.X - start.X, _dragWindowStart.Y + cursor.Y - start.Y);
        Position = Snap(proposed, PixelSize.FromSize(ClientSize, RenderScaling), out var sx, out var sy);
        ShowGuides(sx, sy);
    }

    private void EndPointerDrag()
    {
        if (_dragCursorStart is null)
        {
            return;
        }

        _dragCursorStart = null;
        ShowGuides(false, false);
        Moved?.Invoke(this);
    }
}
