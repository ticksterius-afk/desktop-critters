using System.Globalization;
using System.Runtime.InteropServices;
using DesktopCritters.Config;
using DesktopCritters.Interop;
using DesktopCritters.Render;
using DesktopCritters.Sim;

namespace DesktopCritters;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // The exe is a WinExe so it never flashes a console window, but when it is
        // launched from a terminal we borrow that terminal for diagnostics.
        if (args.Length > 0)
            Win32.AttachConsole(Win32.ATTACH_PARENT_PROCESS);

        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"desktop-critters: {ex.Message}");
            Console.Error.WriteLine("Try --help.");
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.Out.WriteLine(Options.Usage);
            return 0;
        }

        try
        {
            Paths.EnsureSeeded();
            string configPath = options.ConfigPath ?? Paths.ConfigFile;

            if (options.PreviewDirectory is not null)
                return Preview.Run(configPath, options);   // headless, no window to duplicate

            // One widget per session. Without this, every launch adds another critter
            // to the desktop and they accumulate invisibly across restarts.
            using var single = new Mutex(initiallyOwned: true, @"Local\DesktopCritters.SingleInstance", out bool isFirst);
            if (!isFirst)
            {
                Console.Error.WriteLine(
                    "Desktop Critters is already running - look for the fish in the notification area. " +
                    "Use its Exit item first if you want to restart it.");
                return 3;
            }

            return new App(configPath, options.Layer).Run();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"desktop-critters: {ex}");
            return 1;
        }
    }
}

internal sealed class Options
{
    public string? ConfigPath;
    public string? Layer;
    public string? PreviewDirectory;
    public string? MotionOverride;
    public int Frames = 12;
    public int Width = 640;
    public int Height = 480;
    public double? Fps;
    public uint? Background = 0x111820;
    public bool ShowHelp;

    public const string Usage = """
        Desktop Critters -- formula-driven animals that live on your desktop.

          DesktopCritters.exe                     run the widget (tray icon controls it)
          DesktopCritters.exe --layer topmost     override the layer for this run

        Options
          --config <path>      use a specific config.json
          --layer <mode>       desktop | bottom | topmost
          --help               this text

        Preview (renders frames to PNG, no window -- useful for tuning formulas)
          --preview <dir>      write frames here and exit
          --frames <n>         how many frames to render      (default 12)
          --size <WxH>         preview canvas size             (default 640x480)
          --fps <n>            simulation step rate            (default: config fps)
          --motion <mode>      wander | path | still           (default still)
          --bg <hex|none>      composite over this colour      (default 111820)
        """;

    public static Options Parse(string[] args)
    {
        var o = new Options();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            string Next(string name) => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"{name} needs a value");

            switch (arg)
            {
                case "--help" or "-h" or "/?": o.ShowHelp = true; break;
                case "--config": o.ConfigPath = Next(arg); break;
                case "--layer": o.Layer = Next(arg); break;
                case "--preview": o.PreviewDirectory = Next(arg); break;
                case "--motion": o.MotionOverride = Next(arg); break;
                case "--frames": o.Frames = ParseInt(Next(arg), arg); break;
                case "--fps": o.Fps = ParseDouble(Next(arg), arg); break;

                case "--size":
                {
                    var parts = Next(arg).Split('x', 'X');
                    if (parts.Length != 2)
                        throw new ArgumentException("--size expects WxH, e.g. 800x600");
                    o.Width = ParseInt(parts[0], arg);
                    o.Height = ParseInt(parts[1], arg);
                    break;
                }

                case "--bg":
                {
                    string value = Next(arg);
                    o.Background = value.Equals("none", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : uint.Parse(value.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    break;
                }

                default:
                    throw new ArgumentException($"unknown option '{arg}'");
            }
        }

        return o;
    }

    private static int ParseInt(string s, string name)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v : throw new ArgumentException($"{name} expects a number, got '{s}'");

    private static double ParseDouble(string s, string name)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v : throw new ArgumentException($"{name} expects a number, got '{s}'");
}

/// <summary>
/// Headless frame dump. Runs the exact same world and canvas as the widget, just
/// against a plain memory buffer instead of a layered window -- so what you see in
/// a preview PNG is what lands on the desktop.
/// </summary>
internal static class Preview
{
    public static unsafe int Run(string configPath, Options options)
    {
        var config = File.Exists(configPath) ? AppConfig.Load(configPath) : new AppConfig();

        // Default to parking the creature in the middle: a preview is usually about
        // checking the body formula, not the swim path.
        string motion = options.MotionOverride ?? "still";
        foreach (var critter in config.Critters)
            critter.Motion.Mode = motion;

        int width = Math.Clamp(options.Width, 16, 8192);
        int height = Math.Clamp(options.Height, 16, 8192);

        var world = new World(Paths.CreaturesDirectory, width, height);
        world.Configure(config, width, height);

        foreach (var warning in world.Warnings)
            Console.Error.WriteLine($"[critters] warning: {warning}");

        foreach (var line in world.Describe())
            Console.Error.WriteLine($"[critters] {line}");

        Directory.CreateDirectory(options.PreviewDirectory!);

        nuint bytes = (nuint)((long)width * height * 4);
        void* buffer = NativeMemory.AllocZeroed(bytes);

        try
        {
            double fps = options.Fps ?? Math.Clamp(config.Fps, 1, 240);
            double dt = 1.0 / fps;
            int frames = Math.Clamp(options.Frames, 1, 10_000);

            // The world clamps a single step to 0.1s so a resume-from-sleep cannot
            // teleport anything. A preview deliberately uses coarse frame spacing to
            // show movement, so subdivide rather than silently losing time.
            int substeps = Math.Max(1, (int)Math.Ceiling(dt / 0.05));

            for (int frame = 0; frame < frames; frame++)
            {
                for (int s = 0; s < substeps; s++)
                    world.Step(dt / substeps);

                world.Render((IntPtr)buffer);

                string path = Path.Combine(options.PreviewDirectory!, $"frame{frame:D3}.png");
                PngWriter.WriteBgra(path, (IntPtr)buffer, width, height, options.Background);
            }

            Console.Out.WriteLine(
                $"wrote {frames} frame(s) to {options.PreviewDirectory} " +
                $"({world.CritterCount} critter(s), {world.TotalPoints:N0} points, {width}x{height})");
        }
        finally
        {
            NativeMemory.Free(buffer);
        }

        return 0;
    }
}
