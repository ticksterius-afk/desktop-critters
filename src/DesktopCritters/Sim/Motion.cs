using DesktopCritters.Config;
using DesktopCritters.Expr;

namespace DesktopCritters.Sim;

/// <summary>
/// The rectangle a creature's <em>centre</em> is allowed to occupy: the sandbox
/// shrunk by the creature's own radius, so the body turns before any part of it
/// leaves the screen rather than after.
/// </summary>
public readonly record struct Bounds(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public double CentreX => (Left + Right) * 0.5;
    public double CentreY => (Top + Bottom) * 0.5;

    /// <summary>
    /// Insets a sandbox by <paramref name="radius"/>. If the creature is larger than
    /// the sandbox on an axis, that axis collapses to its midline instead of inverting.
    /// </summary>
    public static Bounds Inset(double width, double height, double radius)
    {
        double left = radius, right = width - radius;
        double top = radius, bottom = height - radius;

        if (right <= left) left = right = width * 0.5;
        if (bottom <= top) top = bottom = height * 0.5;

        return new Bounds(left, top, right, bottom);
    }
}

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
    void Reset(in Bounds bounds, Pose pose);
    void Step(double dt, in Bounds bounds, Pose pose);
}

public static class MotionFactory
{
    public static IMotion Create(MotionConfig config, int seed) => config.Mode.ToLowerInvariant() switch
    {
        "path" => new PathMotion(config, seed),
        "still" => new StillMotion(),
        _ => new WanderMotion(config, seed),
    };
}

/// <summary>
/// Steering-based swimming inside a bounded box.
///
/// Three layers, in order of authority:
///   1. a slow noise field nudges the heading, so the path never repeats;
///   2. proportional steering turns the creature inward as it enters the margin band;
///   3. a hard reflection at the boundary guarantees it turns around rather than
///      grinding along an edge, even after a huge time step.
///
/// The noise is damped as the creature nears a wall, so random drift can never fight
/// the turn — which was the flaw in the naive "add avoidance to wander" approach.
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

    public void Reset(in Bounds bounds, Pose pose)
    {
        pose.X = Lerp(bounds.Left, bounds.Right, 0.25 + 0.5 * Noise.At(_seed * 1.7));
        pose.Y = Lerp(bounds.Top, bounds.Bottom, 0.25 + 0.5 * Noise.At(_seed * 2.9 + 11));
        pose.Heading = Noise.At(_seed * 3.3 + 5) * Math.Tau;
        pose.Speed = _c.Speed;
        _time = 0;
    }

    public void Step(double dt, in Bounds bounds, Pose pose)
    {
        _time += dt;

        // The margin can never exceed half the usable box, or the creature would be
        // inside the band everywhere and steer against itself.
        double margin = Math.Max(1.0, Math.Min(_c.Margin, Math.Min(bounds.Width, bounds.Height) * 0.45));

        // --- how close are we to each wall, 0 (clear) .. 1 (touching) ---
        double nearLeft = Band(pose.X - bounds.Left, margin);
        double nearRight = Band(bounds.Right - pose.X, margin);
        double nearTop = Band(pose.Y - bounds.Top, margin);
        double nearBottom = Band(bounds.Bottom - pose.Y, margin);
        double proximity = Math.Max(Math.Max(nearLeft, nearRight), Math.Max(nearTop, nearBottom));

        // --- 1. lazy random drift, silenced near the walls ---
        double turn = Noise.Signed(_seed + _time * _c.WanderFreq) * _c.WanderAmount * (1.0 - proximity);

        // --- 1b. level out towards horizontal, also silenced near the walls so it
        //         cannot fight a turn away from the top or bottom edge ---
        if (_c.LevelBias > 0)
        {
            double level = Math.Cos(pose.Heading) >= 0 ? 0.0 : Math.PI;
            turn += WrapAngle(level - pose.Heading) * _c.LevelBias * (1.0 - proximity);
        }

        // --- 2. proportional steering back towards open water ---
        double escapeX = nearLeft - nearRight;
        double escapeY = nearTop - nearBottom;

        if (escapeX != 0 || escapeY != 0)
        {
            double goalX = Math.Cos(pose.Heading) + escapeX * _c.AvoidStrength;
            double goalY = Math.Sin(pose.Heading) + escapeY * _c.AvoidStrength;

            if (goalX != 0 || goalY != 0)
            {
                double error = WrapAngle(Math.Atan2(goalY, goalX) - pose.Heading);
                turn += error * _c.SteerGain;
            }
        }

        double maxTurn = Math.Max(0.01, _c.TurnRate);
        turn = Math.Clamp(turn, -maxTurn, maxTurn);

        pose.Heading = WrapAngle(pose.Heading + turn * dt);
        pose.TurnRate = turn;

        // --- burst-and-glide speed, so it never cruises at a robotic constant ---
        double variance = Math.Clamp(_c.SpeedVariance, 0, 1);
        pose.Speed = _c.Speed * (1.0 + variance * Noise.Signed(_seed * 3.7 + _time * 0.45));

        pose.X += Math.Cos(pose.Heading) * pose.Speed * dt;
        pose.Y += Math.Sin(pose.Heading) * pose.Speed * dt;

        // --- a slow vertical bob, independent of heading ---
        double previousBob = Bob(_bobPhase);
        _bobPhase += dt;
        pose.Y += Bob(_bobPhase) - previousBob;

        // --- 3. hard reflection: the guarantee that it turns around at the border ---
        Reflect(bounds, pose);
    }

    /// <summary>
    /// Mirrors position and heading about whichever walls were crossed. Reflecting the
    /// heading (rather than merely clamping the position) is what stops a creature
    /// sliding along an edge with its nose pressed into it.
    /// </summary>
    private static void Reflect(in Bounds bounds, Pose pose)
    {
        if (pose.X < bounds.Left)
        {
            pose.X = bounds.Left + (bounds.Left - pose.X);
            pose.Heading = WrapAngle(Math.PI - pose.Heading);
        }
        else if (pose.X > bounds.Right)
        {
            pose.X = bounds.Right - (pose.X - bounds.Right);
            pose.Heading = WrapAngle(Math.PI - pose.Heading);
        }

        if (pose.Y < bounds.Top)
        {
            pose.Y = bounds.Top + (bounds.Top - pose.Y);
            pose.Heading = WrapAngle(-pose.Heading);
        }
        else if (pose.Y > bounds.Bottom)
        {
            pose.Y = bounds.Bottom - (pose.Y - bounds.Bottom);
            pose.Heading = WrapAngle(-pose.Heading);
        }

        // A single reflection cannot fix an overshoot bigger than the box itself
        // (a resume-from-sleep, say), so pin as a last resort.
        pose.X = Math.Clamp(pose.X, bounds.Left, bounds.Right);
        pose.Y = Math.Clamp(pose.Y, bounds.Top, bounds.Bottom);
    }

    /// <summary>0 when further than <paramref name="margin"/> from the wall, 1 at the wall.</summary>
    private static double Band(double distance, double margin)
        => distance >= margin ? 0.0 : Math.Clamp(1.0 - distance / margin, 0.0, 1.0);

    private double Bob(double phase) => _c.BobAmount * Math.Sin(phase * _c.BobFreq * Math.Tau);

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    internal static double WrapAngle(double a)
    {
        a = (a + Math.PI) % Math.Tau;
        if (a < 0) a += Math.Tau;
        return a - Math.PI;
    }
}

/// <summary>
/// Follows a closed-form path given as two formulas in bounds-normalised units.
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

    public void Reset(in Bounds bounds, Pose pose)
    {
        _time = 0;
        Apply(0, bounds, pose);
    }

    public void Step(double dt, in Bounds bounds, Pose pose)
    {
        _time += dt;
        double previousX = pose.X, previousY = pose.Y, previousHeading = pose.Heading;

        Apply(_time, bounds, pose);

        pose.Speed = Math.Sqrt(Sq(pose.X - previousX) + Sq(pose.Y - previousY)) / Math.Max(dt, 1e-4);
        pose.TurnRate = WanderMotion.WrapAngle(pose.Heading - previousHeading) / Math.Max(dt, 1e-4);
    }

    private void Apply(double time, in Bounds bounds, Pose pose)
    {
        double period = Math.Abs(_c.Period) < 1e-6 ? 1.0 : _c.Period;
        double u = Fract(time / period + _phaseOffset);

        var (x0, y0) = Sample(u, time, bounds);
        var (x1, y1) = Sample(Fract(u + DerivativeStep), time, bounds);

        pose.X = x0;
        pose.Y = y0;

        double dx = x1 - x0, dy = y1 - y0;
        if (dx != 0 || dy != 0)
            pose.Heading = Math.Atan2(dy, dx);
    }

    private (double X, double Y) Sample(double u, double time, in Bounds bounds)
    {
        _registers[0] = u;
        _registers[1] = time;
        _registers[2] = bounds.Width;
        _registers[3] = bounds.Height;

        // Formulas work in 0..1 across the *inset* box, so a path never carries the
        // body off-screen however large the creature is.
        return (bounds.Left + _x.Evaluate(_registers) * bounds.Width,
                bounds.Top + _y.Evaluate(_registers) * bounds.Height);
    }

    private static double Fract(double v) => v - Math.Floor(v);
    private static double Sq(double v) => v * v;
}

/// <summary>Parks the creature mid-sandbox. Handy for tuning a body formula.</summary>
public sealed class StillMotion : IMotion
{
    public void Reset(in Bounds bounds, Pose pose) => Step(0, bounds, pose);

    public void Step(double dt, in Bounds bounds, Pose pose)
    {
        pose.X = bounds.CentreX;
        pose.Y = bounds.CentreY;
        pose.Heading = 0;
        pose.Speed = 0;
        pose.TurnRate = 0;
    }
}
