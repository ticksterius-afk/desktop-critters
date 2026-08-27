namespace DesktopCritters.Expr;

/// <summary>
/// Deterministic value noise. Used both by formulas (via the <c>noise()</c>
/// builtin) and by the wander steering, so a given seed always produces the same
/// swim path -- which matters when you are tuning a creature and want to compare
/// two parameter sets frame for frame.
/// </summary>
public static class Noise
{
    private static double Hash(int n)
    {
        unchecked
        {
            uint h = (uint)n * 2654435761u;
            h ^= h >> 15; h *= 2246822519u;
            h ^= h >> 13; h *= 3266489917u;
            h ^= h >> 16;
            return h / 4294967296.0;
        }
    }

    private static double Smooth(double t) => t * t * (3.0 - 2.0 * t);

    /// <summary>Smooth 1D noise in [0,1], period 1 per integer step.</summary>
    public static double At(double x)
    {
        int i = (int)Math.Floor(x);
        double f = Smooth(x - i);
        return Hash(i) + (Hash(i + 1) - Hash(i)) * f;
    }

    /// <summary>Smooth 2D noise in [0,1].</summary>
    public static double At(double x, double y)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        double fx = Smooth(x - ix), fy = Smooth(y - iy);

        double a = Hash(ix + iy * 57);
        double b = Hash(ix + 1 + iy * 57);
        double c = Hash(ix + (iy + 1) * 57);
        double d = Hash(ix + 1 + (iy + 1) * 57);

        double top = a + (b - a) * fx;
        double bottom = c + (d - c) * fx;
        return top + (bottom - top) * fy;
    }

    /// <summary>Fractal sum of <paramref name="octaves"/> noise layers, still in [0,1].</summary>
    public static double Fbm(double x, double octaves)
    {
        int n = Math.Clamp((int)octaves, 1, 8);
        double sum = 0, amp = 0.5, total = 0, freq = 1;
        for (int i = 0; i < n; i++)
        {
            sum += At(x * freq) * amp;
            total += amp;
            amp *= 0.5;
            freq *= 2.0;
        }
        return sum / total;
    }

    /// <summary>Signed variant in [-1,1], the shape steering code usually wants.</summary>
    public static double Signed(double x) => At(x) * 2.0 - 1.0;
}
