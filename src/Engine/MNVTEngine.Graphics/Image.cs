using StbImageSharp;

namespace MNVTEngine.Graphics;

/// <summary>
/// CPU-side RGBA8 image, row 0 at the top. Decodes PNG, JPG, BMP, TGA and other formats supported by stb_image, and
/// rasterizes SVG documents (<see cref="Svg.SvgRasterizer"/>).
/// </summary>
public sealed class Image
{
    public Image(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != width * height * 4)
        {
            throw new ArgumentException($"Expected {width * height * 4} bytes for {width}x{height} RGBA8, got {pixels.Length}.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    /// <exception cref="InvalidDataException">The data is not a decodable image.</exception>
    public static Image Decode(byte[] encoded, string sourceName = "<memory>") => Decode(encoded, sourceName, 1f);

    /// <param name="svgScale">Pixels per SVG unit when the data is an SVG document; ignored for other formats.</param>
    /// <exception cref="InvalidDataException">The data is not a decodable image.</exception>
    public static Image Decode(byte[] encoded, string sourceName, float svgScale)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (Svg.SvgRasterizer.IsSvg(encoded))
        {
            return Svg.SvgRasterizer.Render(encoded, sourceName, svgScale).Image;
        }

        ImageResult result;
        try
        {
            result = ImageResult.FromMemory(encoded, ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // stb reports failures with a plain Exception; rethrow with the asset name attached.
            throw new InvalidDataException($"Cannot decode image '{sourceName}': {ex.Message}", ex);
        }

        return new Image(result.Width, result.Height, result.Data);
    }

    /// <exception cref="InvalidDataException">The file is not a decodable image.</exception>
    public static Image Load(string path) => Decode(File.ReadAllBytes(path), path);

    /// <summary>Solid-color image, useful for placeholder and white textures.</summary>
    public static Image Solid(int width, int height, Mathematics.Color color)
    {
        var pixels = new byte[width * height * 4];
        var rgba = color.ToRgba32();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(pixels.AsSpan(i), rgba);
        }

        return new Image(width, height, pixels);
    }
}
