using System.Runtime.InteropServices;
using DesktopCritters.Interop;

namespace DesktopCritters.Render;

/// <summary>Registers window classes and keeps their WndProc delegates alive.</summary>
internal static class WindowClass
{
    private static readonly List<Win32.WndProc> Pinned = new();
    private static readonly HashSet<string> Registered = new();

    public static void Register(string name, Win32.WndProc proc)
    {
        if (!Registered.Add(name))
            return;

        // The delegate is handed to native code; without a managed root the GC would
        // collect it and the first message would crash the process.
        Pinned.Add(proc);

        var wc = new Win32.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = Win32.GetModuleHandle(null),
            hCursor = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW),
            lpszClassName = name,
        };

        if (Win32.RegisterClassEx(ref wc) == 0)
            throw new InvalidOperationException($"RegisterClassEx('{name}') failed: {Marshal.GetLastWin32Error()}");
    }
}

/// <summary>
/// A transparent, click-through surface the widget paints into.
///
/// Uses a layered window fed by <c>UpdateLayeredWindow</c> rather than a
/// WM_PAINT/D3D swapchain: per-pixel alpha over the wallpaper comes for free, no
/// GPU device to lose on session switch, and the widget owns the pixel memory
/// directly so the renderer can write straight into the DIB with no copy.
/// </summary>
internal sealed class LayeredWindow : IDisposable
{
    private const string ClassName = "DesktopCrittersSurface";

    public IntPtr Handle { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Pointer to top-down 32bpp premultiplied BGRA pixels.</summary>
    public IntPtr Bits { get; private set; }

    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _oldBitmap;
    private IntPtr _parent = IntPtr.Zero;

    private int _screenX, _screenY;
    private bool _needFullPresent = true;
    private bool _indirectUnavailable;
    private bool _presentFailureReported;

    /// <summary>Cached so the per-frame present does no reflection.</summary>
    private static readonly uint UlwInfoSize = (uint)Marshal.SizeOf<Win32.UPDATELAYEREDWINDOWINFO>();

    /// <summary>Diagnostics: how the last present went, surfaced in the tray tooltip.</summary>
    public string PresentMode { get; private set; } = "full";

    public LayeredWindow(Win32.WndProc wndProc)
    {
        WindowClass.Register(ClassName, wndProc);

        Handle = Win32.CreateWindowEx(
            Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE,
            ClassName, "Desktop Critters",
            Win32.WS_POPUP,
            0, 0, 16, 16,
            IntPtr.Zero, IntPtr.Zero, Win32.GetModuleHandle(null), IntPtr.Zero);

        if (Handle == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
    }

    /// <summary>Allocates (or reallocates) the pixel surface.</summary>
    public void SetSurfaceSize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height && _dib != IntPtr.Zero)
            return;

        ReleaseSurface();

        Width = width;
        Height = height;

        // Negative height => top-down rows, which matches how the canvas indexes.
        var header = new Win32.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = Win32.BI_RGB,
        };

        _memDc = Win32.CreateCompatibleDC(IntPtr.Zero);
        _dib = Win32.CreateDIBSection(_memDc, ref header, Win32.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero)
            throw new InvalidOperationException($"CreateDIBSection failed: {Marshal.GetLastWin32Error()}");

        Bits = bits;
        _oldBitmap = Win32.SelectObject(_memDc, _dib);
        _needFullPresent = true;
    }

    /// <summary>
    /// Places the surface at a virtual-screen rectangle and puts it on the requested
    /// layer. Returns a human-readable description of where it ended up.
    /// </summary>
    public string Attach(LayerMode mode, int screenX, int screenY, int width, int height)
    {
        SetSurfaceSize(width, height);
        _screenX = screenX;
        _screenY = screenY;

        string where;
        if (mode == LayerMode.Desktop)
        {
            var host = DesktopLayer.FindWallpaperHost();
            if (host.Handle != IntPtr.Zero)
            {
                // A child of the wallpaper host survives the "hide desktop icons"
                // toggle, because that toggle only hides the icon view sibling.
                SetStyleForParenting(true);
                Win32.SetParent(Handle, host.Handle);
                _parent = host.Handle;
                where = $"desktop layer -> {host.Description}";
            }
            else
            {
                _parent = IntPtr.Zero;
                SetStyleForParenting(false);
                Win32.SetWindowPos(Handle, Win32.HWND_BOTTOM, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
                where = "bottom of z-order (wallpaper host not found)";
            }
        }
        else
        {
            if (_parent != IntPtr.Zero)
            {
                Win32.SetParent(Handle, IntPtr.Zero);
                _parent = IntPtr.Zero;
            }
            SetStyleForParenting(false);

            var after = mode == LayerMode.Topmost ? Win32.HWND_TOPMOST : Win32.HWND_BOTTOM;
            Win32.SetWindowPos(Handle, after, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            where = mode == LayerMode.Topmost ? "always on top" : "bottom of z-order";
        }

        MoveTo(screenX, screenY, width, height);
        Win32.ShowWindow(Handle, Win32.SW_SHOWNOACTIVATE);
        _needFullPresent = true;
        return where;
    }

    private void SetStyleForParenting(bool asChild)
    {
        int style = asChild
            ? Win32.WS_CHILD | Win32.WS_VISIBLE
            : Win32.WS_POPUP;
        SetWindowLong(Handle, GWL_STYLE, style);
    }

    public void MoveTo(int screenX, int screenY, int width, int height)
    {
        _screenX = screenX;
        _screenY = screenY;
        SetSurfaceSize(width, height);

        var pt = DestinationPoint();
        Win32.SetWindowPos(Handle, IntPtr.Zero, pt.X, pt.Y, width, height,
            Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER | Win32.SWP_SHOWWINDOW);
        _needFullPresent = true;
    }

    /// <summary>
    /// Child windows are positioned in their parent's client space, top-level ones in
    /// virtual-screen space. Convert once, here, so callers only ever think in screen
    /// coordinates.
    /// </summary>
    private Win32.POINT DestinationPoint()
    {
        var pt = new Win32.POINT(_screenX, _screenY);
        if (_parent != IntPtr.Zero)
            Win32.ScreenToClient(_parent, ref pt);
        return pt;
    }

    /// <summary>Pushes the DIB to the compositor, blitting only <paramref name="dirty"/> when possible.</summary>
    public unsafe void Present(DirtyRect dirty)
    {
        if (_dib == IntPtr.Zero)
            return;

        var size = new Win32.SIZE(Width, Height);
        var srcPoint = new Win32.POINT(0, 0);
        var blend = new Win32.BLENDFUNCTION
        {
            BlendOp = Win32.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = Win32.AC_SRC_ALPHA,
        };

        bool full = _needFullPresent || dirty.IsEmpty || _indirectUnavailable;

        if (!full)
        {
            var box = new Win32.RECT { Left = dirty.X0, Top = dirty.Y0, Right = dirty.X1, Bottom = dirty.Y1 };
            var info = new Win32.UPDATELAYEREDWINDOWINFO
            {
                cbSize = UlwInfoSize,
                hdcDst = IntPtr.Zero,          // NULL => desktop DC
                pptDst = IntPtr.Zero,          // NULL => leave the window where it is
                psize = (IntPtr)(&size),
                hdcSrc = _memDc,
                pptSrc = (IntPtr)(&srcPoint),
                crKey = 0,
                pblend = (IntPtr)(&blend),
                dwFlags = Win32.ULW_ALPHA,
                prcDirty = (IntPtr)(&box),
            };

            if (Win32.UpdateLayeredWindowIndirect(Handle, ref info))
            {
                PresentMode = "dirty-rect";
                return;
            }

            // Some shells reject the indirect form for child windows. Fall back
            // permanently rather than paying a failed syscall every frame -- but say
            // so, because full-surface presents on a large sandbox are the single
            // biggest cost in the whole widget.
            _indirectUnavailable = true;
            PresentMode = "full-surface";
            Console.Error.WriteLine(
                $"[critters] dirty-rect present unavailable (error {Marshal.GetLastWin32Error()}); " +
                $"falling back to full {Width}x{Height} presents");
        }

        var dst = DestinationPoint();
        bool ok = Win32.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size,
            _memDc, ref srcPoint, 0, ref blend, Win32.ULW_ALPHA);

        if (!ok && !_presentFailureReported)
        {
            // Report once: if this fails every frame, a per-frame log would be worse
            // than the bug.
            _presentFailureReported = true;
            Console.Error.WriteLine(
                $"[critters] UpdateLayeredWindow failed: {Marshal.GetLastWin32Error()} " +
                $"(size {Width}x{Height}, parented={_parent != IntPtr.Zero})");
        }

        _needFullPresent = false;
    }

    public void Hide() => Win32.ShowWindow(Handle, Win32.SW_HIDE);

    public void Show()
    {
        Win32.ShowWindow(Handle, Win32.SW_SHOWNOACTIVATE);
        _needFullPresent = true;
    }

    /// <summary>
    /// Forces the next present to push the entire surface rather than a dirty rectangle.
    ///
    /// A layered *child* window's pixels live in its parent's redirection surface, so a
    /// full repaint of the desktop -- toggling icons, changing wallpaper, Explorer
    /// restarting -- can erase them. Ordinary frames only re-blit the small rectangle
    /// around the creature, which is not enough to heal that; and while the widget is
    /// paused nothing re-blits at all. Calling this on a slow heartbeat restores the
    /// creature within a couple of seconds of any such repaint.
    /// </summary>
    public void Refresh()
    {
        if (_dib == IntPtr.Zero) return;
        _needFullPresent = true;
        Present(default);
    }

    /// <summary>
    /// Re-checks that we are still attached to the right window and still above the
    /// icon view. Explorer restarts, wallpaper apps, and display changes can all move
    /// the wallpaper host out from under us.
    /// </summary>
    public void ReassertZOrder(LayerMode mode)
    {
        if (mode != LayerMode.Desktop)
        {
            var target = mode == LayerMode.Topmost ? Win32.HWND_TOPMOST : Win32.HWND_BOTTOM;
            Win32.SetWindowPos(Handle, target, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            return;
        }

        // Has the shell handed wallpaper duty to a different window since we attached?
        var host = DesktopLayer.FindWallpaperHost(spawnWorkerW: false);
        if (host.Handle != IntPtr.Zero && host.Handle != _parent)
        {
            Console.Error.WriteLine($"[critters] wallpaper host changed -> {host.Description}; re-attaching");
            Attach(mode, _screenX, _screenY, Width, Height);
            return;
        }

        if (_parent == IntPtr.Zero || !Win32.IsWindow(_parent))
        {
            Attach(mode, _screenX, _screenY, Width, Height);
            return;
        }

        // Stay above SHELLDLL_DefView. Only reorder when we are not already on top,
        // because SetWindowPos on the desktop forces a repaint.
        if (Win32.GetTopWindow(_parent) != Handle)
        {
            Win32.SetWindowPos(Handle, Win32.HWND_TOP, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            _needFullPresent = true;
        }
    }

    private void ReleaseSurface()
    {
        if (_memDc != IntPtr.Zero)
        {
            if (_oldBitmap != IntPtr.Zero) Win32.SelectObject(_memDc, _oldBitmap);
            Win32.DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
            _oldBitmap = IntPtr.Zero;
        }
        if (_dib != IntPtr.Zero)
        {
            Win32.DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }
        Bits = IntPtr.Zero;
    }

    public void Dispose()
    {
        ReleaseSurface();

        if (Handle != IntPtr.Zero)
        {
            // Detach from Explorer's window before destroying, so a shutdown can never
            // leave a stale child hanging in the desktop's tree.
            if (_parent != IntPtr.Zero)
            {
                Win32.ShowWindow(Handle, Win32.SW_HIDE);
                Win32.SetParent(Handle, IntPtr.Zero);
                _parent = IntPtr.Zero;
            }

            Win32.DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }
    }

    private const int GWL_STYLE = -16;

    private static IntPtr SetWindowLong(IntPtr hWnd, int index, int value)
        => IntPtr.Size == 8
            ? SetWindowLongPtrW(hWnd, index, new IntPtr(value))
            : new IntPtr(SetWindowLongW(hWnd, index, value));

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);
}
