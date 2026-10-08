using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace F1Telemetry.App.Platform;

/// <summary>
/// OS-specific window flags Avalonia doesn't expose: click-through (mouse events pass to the game underneath),
/// no activation, and "above full-screen" z-order.
/// <list type="bullet">
/// <item>Windows: WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE. Works over borderless-windowed
/// games; exclusive full-screen DirectX cannot be overlaid by any desktop window.</item>
/// <item>macOS: NSWindow.ignoresMouseEvents + screen-saver window level.</item>
/// <item>Linux/X11: empty XShape input region. Wayland does not allow global overlays; there overlays stay regular windows.</item>
/// </list>
/// </summary>
public static partial class OverlayInterop
{
    public static void SetClickThrough(Window window, bool clickThrough)
    {
        var handle = window.TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Win32.SetClickThrough(handle.Handle, clickThrough);
            }
            else if (OperatingSystem.IsMacOS() && handle.HandleDescriptor == "NSWindow")
            {
                MacOs.SetClickThrough(handle.Handle, clickThrough);
            }
            else if (OperatingSystem.IsLinux() && handle.HandleDescriptor == "XID")
            {
                X11.SetClickThrough(handle.Handle, clickThrough);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Missing native library (e.g. libXext): overlay still works, just not click-through.
        }
    }

    /// <summary>
    /// Whether a window can pass clicks through just part of itself (see <see cref="SetInputHole"/>). Windows does it
    /// via WM_NCHITTEST in <c>OverlayWindow</c>, X11 via the input shape; macOS can only ignore the mouse for the whole
    /// window, and Wayland has no global overlays.
    /// </summary>
    public static bool SupportsInputHoles => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    /// <summary>
    /// X11: makes the window accept input everywhere except <paramref name="hole"/> (window-local physical pixels);
    /// null restores the full input area. No-op elsewhere.
    /// </summary>
    public static void SetInputHole(Window window, PixelRect? hole)
    {
        var handle = window.TryGetPlatformHandle();
        if (!OperatingSystem.IsLinux() || handle is null || handle.Handle == IntPtr.Zero || handle.HandleDescriptor != "XID")
        {
            return;
        }

        try
        {
            X11.SetInputHole(handle.Handle, PixelSize.FromSize(window.ClientSize, window.RenderScaling), hole);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>
    /// Windows: the cursor's screen position in physical pixels, read straight from the OS. Unlike a pointer event's
    /// window-relative position, it stays correct while the window under the cursor is being moved. False elsewhere.
    /// </summary>
    public static bool TryGetCursorPosition(out PixelPoint position)
    {
        position = default;
        if (!OperatingSystem.IsWindows() || !Win32.GetCursorPos(out var p))
        {
            return false;
        }

        position = new PixelPoint(p.X, p.Y);
        return true;
    }

    private static partial class Win32
    {
        private const int GwlExStyle = -20;

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int X;
            public int Y;
        }

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetCursorPos(out Point point);
        private const long WsExTransparent = 0x00000020;
        private const long WsExToolWindow = 0x00000080;
        private const long WsExLayered = 0x00080000;
        private const long WsExNoActivate = 0x08000000;

        public static void SetClickThrough(IntPtr hwnd, bool enable)
        {
            var style = GetWindowLongPtrW(hwnd, GwlExStyle).ToInt64() | WsExToolWindow | WsExNoActivate;
            style = enable ? style | WsExLayered | WsExTransparent : style & ~WsExTransparent;
            SetWindowLongPtrW(hwnd, GwlExStyle, new IntPtr(style));
        }

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

        [LibraryImport("user32.dll")]
        private static partial IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    }

    private static partial class MacOs
    {
        private const long ScreenSaverWindowLevel = 1000;

        public static void SetClickThrough(IntPtr nsWindow, bool enable)
        {
            SendBool(nsWindow, SelRegisterName("setIgnoresMouseEvents:"), enable);
            SendLong(nsWindow, SelRegisterName("setLevel:"), ScreenSaverWindowLevel);
        }

        [LibraryImport("/usr/lib/libobjc.dylib", EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr SelRegisterName(string name);

        [LibraryImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static partial void SendBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool value);

        [LibraryImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static partial void SendLong(IntPtr receiver, IntPtr selector, long value);
    }

    private static partial class X11
    {
        private const int ShapeInput = 2;
        private const int ShapeSet = 0;
        private const int Unsorted = 0;
        private const int ShapeBounding = 0;

        public static void SetClickThrough(IntPtr window, bool enable)
        {
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
            {
                return;
            }

            try
            {
                if (enable)
                {
                    XShapeCombineRectangles(display, window, ShapeInput, 0, 0, IntPtr.Zero, 0, ShapeSet, Unsorted);
                }
                else
                {
                    // Reset the input shape to the window's bounding shape.
                    XShapeCombineMask(display, window, ShapeInput, 0, 0, IntPtr.Zero, ShapeSet);
                }

                XFlush(display);
            }
            finally
            {
                XCloseDisplay(display);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XRectangle
        {
            public short X;
            public short Y;
            public ushort Width;
            public ushort Height;
        }

        /// <summary>Input shape = the window minus the hole, as up to four rectangles around it.</summary>
        public static unsafe void SetInputHole(IntPtr window, PixelSize size, PixelRect? hole)
        {
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
            {
                return;
            }

            try
            {
                if (hole is not { } h)
                {
                    XShapeCombineMask(display, window, ShapeInput, 0, 0, IntPtr.Zero, ShapeSet);
                }
                else
                {
                    var rects = new XRectangle[4];
                    var n = 0;
                    void Add(int x, int y, int w, int hgt)
                    {
                        if (w > 0 && hgt > 0)
                        {
                            rects[n++] = new XRectangle { X = (short)x, Y = (short)y, Width = (ushort)w, Height = (ushort)hgt };
                        }
                    }

                    Add(0, 0, size.Width, h.Y);                                   // above
                    Add(0, h.Bottom, size.Width, size.Height - h.Bottom);          // below
                    Add(0, h.Y, h.X, h.Height);                                   // left
                    Add(h.Right, h.Y, size.Width - h.Right, h.Height);            // right
                    fixed (XRectangle* p = rects)
                    {
                        XShapeCombineRectangles(display, window, ShapeInput, 0, 0, (IntPtr)p, n, ShapeSet, Unsorted);
                    }
                }

                XFlush(display);
            }
            finally
            {
                XCloseDisplay(display);
            }
        }

        [LibraryImport("libX11.so.6")]
        private static partial IntPtr XOpenDisplay(IntPtr name);

        [LibraryImport("libX11.so.6")]
        private static partial int XCloseDisplay(IntPtr display);

        [LibraryImport("libX11.so.6")]
        private static partial int XFlush(IntPtr display);

        [LibraryImport("libXext.so.6")]
        private static partial void XShapeCombineRectangles(IntPtr display, IntPtr window, int kind, int x, int y, IntPtr rectangles, int count, int op, int ordering);

        [LibraryImport("libXext.so.6")]
        private static partial void XShapeCombineMask(IntPtr display, IntPtr window, int kind, int x, int y, IntPtr mask, int op);
    }
}
