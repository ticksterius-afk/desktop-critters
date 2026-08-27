using DesktopCritters.Config;
using DesktopCritters.Expr;

namespace DesktopCritters.Sim;

/// <summary>Where a creature is and which way it is pointing, in canvas pixels.</summary>
public sealed class Pose
{
    public double X, Y;

    /// <summary>Direction of travel in radians; 0 is +X (rightwards).</summary>
    public double Heading;

    /// <summary>Signed turn rate in rad/s, used to bank the body into corners.</summary>
    public double TurnRate;

    public double Speed;
}

public interface IMotion
{
    void Reset(double width, double height, Pose pose);
    void Step(double dt, double width, double height, Pose pose);
}

public static class MotionFactory
{
    public static IMotion Create(MotionConfig config, int seed) => config.Mode.ToLowerInvariant() switch
    {
        "path" => new PathMotion(config, seed),
        "still" => new StillMotion(config),
        _ => new WanderMotion(config, seed),
    };
}

/// <summary>
/// Steering-based swimming: a slow noise field nudges the heading, the sandbox
/// edges push back, and the turn rate is capped so the creature banks through
/// corners instead of pivoting on the spot.
///
/// This is the mode that reads as "alive" -- a closed-form path always eventually
/// looks like a closed-form path, whereas bounded noise never repeats.
/// </summary>
public sealed class WanderMotion : IMotion
{
    private readonly MotionConfig _c;
    private readonly double _seed;
    private double _time;
    private double _bobPhase;

    public WanderMotion(MotionConfig config, int seed)
    {
        _c = config;
        _seed = seed * 137.13 + 4.5;
        _bobPhase = seed * 0.911;
    }

    public void Reset(double width, double height, Pose pose)
    {
        // Start somewhere inside the safe interior, pointed at a stable angle so the
        // creature does not spend its first seconds fighting a wall.
        double m = Math.Min(_c.Margin, Math.Min(width, height) * 0.35);
        pose.X = Lerp(m, width - m, Noise.At(_seed * 1.7));
        pose.Y = Lerp(m, height - m, Noise.At(_seed * 2.9 + 11));
        pose.Heading = Noise.At(_seed * 3.3 + 5) * Math.Tau;
        pose.Speed = _c.Speed;
        _time = 0;
    }

    public void Step(double dt, double width, double height, Pose pose)
    {
        _time += dt;

        // 1. Lazy random drift.
        double drift = Noise.Signed(_seed + _time * _c.WanderFreq) * _c.WanderAmount;

        // 2. Edge repulsion, ramping linearly from zero at the margin to full at the wall.
        double margin = Math.Max(1.0, Math.Min(_c.Margin, Math.Min(width, height) * 0.45));
        double ax = 0, ay = 0;

        if (pose.X < margin) ax += 1.0 - pose.X / margin;
        if (width - pose.X < margin) ax -= 1.0 - (width - pose.X) / margin;
        if (pose.Y < margin) ay += 1.0 - pose.Y / margin;
        if (height - pose.Y < margin) ay -= 1.0 - (height - pose.Y) / margin;

        // 3. Blend forward direction with the escape vector, then turn towards the result.
        double desiredX = Math.Cos(pose.Heading) + ax * _c.AvoidStrength;
        double desiredY = Math.Sin(pose.Heading) + ay * _c.AvoidStrength;

        double turn = drift;
        if (desiredX != 0 || desiredY != 0)
        {
            double delta = WrapAngle(Math.Atan2(desiredY, desiredX) - pose.Heading);
            turn += delta / Math.Max(dt, 1e-4);
        }

        double maxTurn = Math.Max(0.01, _c.TurnRate);
        turn = Math.Clamp(turn, -maxTurn, maxTurn);

        pose.Heading = WrapAngle(pose.Heading + turn * dt);
        pose.TurnRate = turn;

        // 4. Burst-and-glide speed, so the creature does not cruise at a robotic constant.
        double variance = Math.Clamp(_c.SpeedVariance, 0, 1);
        pose.Speed = _c.Speed * (1.0 + variance * Noise.Signed(_seed * 3.7 + _time * 0.45));

        pose.X += Math.Cos(pose.Heading) * pose.Speed * dt;
        pose.Y += Math.Sin(pose.Heading) * pose.Speed * dt;

        // 5. A slow vertical bob layered on top, independent of heading.
        double previousBob = Bob(_bobPhase);
        _bobPhase += dt;
        pose.Y += Bob(_bobPhase) - previousBob;

        // Hard stop at the walls in case a big dt (a resume from sleep, say) outran the steering.
        pose.X = Math.Clamp(pose.X, 0, width);
        pose.Y = Math.Clamp(pose.Y, 0, height);
    }

    private double Bob(double phase) => _c.BobAmount * Math.Sin(phase * _c.BobFreq * Math.Tau);

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double WrapAngle(double a)
    {
        a = (a + Math.PI) % Math.Tau;
        if (a < 0) a += Math.Tau;
        return a - Math.PI;
    }
}

/// <summary>
/// Follows a closed-form path given as two formulas in sandbox-normalised units.
/// Heading comes from a finite difference of the path, so the body always points
/// along the curve without the formula having to say so.
/// </summary>
public sealed class PathMotion : IMotion
{
    private const double DerivativeStep = 1e-3;

    private readonly MotionConfig _c;
    private readonly ScalarProgram _x;
    private readonly ScalarProgram _y;
    private readonly double[] _registers = new double[4]; // u, t, w, h
    private readonly double _phaseOffset;
    private double _time;

    public PathMotion(MotionConfig config, int seed)
    {
        _c = config;
        _x = ScalarProgram.Compile(config.X, "u", "t", "w", "h");
        _y = ScalarProgram.Compile(config.Y, "u", "t", "w", "h");

        // Several critters on one path should not stack on top of each other.
        _phaseOffset = Noise.At(seed * 7.31 + 2.5);
    }

    public void Reset(double width, double height, Pose pose)
    {
        _time = 0;
        Apply(0, width, height, pose);
    }

    public void Step(double dt, double width, double height, Pose pose)
    {
        _time += dt;
        double previousX = pose.X, previousY = pose.Y, previousHeading = pose.Heading;

        Apply(_time, width, height, pose);

        pose.Speed = Math.Sqrt(Sq(pose.X - previousX) + Sq(pose.Y - previousY)) / Math.Max(dt, 1e-4);
        pose.TurnRate = AngleDelta(previousHeading, pose.Heading) / Math.Max(dt, 1e-4);
    }

    private void Apply(double time, double width, double height, Pose pose)
    {
        double period = Math.Abs(_c.Period) < 1e-6 ? 1.0 : _c.Period;
        double u = Fract(time / period + _phaseOffset);

        var (x0, y0) = Sample(u, time, width, height);
        var (x1, y1) = Sample(Fract(u + DerivativeStep), time, width, height);

        pose.X = x0;
        pose.Y = y0;

        double dx = x1 - x0, dy = y1 - y0;
        if (dx != 0 || dy != 0)
            pose.Heading = Math.Atan2(dy, dx);
    }

    private (double X, double Y) Sample(double u, double time, double width, double height)
    {
        _registers[0] = u;
        _registers[1] = time;
        _registers[2] = width;
        _registers[3] = height;

        // Formulas work in 0..1 so a path survives being moved to a different sandbox.
        return (_x.Evaluate(_registers) * width, _y.Evaluate(_registers) * height);
    }

    private static double Fract(double v) => v - Math.Floor(v);
    private static double Sq(double v) => v * v;

    private static double AngleDelta(double from, double to)
    {
        double d = (to - from + Math.PI) % Math.Tau;
        if (d < 0) d += Math.Tau;
        return d - Math.PI;
    }
}

/// <summary>Parks the creature in the middle of the sandbox. Handy for tuning a body formula.</summary>
public sealed class StillMotion : IMotion
{
    private readonly MotionConfig _c;
    public StillMotion(MotionConfig config) => _c = config;

    public void Reset(double width, double height, Pose pose)
    {
        pose.X = width / 2;
        pose.Y = height / 2;
        pose.Heading = 0;
        pose.Speed = 0;
        pose.TurnRate = 0;
    }

    public void Step(double dt, double width, double height, Pose pose)
    {
        pose.X = width / 2;
        pose.Y = height / 2 + _c.BobAmount * Math.Sin(Environment.TickCount64 / 1000.0 * _c.BobFreq * Math.Tau);
    }
}
