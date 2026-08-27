using System.Runtime.InteropServices;
using DesktopCritters.Interop;

namespace DesktopCritters.Tray;

/// <summary>
/// The notification-area icon and its context menu, straight on Shell_NotifyIcon.
///
/// The widget is click-through by design, so the tray is the only place a user can
/// reach it. Commands arrive as WM_COMMAND on the host window; this class only owns
/// the icon and the popup.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    public const int CallbackMessage = Win32.WM_APP + 1;

    private readonly IntPtr _host;
    private IntPtr _icon;
    private bool _added;

    public TrayIcon(IntPtr hostWindow, string tooltip)
    {
        _host = hostWindow;
        _icon = CreateFishIcon();

        var data = NewData(tooltip);
        _added = Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref data);
    }

    public void SetTooltip(string tooltip)
    {
        if (!_added) return;
        var data = NewData(tooltip);
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref data);
    }

    private Win32.NOTIFYICONDATA NewData(string tooltip) => new()
    {
        cbSize = (uint)Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
        hWnd = _host,
        uID = 1,
        uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    /// <summary>
    /// Pops the menu at the cursor and blocks until an item is chosen. The
    /// SetForegroundWindow call is the documented workaround for menus that
    /// otherwise refuse to dismiss when the user clicks elsewhere.
    /// </summary>
    public void ShowMenu(IntPtr menu)
    {
        Win32.GetCursorPos(out var cursor);
        Win32.SetForegroundWindow(_host);
        Win32.TrackPopupMenuEx(menu, Win32.TPM_RIGHTBUTTON | Win32.TPM_LEFTALIGN,
            cursor.X, cursor.Y, _host, IntPtr.Zero);
    }

    /// <summary>
    /// Draws the tray icon at run time. Keeps the repo free of binary assets and
    /// means the icon scales with whatever size the shell asks for next.
    /// </summary>
    private static unsafe IntPtr CreateFishIcon()
    {
        const int size = 32;

        var header = new Win32.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size,   // top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = Win32.BI_RGB,
        };

        IntPtr dc = Win32.CreateCompatibleDC(IntPtr.Zero);
        IntPtr colour = Win32.CreateDIBSection(dc, ref header, Win32.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);

        if (colour != IntPtr.Zero)
        {
            uint* pixels = (uint*)bits;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    double nx = (x - 13.0) / 11.0;
                    double ny = (y - 16.0) / 6.5;

                    bool body = nx * nx + ny * ny <= 1.0;
                    bool tail = x >= 21 && x <= 30 && Math.Abs(y - 16.0) <= (x - 21) * 0.85;
                    bool eye = (x - 8.0) * (x - 8.0) + (y - 14.0) * (y - 14.0) <= 3.0;

                    uint argb = 0x00000000;
                    if (eye && body) argb = 0xFF0B2A3A;
                    else if (body || tail) argb = 0xFF8FE6FF;

                    pixels[y * size + x] = argb;   // straight alpha; icons are not premultiplied
                }
            }
        }

        // A 32bpp colour bitmap carries its own alpha, but CreateIconIndirect still
        // wants a mask bitmap to exist. All-zero means "fully opaque, use the alpha".
        var maskBits = new byte[size * size / 8];
        IntPtr mask;
        fixed (byte* m = maskBits)
            mask = Win32.CreateBitmap(size, size, 1, 1, (IntPtr)m);

        var info = new Win32.ICONINFO { fIcon = true, hbmMask = mask, hbmColor = colour };
        IntPtr icon = Win32.CreateIconIndirect(ref info);

        if (mask != IntPtr.Zero) Win32.DeleteObject(mask);
        if (colour != IntPtr.Zero) Win32.DeleteObject(colour);
        if (dc != IntPtr.Zero) Win32.DeleteDC(dc);

        return icon;
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = NewData(string.Empty);
            Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref data);
            _added = false;
        }
        if (_icon != IntPtr.Zero)
        {
            Win32.DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
    }
}
