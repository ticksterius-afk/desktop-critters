using System.Diagnostics;
using DesktopCritters.Config;
using DesktopCritters.Interop;
using DesktopCritters.Render;
using DesktopCritters.Sim;
using DesktopCritters.Tray;

namespace DesktopCritters;

/// <summary>
/// The running widget: one hidden message window for the tray, one layered surface
/// for the art, and a single-threaded loop that paces frames off a high-resolution
/// waitable timer.
///
/// Everything is on one thread on purpose. The workload per frame is small and
/// bounded, and a lock-free single-threaded design means no synchronisation cost,
/// no torn config reads, and a working set that stays where it starts.
/// </summary>
internal sealed class App : IDisposable
{
    private const string HostClassName = "DesktopCrittersHost";

    private const int CmdPauseResume = 1;
    private const int CmdReload = 2;
    private const int CmdOpenFolder = 3;
    private const int CmdLayerDesktop = 10;
    private const int CmdLayerBottom = 11;
    private const int CmdLayerTopmost = 12;
    private const int CmdExit = 20;

    private readonly string _configPath;
    private readonly string? _layerOverride;

    private AppConfig _config = new();
    private World? _world;
    private LayeredWindow? _surface;
    private TrayIcon? _tray;
    private FileSystemWatcher? _watcher;

    private IntPtr _host;
    private Win32.WndProc? _hostProc;      // rooted so the GC cannot collect the thunk
    private Win32.WndProc? _surfaceProc;

    private Win32.RECT _sandbox;
    private LayerMode _layer = LayerMode.Desktop;
    private bool _running = true;
    private bool _paused;
    private volatile bool _reloadRequested;
    private long _reloadDueTicks;
    private long _nextTickErrorTicks;

    public App(string configPath, string? layerOverride)
    {
        _configPath = configPath;
        _layerOverride = layerOverride;
    }

    public int Run()
    {
        LoadConfig();
        CreateHostWindow();

        _surfaceProc = SurfaceWndProc;
        _surface = new LayeredWindow(_surfaceProc);

        _tray = new TrayIcon(_host, "Desktop Critters");

        ApplyConfig(rebuildWorld: true);
        StartConfigWatcher();

        int exitCode = Loop();

        Dispose();
        return exitCode;
    }

    // -----------------------------------------------------------------------
    // configuration
    // -----------------------------------------------------------------------

    private void LoadConfig()
    {
        try
        {
            _config = File.Exists(_configPath) ? AppConfig.Load(_configPath) : new AppConfig();
        }
        catch (Exception ex)
        {
            // A broken config must not take the widget down -- fall back to defaults
            // and say so in the tooltip.
            Log($"config error: {ex.Message}");
            _config = new AppConfig();
        }

        _layer = _layerOverride is not null
            ? ParseLayer(_layerOverride)
            : _config.LayerMode;
    }

    private static LayerMode ParseLayer(string s) => s.ToLowerInvariant() switch
    {
        "topmost" or "top" => LayerMode.Topmost,
        "bottom" => LayerMode.Bottom,
        _ => LayerMode.Desktop,
    };

    /// <summary>Recomputes the sandbox, resizes the surface, and rebuilds the population.</summary>
    private void ApplyConfig(bool rebuildWorld)
    {
        _sandbox = ComputeSandbox(_config.Sandbox);

        int width = Math.Clamp(_sandbox.Width, 16, 8192);
        int height = Math.Clamp(_sandbox.Height, 16, 8192);

        string where = _surface!.Attach(_layer, _sandbox.Left, _sandbox.Top, width, height);

        if (rebuildWorld || _world is null)
            _world = new World(Paths.CreaturesDirectory, width, height);

        _world.Configure(_config, width, height);

        string tip = $"Desktop Critters — {_world.CritterCount} critter(s), {_world.TotalPoints:N0} points\n{where}";
        if (_world.Warnings.Count > 0)
            tip += "\n! " + string.Join("; ", _world.Warnings.Take(2));
        _tray?.SetTooltip(tip);

        foreach (var warning in _world.Warnings)
            Log("warning: " + warning);

        Log($"sandbox {width}x{height} at ({_sandbox.Left},{_sandbox.Top}) — {where}");
    }

    private static Win32.RECT ComputeSandbox(SandboxConfig s)
    {
        Win32.RECT rect;

        switch (s.Mode.ToLowerInvariant())
        {
            case "rect":
                rect = new Win32.RECT { Left = s.X, Top = s.Y, Right = s.X + s.Width, Bottom = s.Y + s.Height };
                break;
            case "virtual":
                rect = DesktopLayer.VirtualScreen();
                break;
            case "monitor":
                rect = DesktopLayer.MonitorRects(s.Monitor).Full;
                break;
            default:
                rect = DesktopLayer.MonitorRects(s.Monitor).Work;
                break;
        }

        int inset = Math.Max(0, s.Inset);
        rect.Left += inset; rect.Top += inset;
        rect.Right -= inset; rect.Bottom -= inset;

        if (rect.Width < 16) rect.Right = rect.Left + 16;
        if (rect.Height < 16) rect.Bottom = rect.Top + 16;
        return rect;
    }

    private void StartConfigWatcher()
    {
        if (!_config.WatchConfig)
            return;

        try
        {
            string? directory = Path.GetDirectoryName(_configPath);
            if (directory is null) return;

            _watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };

            // Editors write in bursts; the loop debounces rather than reloading per event.
            _watcher.Changed += (_, _) => _reloadRequested = true;
            _watcher.Created += (_, _) => _reloadRequested = true;
            _watcher.Renamed += (_, _) => _reloadRequested = true;
        }
        catch (Exception ex)
        {
            Log($"config watch unavailable: {ex.Message}");
        }
    }

    private void Reload()
    {
        _reloadRequested = false;
        LoadConfig();
        ApplyConfig(rebuildWorld: true);
    }

    // -----------------------------------------------------------------------
    // windows
    // -----------------------------------------------------------------------

    private void CreateHostWindow()
    {
        _hostProc = HostWndProc;
        WindowClass.Register(HostClassName, _hostProc);

        _host = Win32.CreateWindowEx(
            Win32.WS_EX_TOOLWINDOW, HostClassName, "Desktop Critters",
            Win32.WS_POPUP, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, Win32.GetModuleHandle(null), IntPtr.Zero);

        if (_host == IntPtr.Zero)
            throw new InvalidOperationException("could not create the host window");
    }

    private IntPtr SurfaceWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        => Win32.DefWindowProc(hwnd, msg, wParam, lParam);

    private IntPtr HostWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case TrayIcon.CallbackMessage:
            {
                int mouse = (int)(lParam.ToInt64() & 0xFFFF);
                if (mouse is Win32.WM_RBUTTONUP or Win32.WM_LBUTTONUP)
                    ShowTrayMenu();
                return IntPtr.Zero;
            }

            case Win32.WM_COMMAND:
                HandleCommand((int)(wParam.ToInt64() & 0xFFFF));
                return IntPtr.Zero;

            case Win32.WM_DISPLAYCHANGE:
            case Win32.WM_SETTINGCHANGE:
                // Resolution, DPI, or taskbar changes move the work area under us.
                _reloadRequested = true;
                return IntPtr.Zero;

            case Win32.WM_DESTROY:
                Log("host window destroyed; shutting down");
                _running = false;
                Win32.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowTrayMenu()
    {
        IntPtr menu = Win32.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            Win32.AppendMenu(menu, Win32.MF_STRING, (UIntPtr)CmdPauseResume, _paused ? "Resume" : "Pause");
            Win32.AppendMenu(menu, Win32.MF_SEPARATOR, UIntPtr.Zero, null);

            IntPtr layers = Win32.CreatePopupMenu();
            Win32.AppendMenu(layers, Win32.MF_STRING | Check(_layer == LayerMode.Desktop), (UIntPtr)CmdLayerDesktop, "On the desktop");
            Win32.AppendMenu(layers, Win32.MF_STRING | Check(_layer == LayerMode.Bottom), (UIntPtr)CmdLayerBottom, "Behind all windows");
            Win32.AppendMenu(layers, Win32.MF_STRING | Check(_layer == LayerMode.Topmost), (UIntPtr)CmdLayerTopmost, "Always on top");
            Win32.AppendMenu(menu, Win32.MF_POPUP, (UIntPtr)(ulong)layers.ToInt64(), "Layer");

            Win32.AppendMenu(menu, Win32.MF_STRING, (UIntPtr)CmdReload, "Reload config");
            Win32.AppendMenu(menu, Win32.MF_STRING, (UIntPtr)CmdOpenFolder, "Open config folder");
            Win32.AppendMenu(menu, Win32.MF_SEPARATOR, UIntPtr.Zero, null);
            Win32.AppendMenu(menu, Win32.MF_STRING, (UIntPtr)CmdExit, "Exit");

            _tray?.ShowMenu(menu);
        }
        finally
        {
            Win32.DestroyMenu(menu);
        }

        static uint Check(bool on) => on ? Win32.MF_CHECKED : Win32.MF_UNCHECKED;
    }

    private void HandleCommand(int id)
    {
        switch (id)
        {
            case CmdPauseResume:
                _paused = !_paused;
                break;

            case CmdReload:
                Reload();
                break;

            case CmdOpenFolder:
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Paths.Root}\"") { UseShellExecute = true });
                break;

            case CmdLayerDesktop: SwitchLayer(LayerMode.Desktop); break;
            case CmdLayerBottom: SwitchLayer(LayerMode.Bottom); break;
            case CmdLayerTopmost: SwitchLayer(LayerMode.Topmost); break;

            case CmdExit:
                Log("exit chosen from the tray menu");
                _running = false;
                Win32.PostQuitMessage(0);
                break;
        }
    }

    private void SwitchLayer(LayerMode mode)
    {
        if (_layer == mode) return;
        _layer = mode;
        _config.Layer = mode.ToString().ToLowerInvariant();
        ApplyConfig(rebuildWorld: false);
    }

    // -----------------------------------------------------------------------
    // main loop
    // -----------------------------------------------------------------------

    private int Loop()
    {
        // A high-resolution waitable timer gives sub-millisecond frame pacing without
        // calling timeBeginPeriod, which would raise the timer resolution (and the
        // power draw) for the entire system.
        IntPtr timer = Win32.CreateWaitableTimerEx(IntPtr.Zero, null,
            Win32.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Win32.TIMER_ALL_ACCESS);

        if (timer == IntPtr.Zero)   // pre-1803 fallback: ordinary timer, coarser pacing
            timer = Win32.CreateWaitableTimerEx(IntPtr.Zero, null, 0, Win32.TIMER_ALL_ACCESS);

        var handles = timer != IntPtr.Zero ? new[] { timer } : null;
        uint handleCount = timer != IntPtr.Zero ? 1u : 0u;

        long frequency = Stopwatch.Frequency;
        long lastTick = Stopwatch.GetTimestamp();
        long nextFrame = lastTick;
        int zOrderCountdown = 0;

        while (_running)
        {
            int fps = Math.Clamp(_config.Fps, 1, 240);
            long frameTicks = frequency / fps;

            bool idle = _paused || (_config.PauseWhenFullscreen && DesktopLayer.DesktopIsObscured());
            long now = Stopwatch.GetTimestamp();

            // While idle, wake ten times a second just to notice the state changing.
            long waitTicks = idle ? frequency / 10 : nextFrame - now;

            if (waitTicks > frequency / 2000)   // anything under ~0.5ms is not worth sleeping for
            {
                WaitFor(handles, handleCount, timer, waitTicks, frequency);
                PumpMessages();
                continue;
            }

            PumpMessages();
            if (!_running) break;

            if (_reloadRequested && now >= _reloadDueTicks)
            {
                // Debounce: editors often fire several writes for one save.
                _reloadDueTicks = now + frequency / 4;
                try
                {
                    Reload();
                }
                catch (Exception ex)
                {
                    _reloadRequested = false;
                    Log($"reload failed, keeping the previous world: {ex.Message}");
                }
                lastTick = Stopwatch.GetTimestamp();
                nextFrame = lastTick;
                continue;
            }

            if (idle)
            {
                lastTick = now;
                nextFrame = now;
                continue;
            }

            double dt = (now - lastTick) / (double)frequency;
            lastTick = now;

            try
            {
                Tick(dt);
            }
            catch (Exception ex)
            {
                // One bad frame should never end the session; log once per minute
                // at most so a persistent fault does not flood the log.
                if (now >= _nextTickErrorTicks)
                {
                    _nextTickErrorTicks = now + frequency * 60;
                    Log($"frame failed: {ex}");
                }
            }

            nextFrame += frameTicks;
            if (nextFrame < now - frameTicks * 3)
                nextFrame = now + frameTicks;   // resync after a stall instead of catching up in a burst

            // Explorer restarts and full-screen apps can shuffle us out of place.
            if (--zOrderCountdown <= 0)
            {
                zOrderCountdown = fps * 5;
                _surface?.ReassertZOrder(_layer);
            }
        }

        if (timer != IntPtr.Zero)
            Win32.CloseHandle(timer);

        return 0;
    }

    private static void WaitFor(IntPtr[]? handles, uint handleCount, IntPtr timer, long waitTicks, long frequency)
    {
        if (handles is not null)
        {
            // Negative due time means "relative", in 100ns units.
            long due = -(waitTicks * 10_000_000 / frequency);
            if (due >= 0) due = -1;
            Win32.SetWaitableTimerEx(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0);
            Win32.MsgWaitForMultipleObjectsEx(handleCount, handles, uint.MaxValue,
                Win32.QS_ALLINPUT, Win32.MWMO_INPUTAVAILABLE);
        }
        else
        {
            uint ms = (uint)Math.Max(1, waitTicks * 1000 / frequency);
            Win32.MsgWaitForMultipleObjectsEx(0, null, ms, Win32.QS_ALLINPUT, Win32.MWMO_INPUTAVAILABLE);
        }
    }

    private void PumpMessages()
    {
        while (Win32.PeekMessage(out var msg, IntPtr.Zero, 0, 0, Win32.PM_REMOVE))
        {
            if (msg.message == Win32.WM_QUIT)
            {
                Log($"WM_QUIT received (wParam={msg.wParam}); shutting down");
                _running = false;
                return;
            }
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }
    }

    private void Tick(double dt)
    {
        if (_world is null || _surface is null || _surface.Bits == IntPtr.Zero)
            return;

        _world.Step(dt);
        var dirty = _world.Render(_surface.Bits);
        _surface.Present(dirty);
    }

    // -----------------------------------------------------------------------

    private static void Log(string message) => Console.Error.WriteLine($"[critters] {message}");

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;

        _tray?.Dispose();
        _tray = null;

        _surface?.Dispose();
        _surface = null;

        if (_host != IntPtr.Zero)
        {
            Win32.DestroyWindow(_host);
            _host = IntPtr.Zero;
        }
    }
}
