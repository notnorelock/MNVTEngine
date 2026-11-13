using System.Buffers.Binary;

namespace MNVTEngine.Graphics;

/// <summary>
/// The low-resolution stand-in of a streamed texture: drawn while the full image loads, and again after the full image
/// was released under memory pressure. Built at export time (artifact <see cref="ArtifactName"/>) as raw RGBA8, so
/// showing it needs no decoding: <c>"MNVTPRXY"</c>, u32 version 1, u32 full width, u32 full height, u32 proxy width,
/// u32 proxy height, then the proxy's pixels, little-endian.
/// </summary>
public static class TextureProxy
{
    public const string ArtifactName = "proxy";

    /// <summary>Longest side of a proxy; textures up to twice this size are not streamed (a proxy would not save much).</summary>
    public const int MaxSize = 64;

    private const int HeaderSize = 8 + (5 * 4);

    /// <summary>True when a texture of this size is worth streaming.</summary>
    public static bool ShouldStream(int width, int height) => Math.Max(width, height) > MaxSize * 2;

    /// <summary>The proxy of <paramref name="full"/>: box-filtered down to at most <see cref="MaxSize"/> on its longest side, encoded.</summary>
    public static byte[] Create(Image full)
    {
        ArgumentNullException.ThrowIfNull(full);
        var scale = (double)MaxSize / Math.Max(full.Width, full.Height);
        var width = Math.Max(1, (int)Math.Round(full.Width * Math.Min(1, scale)));
        var height = Math.Max(1, (int)Math.Round(full.Height * Math.Min(1, scale)));
        var data = new byte[HeaderSize + (width * height * 4)];
        "MNVTPRXY"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), full.Width);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), full.Height);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(20), width);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), height);
        var pixels = data.AsSpan(HeaderSize);
        for (var y = 0; y < height; y++)
        {
            var y0 = y * full.Height / height;
            var y1 = Math.Max(y0 + 1, (y + 1) * full.Height / height);
            for (var x = 0; x < width; x++)
            {
                var x0 = x * full.Width / width;
                var x1 = Math.Max(x0 + 1, (x + 1) * full.Width / width);
                long r = 0, g = 0, b = 0, a = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var i = ((sy * full.Width) + sx) * 4;
                        var alpha = full.Pixels[i + 3];
                        r += full.Pixels[i] * alpha; // premultiplied while averaging: transparent texels add no color fringes
                        g += full.Pixels[i + 1] * alpha;
                        b += full.Pixels[i + 2] * alpha;
                        a += alpha;
                    }
                }

                var o = ((y * width) + x) * 4;
                var count = (x1 - x0) * (y1 - y0);
                pixels[o] = (byte)(a == 0 ? 0 : r / a);
                pixels[o + 1] = (byte)(a == 0 ? 0 : g / a);
                pixels[o + 2] = (byte)(a == 0 ? 0 : b / a);
                pixels[o + 3] = (byte)(a / count);
            }
        }

        return data;
    }

    /// <exception cref="InvalidDataException">Not a proxy, or a malformed one.</exception>
    public static (int FullWidth, int FullHeight, Image Proxy) Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || !data[..8].SequenceEqual("MNVTPRXY"u8) || BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) != 1)
        {
            throw new InvalidDataException("Not a texture proxy.");
        }

        var (fullWidth, fullHeight) = (BinaryPrimitives.ReadInt32LittleEndian(data[12..]), BinaryPrimitives.ReadInt32LittleEndian(data[16..]));
        var (width, height) = (BinaryPrimitives.ReadInt32LittleEndian(data[20..]), BinaryPrimitives.ReadInt32LittleEndian(data[24..]));
        if (fullWidth <= 0 || fullHeight <= 0 || width <= 0 || height <= 0 || width > MaxSize || height > MaxSize || fullWidth > 65536 || fullHeight > 65536
            || data.Length != HeaderSize + (width * height * 4))
        {
            throw new InvalidDataException("Malformed texture proxy.");
        }

        return (fullWidth, fullHeight, new Image(width, height, data[HeaderSize..].ToArray()));
    }
}
