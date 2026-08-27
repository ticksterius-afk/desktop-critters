namespace DesktopCritters.Interop;

public enum LayerMode
{
    /// <summary>Sits on the wallpaper layer: under normal windows, over the wallpaper.</summary>
    Desktop,
    /// <summary>Free-floating window pinned to the bottom of the z-order (over icons, under windows).</summary>
    Bottom,
    /// <summary>Always on top of everything. Mostly useful for demos and for checking the art.</summary>
    Topmost,
}

/// <summary>
/// Finds the shell window that paints the wallpaper so the widget can be parented
/// into it.
///
/// Why this matters for the "icons hidden" requirement: Explorer's desktop is a
/// stack of three windows -- <c>Progman</c>, a <c>SHELLDLL_DefView</c> child that
/// hosts the icon list view, and a <c>WorkerW</c> that paints the wallpaper behind
/// it. Turning off "Show desktop icons" hides <c>SHELLDLL_DefView</c> and nothing
/// else. So anything parented to the icon view vanishes with the icons, while
/// anything parented to <c>WorkerW</c> (or to <c>Progman</c> itself) keeps
/// painting. We deliberately target those two, never the icon view.
/// </summary>
internal static class DesktopLayer
{
    /// <summary>
    /// Undocumented Progman message that asks Explorer to spawn the wallpaper
    /// WorkerW. On a fresh login the window often does not exist until something
    /// asks for it.
    /// </summary>
    private const uint WM_SPAWN_WORKERW = 0x052C;

    public readonly record struct Host(IntPtr Handle, string Description);

    /// <summary>
    /// Returns the window the widget should parent itself into, or a zero handle
    /// if the shell is not in a state we recognise (in which case the caller
    /// should fall back to <see cref="LayerMode.Bottom"/>).
    /// </summary>
    public static Host FindWallpaperHost()
    {
        var progman = Win32.FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
            return new Host(IntPtr.Zero, "no Progman (Explorer not running?)");

        // Nudge Explorer into creating the wallpaper WorkerW. Timeout rather than
        // block: a wedged Explorer must not wedge the widget's startup.
        Win32.SendMessageTimeout(progman, WM_SPAWN_WORKERW, IntPtr.Zero, IntPtr.Zero,
            Win32.SMTO_NORMAL, 1000, out _);

        var worker = FindWorkerWBehindIcons();
        if (worker != IntPtr.Zero)
            return new Host(worker, "WorkerW (wallpaper layer)");

        // Older shells (and some Windows 11 builds after certain updates) keep
        // SHELLDLL_DefView parented directly to Progman with no sibling WorkerW.
        // Progman itself is never hidden by the icon toggle, so it is a safe host.
        return new Host(progman, "Progman (wallpaper layer, no WorkerW)");
    }

    /// <summary>
    /// The wallpaper WorkerW is the top-level <c>WorkerW</c> that is the immediate
    /// z-order sibling *after* whichever top-level window owns SHELLDLL_DefView.
    /// Enumerating for "a WorkerW with no DefView child" is the usual shortcut but
    /// misfires when Explorer has several WorkerWs, so anchor off the icon view.
    /// </summary>
    private static IntPtr FindWorkerWBehindIcons()
    {
        IntPtr result = IntPtr.Zero;

        Win32.EnumWindows((hwnd, _) =>
        {
            var defView = Win32.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView == IntPtr.Zero)
                return true; // keep looking

            var sibling = Win32.FindWindowEx(IntPtr.Zero, hwnd, "WorkerW", null);
            if (sibling != IntPtr.Zero)
            {
                result = sibling;
                return false; // stop enumerating
            }
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>
    /// True when something is genuinely covering the whole screen, so the render
    /// loop can idle instead of animating into a surface nobody can see.
    ///
    /// Deliberately does NOT trust <c>SHQueryUserNotificationState</c>'s
    /// D3D-fullscreen and busy states on their own: animated-wallpaper apps
    /// (Wallpaper Engine, Lively, and friends) run a fullscreen D3D surface
    /// permanently, and reporting those as "obscured" would freeze the widget
    /// forever on exactly the desktops it is most wanted on. Instead, ask the
    /// narrower question -- is a real foreground window covering a whole monitor?
    /// </summary>
    public static bool DesktopIsObscured()
    {
        // Presentation mode is an explicit "do not disturb" from the user, so honour it.
        if (Win32.SHQueryUserNotificationState(out int state) == 0 && state == Win32.QUNS_PRESENTATION_MODE)
            return true;

        var foreground = Win32.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return false;

        // The shell's own windows being in front means the desktop is showing, not hidden.
        string cls = ClassNameOf(foreground);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return false;

        if (!Win32.GetWindowRect(foreground, out var window))
            return false;

        var monitor = Win32.MonitorFromWindow(foreground, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new Win32.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        if (monitor == IntPtr.Zero || !Win32.GetMonitorInfo(monitor, ref info))
            return false;

        return window.Left <= info.rcMonitor.Left
            && window.Top <= info.rcMonitor.Top
            && window.Right >= info.rcMonitor.Right
            && window.Bottom >= info.rcMonitor.Bottom;
    }

    private static string ClassNameOf(IntPtr hwnd)
    {
        var buffer = new System.Text.StringBuilder(256);
        int written = Win32.GetClassName(hwnd, buffer, buffer.Capacity);
        return written > 0 ? buffer.ToString() : string.Empty;
    }

    /// <summary>Bounding box of every monitor, in virtual-screen coordinates.</summary>
    public static Win32.RECT VirtualScreen()
    {
        int x = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        int y = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        return new Win32.RECT
        {
            Left = x,
            Top = y,
            Right = x + Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN),
            Bottom = y + Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN),
        };
    }

    /// <summary>
    /// Work area (screen minus taskbar) and full bounds of a monitor.
    /// <paramref name="monitor"/> accepts "primary", "cursor", or a 0-based index.
    /// </summary>
    public static (Win32.RECT Work, Win32.RECT Full) MonitorRects(string monitor)
    {
        IntPtr h;

        if (monitor.Equals("cursor", StringComparison.OrdinalIgnoreCase))
        {
            Win32.GetCursorPos(out var pt);
            h = Win32.MonitorFromPoint(pt, Win32.MONITOR_DEFAULTTONEAREST);
        }
        else if (int.TryParse(monitor, out int index))
        {
            h = MonitorByIndex(index);
        }
        else
        {
            h = Win32.MonitorFromPoint(new Win32.POINT(0, 0), Win32.MONITOR_DEFAULTTOPRIMARY);
        }

        var mi = new Win32.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        if (h != IntPtr.Zero && Win32.GetMonitorInfo(h, ref mi))
            return (mi.rcWork, mi.rcMonitor);

        var vs = VirtualScreen();
        return (vs, vs);
    }

    private static IntPtr MonitorByIndex(int index)
    {
        var handles = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hm, _, _, _) => { handles.Add(hm); return true; }, IntPtr.Zero);

        if (handles.Count == 0)
            return IntPtr.Zero;

        return handles[Math.Clamp(index, 0, handles.Count - 1)];
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
}
