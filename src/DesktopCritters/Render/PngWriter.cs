using System.Buffers.Binary;
using System.IO.Compression;

namespace DesktopCritters.Render;

/// <summary>
/// Minimal PNG encoder, used by <c>--preview</c> to dump frames without a window.
///
/// Hand-rolled rather than referencing System.Drawing.Common: the whole app has no
/// third-party or optional dependencies, and this is 60 lines against a 2MB package.
/// </summary>
public static class PngWriter
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>
    /// Writes premultiplied BGRA pixels as a straight-alpha RGBA PNG, optionally
    /// composited over <paramref name="background"/> (0xRRGGBB, or null to keep alpha).
    /// </summary>
    public static unsafe void WriteBgra(string path, IntPtr bits, int width, int height, uint? background)
    {
        var rgba = new byte[width * height * 4];
        uint* src = (uint*)bits;

        double bgR = background.HasValue ? (background.Value >> 16) & 0xFF : 0;
        double bgG = background.HasValue ? (background.Value >> 8) & 0xFF : 0;
        double bgB = background.HasValue ? background.Value & 0xFF : 0;

        for (int i = 0; i < width * height; i++)
        {
            uint p = src[i];
            byte a = (byte)(p >> 24);
            byte r = (byte)(p >> 16);   // premultiplied
            byte g = (byte)(p >> 8);
            byte b = (byte)p;

            int o = i * 4;
            if (background.HasValue)
            {
                double inv = 1.0 - a / 255.0;
                rgba[o + 0] = (byte)Math.Clamp(r + bgR * inv, 0, 255);
                rgba[o + 1] = (byte)Math.Clamp(g + bgG * inv, 0, 255);
                rgba[o + 2] = (byte)Math.Clamp(b + bgB * inv, 0, 255);
                rgba[o + 3] = 255;
            }
            else
            {
                // Un-premultiply so viewers that honour alpha show the true colour.
                double scale = a == 0 ? 0 : 255.0 / a;
                rgba[o + 0] = (byte)Math.Clamp(r * scale, 0, 255);
                rgba[o + 1] = (byte)Math.Clamp(g * scale, 0, 255);
                rgba[o + 2] = (byte)Math.Clamp(b * scale, 0, 255);
                rgba[o + 3] = a;
            }
        }

        WriteRgba(path, rgba, width, height);
    }

    public static void WriteRgba(string path, byte[] rgba, int width, int height)
    {
        using var file = File.Create(path);
        file.Write(Signature);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;    // bit depth
        header[9] = 6;    // colour type: truecolour with alpha
        header[10] = 0;   // deflate
        header[11] = 0;   // adaptive filtering
        header[12] = 0;   // no interlace
        WriteChunk(file, "IHDR", header);

        // Filter byte 0 (None) in front of every scanline.
        var raw = new byte[height * (width * 4 + 1)];
        for (int y = 0; y < height; y++)
        {
            int dst = y * (width * 4 + 1);
            raw[dst] = 0;
            Buffer.BlockCopy(rgba, y * width * 4, raw, dst + 1, width * 4);
        }

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);

        WriteChunk(file, "IDAT", compressed.ToArray());
        WriteChunk(file, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        // CRC covers the type field and the data, but not the length.
        uint crc = Crc32(data, Crc32(typeBytes, 0xFFFFFFFF)) ^ 0xFFFFFFFF;

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data, uint crc)
    {
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
