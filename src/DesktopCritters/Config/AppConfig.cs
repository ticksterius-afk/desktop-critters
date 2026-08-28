using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopCritters.Expr;

namespace DesktopCritters.Config;

public sealed class SandboxConfig
{
    /// <summary>workArea | monitor | virtual | rect</summary>
    public string Mode { get; set; } = "workArea";

    /// <summary>primary | cursor | a 0-based monitor index</summary>
    public string Monitor { get; set; } = "primary";

    /// <summary>Pixels to shrink the sandbox by on every side.</summary>
    public int Inset { get; set; }

    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 900;
    public int Height { get; set; } = 600;
}

public sealed class RenderConfig
{
    /// <summary>How quickly accumulated density saturates to opaque.</summary>
    public double Exposure { get; set; } = 1.8;

    /// <summary>Density at which the colour ramp reaches its last stop.</summary>
    public double DensityRange { get; set; } = 3.0;

    /// <summary>Master opacity, 0..1.</summary>
    public double Opacity { get; set; } = 0.95;

    /// <summary>Ramp from sparse to dense, as #RRGGBB.</summary>
    public string[] Colors { get; set; } = { "#1b5f8a", "#3fb6e6", "#a9f0ff", "#ffffff" };
}

public sealed class MotionConfig
{
    /// <summary>wander | path | still</summary>
    public string Mode { get; set; } = "wander";

    /// <summary>Cruise speed in pixels per second.</summary>
    public double Speed { get; set; } = 52;

    /// <summary>How much the speed varies with the swim cycle, 0..1.</summary>
    public double SpeedVariance { get; set; } = 0.35;

    /// <summary>Maximum heading change in radians per second.</summary>
    public double TurnRate { get; set; } = 1.1;

    /// <summary>How fast the wander noise evolves. Lower is lazier.</summary>
    public double WanderFreq { get; set; } = 0.18;

    /// <summary>Strength of the random heading drift.</summary>
    public double WanderAmount { get; set; } = 0.9;

    /// <summary>
    /// How hard the creature corrects towards open water, per radian of heading
    /// error, per second. Higher turns away from edges more decisively.
    /// </summary>
    public double SteerGain { get; set; } = 3.0;

    /// <summary>
    /// Multiplier on the body radius kept clear of the sandbox edge. 1.0 keeps the
    /// whole creature on screen; below 1.0 lets it clip out at the edges.
    /// </summary>
    public double EdgePadding { get; set; } = 1.0;

    /// <summary>
    /// How strongly the heading is pulled back towards horizontal, per radian of tilt,
    /// per second. Fish swim level far more than they swim straight up, and without
    /// this a pure random walk spends too much time nose-up or nose-down. 0 disables.
    /// </summary>
    public double LevelBias { get; set; } = 0.7;

    /// <summary>Distance from a sandbox edge at which the creature starts turning away.</summary>
    public double Margin { get; set; } = 220;

    /// <summary>How hard edges push. Raise it if a creature keeps nosing into corners.</summary>
    public double AvoidStrength { get; set; } = 2.4;

    /// <summary>Extra body rotation proportional to turn rate, so turns look banked.</summary>
    public double Bank { get; set; } = 0.35;

    /// <summary>Gentle vertical drift, in pixels.</summary>
    public double BobAmount { get; set; } = 7;
    public double BobFreq { get; set; } = 0.27;

    // --- path mode -----------------------------------------------------
    /// <summary>Seconds for one full lap of the path.</summary>
    public double Period { get; set; } = 34;

    /// <summary>Position formulas in sandbox-normalised units, with <c>u</c> in 0..1.</summary>
    public string X { get; set; } = "0.5 + 0.38 * sin(TAU * u)";
    public string Y { get; set; } = "0.5 + 0.22 * sin(2 * TAU * u)";
}

public sealed class CritterConfig
{
    /// <summary>File name (without .json) under the creatures folder.</summary>
    public string Creature { get; set; } = "fish";

    /// <summary>How many of this creature to spawn.</summary>
    public int Count { get; set; } = 1;

    /// <summary>Body length in pixels, measured along the swim axis.</summary>
    public double Size { get; set; } = 300;

    /// <summary>Random variation in size between individuals, 0..1.</summary>
    public double SizeVariance { get; set; } = 0.18;

    /// <summary>Overrides the creature file's point count. More points = denser, costlier.</summary>
    public int? Points { get; set; }

    /// <summary>Multiplier on the body's animation cycle.</summary>
    public double BodySpeed { get; set; } = 1.0;

    /// <summary>Changes the wander path and the phase offsets. Same seed = same swim.</summary>
    public int Seed { get; set; } = 1;

    public MotionConfig Motion { get; set; } = new();

    /// <summary>Overrides for the creature file's own named parameters.</summary>
    public Dictionary<string, double>? Params { get; set; }
}

public sealed class AppConfig
{
    /// <summary>Target frames per second.</summary>
    public int Fps { get; set; } = 60;

    /// <summary>desktop | bottom | topmost</summary>
    public string Layer { get; set; } = "desktop";

    /// <summary>Stop rendering while a fullscreen app or presentation is running.</summary>
    public bool PauseWhenFullscreen { get; set; } = true;

    /// <summary>Re-read this file automatically when it changes on disk.</summary>
    public bool WatchConfig { get; set; } = true;

    /// <summary>
    /// How often (seconds) to re-push the whole surface and re-check that we are still
    /// attached above the wallpaper. Repaints of the desktop -- toggling icons, changing
    /// wallpaper, Explorer restarting -- can erase a layered child's pixels, and normal
    /// frames only repaint the small rectangle around the creature. This heals that,
    /// and runs even while paused. Set to 0 to disable.
    /// </summary>
    public double RefreshSeconds { get; set; } = 2.0;

    public SandboxConfig Sandbox { get; set; } = new();
    public RenderConfig Render { get; set; } = new();
    public List<CritterConfig> Critters { get; set; } = new() { new CritterConfig() };

    // ---- serialisation -------------------------------------------------
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppConfig Load(string path)
        => JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions)
           ?? throw new InvalidDataException($"'{path}' did not contain a config object");

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public Interop.LayerMode LayerMode => Layer.ToLowerInvariant() switch
    {
        "topmost" or "top" => Interop.LayerMode.Topmost,
        "bottom" => Interop.LayerMode.Bottom,
        _ => Interop.LayerMode.Desktop,
    };

    /// <summary>Parses the colour ramp, skipping anything malformed rather than throwing.</summary>
    public static uint[] ParseColors(string[]? hex, uint[] fallback)
    {
        if (hex is null || hex.Length == 0)
            return fallback;

        var result = new List<uint>(hex.Length);
        foreach (var raw in hex)
        {
            var s = raw.Trim().TrimStart('#');
            if (s.Length == 3)
                s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
            if (s.Length == 6 && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint v))
                result.Add(v);
        }
        return result.Count > 0 ? result.ToArray() : fallback;
    }
}

// ---------------------------------------------------------------------------

/// <summary>
/// Optional settings a creature recommends for itself. Applied when the creature is
/// chosen from the tray, so switching animal does not leave a bird flapping at a
/// koi's cruising speed.
/// </summary>
public sealed class CreatureSuggest
{
    public double? Size { get; set; }
    public double? BodySpeed { get; set; }
    public MotionConfig? Motion { get; set; }
}

/// <summary>JSON shape of a creature definition file.</summary>
public sealed class CreatureFile
{
    public string Name { get; set; } = "unnamed";
    public string? Description { get; set; }
    public int Points { get; set; } = 20000;

    /// <summary>
    /// Radians of body phase per second. The p5.js originals advance by PI/80 per
    /// frame at 60fps, which is where the default comes from.
    /// </summary>
    public double BodyRate { get; set; } = Math.PI / 80 * 60;

    /// <summary>Rotation, in degrees, that makes the creature's +X axis its forward axis.</summary>
    public double Facing { get; set; }

    /// <summary>Which extent the config's <c>size</c> measures: forward | span | max.</summary>
    public string SizeAxis { get; set; } = "forward";

    /// <summary>
    /// Radians of <c>t</c> after which the body repeats exactly, as an expression over
    /// this creature's params (e.g. "TAU" or "12 * PI / bodyTurn"). The phase wraps here,
    /// so the animation loops forever with no seam and no floating-point drift.
    /// </summary>
    public string LoopPeriod { get; set; } = "TAU";

    /// <summary>What this creature would like config to use for it. Optional.</summary>
    public CreatureSuggest? Suggest { get; set; }

    public Dictionary<string, double> Params { get; set; } = new();
    public List<VarEntry> Vars { get; set; } = new();

    public string Px { get; set; } = "0";
    public string Py { get; set; } = "0";
    public string Weight { get; set; } = "1";

    public sealed class VarEntry
    {
        public string Name { get; set; } = "";
        public string Expr { get; set; } = "0";
    }

    public CreatureFormula ToFormula() => new()
    {
        Name = Name,
        Points = Math.Max(1, Points),
        BodyRate = BodyRate,
        Facing = Facing,
        SizeAxis = SizeAxis,
        LoopPeriod = LoopPeriod,
        Params = Params,
        Vars = Vars.Select(v => new VarDef(v.Name, v.Expr)).ToList(),
        Px = Px,
        Py = Py,
        Weight = Weight,
    };

    public static CreatureFile Load(string path)
        => JsonSerializer.Deserialize<CreatureFile>(File.ReadAllText(path), AppConfig.JsonOptions)
           ?? throw new InvalidDataException($"'{path}' did not contain a creature object");
}
