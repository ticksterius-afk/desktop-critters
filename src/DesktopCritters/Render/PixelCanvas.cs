using System.Runtime.CompilerServices;

namespace DesktopCritters.Render;

/// <summary>Inclusive-exclusive integer rectangle in canvas pixels.</summary>
public struct DirtyRect
{
    public int X0, Y0, X1, Y1;

    public static DirtyRect Empty => new() { X0 = int.MaxValue, Y0 = int.MaxValue, X1 = int.MinValue, Y1 = int.MinValue };
    public readonly bool IsEmpty => X1 <= X0 || Y1 <= Y0;
    public readonly int Width => IsEmpty ? 0 : X1 - X0;
    public readonly int Height => IsEmpty ? 0 : Y1 - Y0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(int x, int y)
    {
        if (x < X0) X0 = x;
        if (y < Y0) Y0 = y;
        if (x >= X1) X1 = x + 1;
        if (y >= Y1) Y1 = y + 1;
    }

    public DirtyRect Union(in DirtyRect o)
    {
        if (o.IsEmpty) return this;
        if (IsEmpty) return o;
        return new DirtyRect
        {
            X0 = Math.Min(X0, o.X0), Y0 = Math.Min(Y0, o.Y0),
            X1 = Math.Max(X1, o.X1), Y1 = Math.Max(Y1, o.Y1),
        };
    }

    public DirtyRect ClampTo(int w, int h) => new()
    {
        X0 = Math.Max(0, X0), Y0 = Math.Max(0, Y0),
        X1 = Math.Min(w, X1), Y1 = Math.Min(h, Y1),
    };

    public DirtyRect Inflate(int by) => IsEmpty ? this : new DirtyRect
    {
        X0 = X0 - by, Y0 = Y0 - by, X1 = X1 + by, Y1 = Y1 + by,
    };
}

/// <summary>
/// The whole rendering engine, in one buffer.
///
/// Creature formulas emit a cloud of points, so rather than rasterising shapes we
/// accumulate a scalar density field -- each point splats bilinearly into four
/// pixels -- and then map density to premultiplied BGRA through a lookup table.
/// That gives free anti-aliasing, free "thicker where the points bunch up" shading,
/// and a resolve pass that is one table read per pixel with no float maths.
/// </summary>
public sealed class PixelCanvas
{
    public readonly int Width;
    public readonly int Height;

    private readonly float[] _density;

    /// <summary>Region written by <see cref="Plot"/> since the last <see cref="Begin"/>.</summary>
    private DirtyRect _plotted = DirtyRect.Empty;

    /// <summary>Region plotted last frame; needs clearing and re-resolving this frame.</summary>
    private DirtyRect _previous = DirtyRect.Empty;

    private const int LutSize = 1024;
    private uint[] _lut = new uint[LutSize];
    private float _lutScale = LutSize / 6f;

    public PixelCanvas(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        _density = new float[Width * Height];
        SetPalette(new[] { 0x00284Cu, 0x2EA8D8u, 0xBFF4FFu, 0xFFFFFFu }, 0.6, 1.0, 6.0);
    }

    /// <summary>
    /// Precomputes density -> colour. <paramref name="stops"/> are 0xRRGGBB and are
    /// sampled evenly across the ramp; <paramref name="exposure"/> controls how fast
    /// density saturates to opaque; <paramref name="densityRange"/> is the density at
    /// which the ramp tops out.
    /// </summary>
    public void SetPalette(uint[] stops, double exposure, double opacity, double densityRange)
    {
        if (stops.Length == 0) stops = new[] { 0xFFFFFFu };
        densityRange = Math.Max(0.05, densityRange);
        _lutScale = (float)(LutSize / densityRange);

        var lut = new uint[LutSize];
        for (int i = 0; i < LutSize; i++)
        {
            double d = i / _lutScale;

            // Beer-Lambert style falloff: never quite reaches 1, so dense cores stay
            // smooth instead of clipping into a flat blob.
            double a = (1.0 - Math.Exp(-d * exposure)) * opacity;
            a = Math.Clamp(a, 0.0, 1.0);

            // Ramp position follows opacity, so the faint outer haze picks up the
            // first stop and the dense core reaches the last one.
            double pos = Math.Clamp(a / Math.Max(1e-6, opacity), 0.0, 1.0) * (stops.Length - 1);
            int lo = (int)Math.Floor(pos);
            int hi = Math.Min(lo + 1, stops.Length - 1);
            double f = pos - lo;

            double r = Lerp((stops[lo] >> 16) & 0xFF, (stops[hi] >> 16) & 0xFF, f);
            double g = Lerp((stops[lo] >> 8) & 0xFF, (stops[hi] >> 8) & 0xFF, f);
            double b = Lerp(stops[lo] & 0xFF, stops[hi] & 0xFF, f);

            // UpdateLayeredWindow with ULW_ALPHA requires premultiplied alpha.
            byte pa = (byte)Math.Round(a * 255.0);
            byte pr = (byte)Math.Round(r * a);
            byte pg = (byte)Math.Round(g * a);
            byte pb = (byte)Math.Round(b * a);

            lut[i] = ((uint)pa << 24) | ((uint)pr << 16) | ((uint)pg << 8) | pb;
        }
        _lut = lut;

        static double Lerp(double a, double b, double t) => a + (b - a) * t;
    }

    /// <summary>Clears only what the previous frame dirtied, then starts a new frame.</summary>
    public void Begin()
    {
        var clear = _plotted.ClampTo(Width, Height);
        if (!clear.IsEmpty)
        {
            for (int y = clear.Y0; y < clear.Y1; y++)
                Array.Clear(_density, y * Width + clear.X0, clear.X1 - clear.X0);
        }

        _previous = _plotted;
        _plotted = DirtyRect.Empty;
    }

    /// <summary>Adds <paramref name="weight"/> at a sub-pixel position, bilinearly.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Plot(double x, double y, float weight)
    {
        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);

        float fx = (float)(x - x0);
        float fy = (float)(y - y0);

        float w00 = (1f - fx) * (1f - fy) * weight;
        float w10 = fx * (1f - fy) * weight;
        float w01 = (1f - fx) * fy * weight;
        float w11 = fx * fy * weight;

        // Fast path: the whole 2x2 footprint is inside the canvas.
        if ((uint)x0 < (uint)(Width - 1) && (uint)y0 < (uint)(Height - 1))
        {
            int p = y0 * Width + x0;
            _density[p] += w00;
            _density[p + 1] += w10;
            _density[p + Width] += w01;
            _density[p + Width + 1] += w11;

            _plotted.Add(x0, y0);
            _plotted.Add(x0 + 1, y0 + 1);
            return;
        }

        // Edge path: creatures are allowed to hang off the sandbox, so clip per-texel
        // rather than dropping the point.
        Splat(x0, y0, w00);
        Splat(x0 + 1, y0, w10);
        Splat(x0, y0 + 1, w01);
        Splat(x0 + 1, y0 + 1, w11);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Splat(int x, int y, float w)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
        _density[y * Width + x] += w;
        _plotted.Add(x, y);
    }

    /// <summary>
    /// Converts density to premultiplied BGRA in <paramref name="bits"/> (a top-down
    /// 32bpp DIB of the same dimensions) and reports the rectangle that changed.
    /// The rectangle covers this frame's points plus last frame's, so pixels the
    /// creature has swum away from get cleared.
    /// </summary>
    public unsafe DirtyRect Resolve(IntPtr bits)
    {
        var rect = _plotted.Union(_previous).ClampTo(Width, Height);
        if (rect.IsEmpty)
            return rect;

        uint* dst = (uint*)bits;
        int lutMax = LutSize - 1;
        float scale = _lutScale;

        fixed (float* dens = _density)
        fixed (uint* lut = _lut)
        {
            for (int y = rect.Y0; y < rect.Y1; y++)
            {
                int row = y * Width;
                float* s = dens + row + rect.X0;
                uint* d = dst + row + rect.X0;

                for (int x = rect.X0; x < rect.X1; x++, s++, d++)
                {
                    int idx = (int)(*s * scale);
                    if (idx <= 0) { *d = 0u; continue; }
                    if (idx > lutMax) idx = lutMax;
                    *d = lut[idx];
                }
            }
        }

        return rect;
    }

    /// <summary>Forces the next <see cref="Resolve"/> to rewrite the whole surface.</summary>
    public void InvalidateAll()
    {
        _previous = new DirtyRect { X0 = 0, Y0 = 0, X1 = Width, Y1 = Height };
    }
}
