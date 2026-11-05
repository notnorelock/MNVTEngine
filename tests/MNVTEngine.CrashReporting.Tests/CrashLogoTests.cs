using MNVTEngine.Graphics.Svg;
using MNVTEngine.Mathematics;

namespace MNVTEngine.CrashReporting.Tests;

/// <summary>
/// The crash window's wordmark is the engine logo (docs/images/mnvtengine-logo.svg) rendered dark, embedded as a PNG
/// because the crash handler loads no engine code. Set <c>MNVT_UPDATE_LOGO=1</c> to regenerate it after the logo changes.
/// </summary>
public sealed class CrashLogoTests
{
    public const float Scale = 0.35f;
    public static readonly Color Ink = new(0.149f, 0.165f, 0.2f, 1f); // #262A33

    [Fact]
    public void TheEmbeddedWordmark_IsTheEngineLogo()
    {
        var svg = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "mnvtengine-logo.svg"));
        var expected = SvgRasterizer.Render(svg, "mnvtengine-logo.svg", Scale, Ink).Image;
        if (Environment.GetEnvironmentVariable("MNVT_UPDATE_LOGO") == "1")
        {
            var target = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Engine", "MNVTEngine.CrashReporting", "Resources", "logo-dark.png"));
            using var file = File.Create(target);
            new StbImageWriteSharp.ImageWriter().WritePng(expected.Pixels, expected.Width, expected.Height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, file);
            return;
        }

        using var stream = typeof(CrashReporter).Assembly.GetManifestResourceStream("MNVTEngine.CrashReporting.logo-dark.png");
        Assert.NotNull(stream);
        var embedded = StbImageSharp.ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        Assert.Equal((expected.Width, expected.Height), (embedded.Width, embedded.Height));
        Assert.Equal(expected.Pixels, embedded.Data); // regenerate with MNVT_UPDATE_LOGO=1 if the logo or the rasterizer changed
    }
}
