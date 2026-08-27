using DesktopCritters.Config;
using DesktopCritters.Expr;
using DesktopCritters.Render;

namespace DesktopCritters.Sim;

/// <summary>
/// Owns the sandbox: the pixel canvas, the population, and the mapping from config
/// to running simulation. Rebuilding a world is cheap enough (a few milliseconds,
/// dominated by compiling the formulas) that config reloads just build a new one.
/// </summary>
public sealed class World
{
    private readonly string _creaturesDirectory;
    private readonly Dictionary<string, CreatureProgram> _programCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Critter> _critters = new();
    private readonly List<string> _warnings = new();

    public PixelCanvas Canvas { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public IReadOnlyList<string> Warnings => _warnings;
    public int CritterCount => _critters.Count;
    public int TotalPoints { get; private set; }

    public World(string creaturesDirectory, int width, int height)
    {
        _creaturesDirectory = creaturesDirectory;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Canvas = new PixelCanvas(Width, Height);
    }

    /// <summary>Rebuilds the population and palette from config. Safe to call repeatedly.</summary>
    public void Configure(AppConfig config, int width, int height)
    {
        _warnings.Clear();

        if (width != Width || height != Height || Canvas is null)
        {
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
            Canvas = new PixelCanvas(Width, Height);
        }

        var fallback = new[] { 0x1B5F8Au, 0x3FB6E6u, 0xA9F0FFu, 0xFFFFFFu };
        Canvas.SetPalette(
            AppConfig.ParseColors(config.Render.Colors, fallback),
            config.Render.Exposure,
            config.Render.Opacity,
            config.Render.DensityRange);

        _critters.Clear();
        _programCache.Clear();
        TotalPoints = 0;

        int seedCounter = 0;
        foreach (var spec in config.Critters)
        {
            CreatureProgram program;
            try
            {
                program = LoadProgram(spec.Creature);
            }
            catch (Exception ex)
            {
                _warnings.Add($"creature '{spec.Creature}': {ex.Message}");
                continue;
            }

            int count = Math.Clamp(spec.Count, 0, 64);
            for (int n = 0; n < count; n++)
            {
                int seed = spec.Seed * 1000 + seedCounter++;

                // Vary the size a little so a group does not look cloned.
                double jitter = 1.0 + (Noise.At(seed * 2.71 + 0.5) - 0.5) * 2.0 * Math.Clamp(spec.SizeVariance, 0, 0.9);
                int points = Math.Clamp(spec.Points ?? program.DefaultPoints, 16, 400_000);

                try
                {
                    var critter = new Critter(
                        program,
                        points,
                        Math.Max(4, spec.Size * jitter),
                        spec.BodySpeed,
                        MotionFactory.Create(spec.Motion, seed),
                        spec.Motion.Bank,
                        spec.Motion.TurnRate,
                        spec.Motion.EdgePadding,
                        spec.Params,
                        seed);

                    critter.Reset(Width, Height);
                    _critters.Add(critter);
                    TotalPoints += points;
                }
                catch (Exception ex)
                {
                    _warnings.Add($"critter '{spec.Creature}' #{n}: {ex.Message}");
                }
            }
        }

        if (_critters.Count == 0)
            _warnings.Add("no critters were created -- check the creature names in config.json");

        Canvas.InvalidateAll();
    }

    private CreatureProgram LoadProgram(string name)
    {
        if (_programCache.TryGetValue(name, out var cached))
            return cached;

        string path = Path.Combine(_creaturesDirectory, name + ".json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"no such file: {path}");

        var program = CreatureProgram.Compile(CreatureFile.Load(path).ToFormula());
        _programCache[name] = program;
        return program;
    }

    public void Step(double dt)
    {
        // Clamp the step so a resume-from-sleep does not teleport everything into a wall.
        dt = Math.Clamp(dt, 0.0, 0.1);
        foreach (var critter in _critters)
            critter.Step(dt, Width, Height);
    }

    /// <summary>Draws every critter and resolves to the target surface.</summary>
    public DirtyRect Render(IntPtr bits)
    {
        Canvas.Begin();
        foreach (var critter in _critters)
            critter.Render(Canvas);
        return Canvas.Resolve(bits);
    }
}
