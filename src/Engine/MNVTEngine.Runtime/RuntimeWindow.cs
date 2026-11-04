using MNVTEngine.Assets;
using MNVTEngine.Graphics;
using MNVTEngine.Platform;

namespace MNVTEngine.Runtime;

/// <summary>
/// <see cref="Application.Window"/> for a running game: the GLFW window plus the graphics device (VSync is the
/// device's present mode). Icons are decoded from texture assets and scaled to the usual icon sizes.
/// </summary>
public sealed class RuntimeWindow(GameWindow window, IGraphicsDevice device, AssetDatabase assets) : IApplicationWindow
{
    private static readonly int[] IconSizes = [16, 24, 32, 48, 64, 128, 256];

    public string Title
    {
        get => window.Title;
        set => window.Title = value ?? "";
    }

    public int Width => window.WindowWidth;

    public int Height => window.WindowHeight;

    public int FramebufferWidth => window.FramebufferWidth;

    public int FramebufferHeight => window.FramebufferHeight;

    public WindowMode Mode
    {
        get => window.Mode;
        set => window.Mode = value;
    }

    public bool VSync
    {
        get => device.VSync;
        set => device.VSync = value;
    }

    public bool Resizable
    {
        get => window.Resizable;
        set => window.Resizable = value;
    }

    public bool TopMost
    {
        get => window.TopMost;
        set => window.TopMost = value;
    }

    public (int X, int Y) Position
    {
        get => window.Position;
        set => window.Position = value;
    }

    public void SetSize(int width, int height) => window.SetSize(width, height);

    public void Center() => window.Center();

    public void SetIcon(AssetRef texture)
    {
        var path = assets.Get(texture.Guid)?.Path ?? throw new InvalidDataException($"{texture} is not an asset of this game.");
        window.SetIcon(IconsFrom(Image.Decode(assets.ReadAllBytes(texture.Guid), path)));
    }

    /// <summary>The image at every standard icon size up to its own (box-filtered down), plus the original.</summary>
    public static IReadOnlyList<WindowIcon> IconsFrom(Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var icons = new List<WindowIcon> { new(image.Width, image.Height, [.. image.Pixels]) };
        var largest = Math.Max(image.Width, image.Height);
        foreach (var size in IconSizes.Where(s => s < largest))
        {
            var w = Math.Max(1, image.Width * size / largest);
            var h = Math.Max(1, image.Height * size / largest);
            icons.Add(new WindowIcon(w, h, Downscale(image, w, h)));
        }

        return icons;
    }

    private static byte[] Downscale(Image image, int width, int height)
    {
        var result = new byte[width * height * 4];
        var pixels = image.Pixels;
        for (var y = 0; y < height; y++)
        {
            int y0 = y * image.Height / height, y1 = Math.Max(y0 + 1, (y + 1) * image.Height / height);
            for (var x = 0; x < width; x++)
            {
                int x0 = x * image.Width / width, x1 = Math.Max(x0 + 1, (x + 1) * image.Width / width);
                long r = 0, g = 0, b = 0, a = 0, n = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var i = ((sy * image.Width) + sx) * 4;
                        var alpha = pixels[i + 3];
                        r += pixels[i] * alpha;
                        g += pixels[i + 1] * alpha;
                        b += pixels[i + 2] * alpha;
                        a += alpha;
                        n++;
                    }
                }

                var o = ((y * width) + x) * 4;
                if (a > 0)
                {
                    // Alpha-weighted, so transparent pixels do not darken the edges.
                    (result[o], result[o + 1], result[o + 2]) = ((byte)(r / a), (byte)(g / a), (byte)(b / a));
                }

                result[o + 3] = (byte)(a / n);
            }
        }

        return result;
    }
}
