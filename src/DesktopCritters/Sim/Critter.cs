using DesktopCritters.Expr;
using DesktopCritters.Render;

namespace DesktopCritters.Sim;

/// <summary>
/// One animal: a compiled body formula, its own parameter registers, and a motion
/// model that carries it around the sandbox.
///
/// The body formula is written in a fixed local frame and knows nothing about
/// where the creature is or which way it is going. This split is the whole point of
/// the design -- swapping the swim path never touches the art, and swapping the art
/// never touches the swimming.
/// </summary>
public sealed class Critter
{
    private readonly CreatureProgram _program;
    private readonly double[] _registers;

    /// <summary>Time-invariant per-point values, computed once at construction.</summary>
    private readonly double[] _cache;
    private readonly IMotion _motion;
    private readonly Pose _pose = new();

    private readonly int _points;
    private readonly double _scale;
    private readonly double _bodySpeed;
    private readonly double _bankAmount;
    private readonly double _maxTurnRate;
    private readonly double _edgePadding;

    private double _phase;
    private double _bank;

    /// <summary>Radians of body phase after which the formula repeats exactly.</summary>
    private readonly double _loopPeriod;

    /// <summary>Half-extents of the body in canvas pixels, after scaling.</summary>
    public double HalfWidth { get; }
    public double HalfHeight { get; }

    public Critter(CreatureProgram program, int points, double targetLength, double bodySpeed,
        IMotion motion, double bankAmount, double maxTurnRate, double edgePadding,
        IReadOnlyDictionary<string, double>? paramOverrides, int seed)
    {
        _program = program;
        _points = Math.Max(1, points);
        _bodySpeed = bodySpeed;
        _motion = motion;
        _bankAmount = bankAmount;
        _maxTurnRate = Math.Max(0.01, maxTurnRate);
        _edgePadding = Math.Max(0, edgePadding);

        _registers = program.NewRegisters(_points);

        if (paramOverrides is not null)
        {
            foreach (var (name, value) in paramOverrides)
            {
                if (program.ParamSlots.TryGetValue(name, out int slot))
                    _registers[slot] = value;
            }
        }

        // Parameters are set, so the time-invariant half of the formula can be
        // evaluated once here and reused for the life of the critter.
        _cache = program.NewCache(_points);
        program.BuildCache(_registers, _points, _cache);

        // Scale from the creature's own units to the requested on-screen length.
        var (halfW, halfH) = program.MeasureExtent(_registers, _cache, _points);
        double reference = program.ReferenceHalfExtent(halfW, halfH);
        _scale = targetLength / Math.Max(1e-6, reference * 2.0);
        HalfWidth = halfW * _scale;
        HalfHeight = halfH * _scale;

        // The creature rotates freely, so the circumscribed radius is what has to
        // clear the edge -- using just the half-length would let the tail hang off
        // whenever the fish is swimming diagonally.
        Radius = Math.Sqrt(HalfWidth * HalfWidth + HalfHeight * HalfHeight) * _edgePadding;

        _loopPeriod = program.LoopPeriodFor(_registers);

        // Offset the body cycle so a school of fish does not beat in lockstep.
        _phase = Noise.At(seed * 5.17 + 0.3) * _loopPeriod;
    }

    /// <summary>Distance from the creature's centre that must stay inside the sandbox.</summary>
    public double Radius { get; }

    private Bounds BoundsFor(double width, double height) => Bounds.Inset(width, height, Radius);

    public void Reset(double width, double height) => _motion.Reset(BoundsFor(width, height), _pose);

    public void Step(double dt, double width, double height)
    {
        // Wrap at the creature's own period, every frame. Because the shape is exactly
        // equal at phase p and p + loopPeriod, the wrap is invisible -- and the phase can
        // never grow large enough to lose precision, however long the widget runs.
        _phase += _program.BodyRate * _bodySpeed * dt;
        if (_phase >= _loopPeriod) _phase %= _loopPeriod;

        _motion.Step(dt, BoundsFor(width, height), _pose);

        // Ease into the bank so direction changes read as a lean, not a snap.
        double target = Math.Clamp(_pose.TurnRate / _maxTurnRate, -1, 1) * _bankAmount;
        _bank += (target - _bank) * Math.Min(1.0, dt * 4.0);
    }

    public void Render(PixelCanvas canvas)
    {
        // A side-on animal should mirror when it swims left, never roll upside down.
        // Mirroring about local X and rotating by heading+PI puts "forward" on the
        // velocity vector while leaving "up" pointing up.
        bool mirror = Math.Cos(_pose.Heading) < 0;
        double m = mirror ? -1.0 : 1.0;
        double phi = mirror ? _pose.Heading + Math.PI : _pose.Heading;

        // Bank is applied in body space, before the mirror, so it leans the same way
        // whichever direction the creature is facing.
        double f = _program.FacingRadians + _bank;

        double cf = Math.Cos(f), sf = Math.Sin(f);
        double cp = Math.Cos(phi), sp = Math.Sin(phi);

        // Combined R(phi) * Mirror * R(f), folded into one 2x2 so each point costs
        // four multiplies instead of two full rotations.
        double a = (cp * m * cf - sp * sf) * _scale;
        double b = (-cp * m * sf - sp * cf) * _scale;
        double c = (sp * m * cf + cp * sf) * _scale;
        double d = (-sp * m * sf + cp * cf) * _scale;

        double originX = _pose.X, originY = _pose.Y;
        var registers = _registers;
        var cache = _cache;
        int outX = CreatureProgram.RegOutX, outY = CreatureProgram.RegOutY, outW = CreatureProgram.RegOutWeight;

        for (int i = 0; i < _points; i++)
        {
            _program.Evaluate(registers, cache, i, _phase);

            double x = registers[outX];
            double y = registers[outY];
            if (double.IsNaN(x) || double.IsNaN(y))
                continue;   // a formula can legitimately produce holes; skip rather than pile them at the origin

            canvas.Plot(originX + a * x + b * y, originY + c * x + d * y, (float)registers[outW]);
        }
    }
}
