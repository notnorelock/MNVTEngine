using System.Text;
using MNVTEngine.Graphics.Svg;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics.Tests;

public sealed class SvgRasterizerTests
{
    private static SvgImage Render(string svg, float scale = 1f) => SvgRasterizer.Render(Encoding.UTF8.GetBytes(svg), "test.svg", scale);

    private static (byte R, byte G, byte B, byte A) At(Image image, int x, int y)
    {
        var i = ((y * image.Width) + x) * 4;
        return (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2], image.Pixels[i + 3]);
    }

    [Fact]
    public void Shapes_FillWithTheirColors_AndOutsideStaysTransparent()
    {
        var image = Render("""
            <svg width="40" height="20" xmlns="http://www.w3.org/2000/svg">
              <rect x="0" y="0" width="20" height="20" fill="#ff0000"/>
              <circle cx="30" cy="10" r="8" style="fill: rgb(0, 0, 255)"/>
            </svg>
            """).Image;

        Assert.Equal((40, 20), (image.Width, image.Height));
        Assert.Equal((255, 0, 0, 255), At(image, 10, 10));
        Assert.Equal((0, 0, 255, 255), At(image, 30, 10));
        Assert.Equal(0, At(image, 21, 1).A);       // between the shapes
        Assert.InRange(At(image, 30, 2).A, 1, 254); // the circle's edge is anti-aliased
    }

    [Fact]
    public void PathCommands_Transforms_ViewBox_AndScale()
    {
        // A 10 x 10 viewBox shown at 20 x 20, scaled by 2: one unit is 4 pixels. The path is a relative square with a
        // cubic and an arc; the group moves it by (5, 0) units.
        var image = Render("""
            <svg width="20" height="20" viewBox="0 0 10 10">
              <g transform="translate(5 0)">
                <path d="m0 0h5v5c0 0 0 0-5 0z" fill="lime"/>
              </g>
              <path d="M0 6 A 2 2 0 0 1 4 6 Z" fill="navy"/>
            </svg>
            """, scale: 2f).Image;

        Assert.Equal((40, 40), (image.Width, image.Height));
        Assert.Equal((0, 255, 0, 255), At(image, 30, 10)); // inside the moved square (units 5..10)
        Assert.Equal(0, At(image, 10, 10).A);               // left of it
        Assert.Equal((0, 0, 128, 255), At(image, 8, 20));   // inside the half disc above y = 6 units
        Assert.Equal(0, At(image, 8, 26).A);                // below its flat side
    }

    [Fact]
    public void FillRules_Strokes_Opacity_AndTint()
    {
        const string Rings = """<path d="M0 0H20V20H0Z M5 5H15V15H5Z" fill="black" fill-rule="{0}"/>""";
        var evenOdd = Render($"""<svg width="20" height="20">{Rings.Replace("{0}", "evenodd", StringComparison.Ordinal)}</svg>""").Image;
        var nonZero = Render($"""<svg width="20" height="20">{Rings.Replace("{0}", "nonzero", StringComparison.Ordinal)}</svg>""").Image;
        Assert.Equal(0, At(evenOdd, 10, 10).A);   // the hole
        Assert.Equal(255, At(nonZero, 10, 10).A); // same winding: filled
        Assert.Equal(255, At(evenOdd, 2, 2).A);

        var stroked = Render("""<svg width="20" height="20"><line x1="2" y1="10" x2="18" y2="10" stroke="red" stroke-width="4"/></svg>""").Image;
        Assert.Equal((255, 0, 0, 255), At(stroked, 10, 10));
        Assert.Equal(0, At(stroked, 10, 15).A); // half the width (2) above and below

        var faded = Render("""<svg width="10" height="10"><g opacity="0.5"><rect width="10" height="10" fill="white"/><rect width="10" height="10" fill="white"/></g></svg>""").Image;
        Assert.InRange(At(faded, 5, 5).A, 126, 129); // group opacity applies once, not per child

        var tinted = SvgRasterizer.Render(Encoding.UTF8.GetBytes("""<svg width="4" height="4"><rect width="4" height="4" fill="white"/></svg>"""), "t", 1f, new Color(0f, 0.5f, 1f, 1f)).Image;
        Assert.Equal((0, 128, 255, 255), At(tinted, 2, 2));
    }

    [Fact]
    public void UnsupportedFeatures_AreReported_AndBadDocumentsRejected()
    {
        var svg = Render("""
            <svg width="10" height="10">
              <defs><linearGradient id="g"><stop offset="0" stop-color="#00ff00"/></linearGradient></defs>
              <rect width="10" height="10" fill="url(#g)"/>
              <text x="0" y="5">hi</text>
            </svg>
            """);
        Assert.Equal((0, 255, 0, 255), At(svg.Image, 5, 5)); // the gradient's first stop
        Assert.Contains(svg.Warnings, w => w.Contains("first stop", StringComparison.Ordinal));
        Assert.Contains(svg.Warnings, w => w.Contains("<text>", StringComparison.Ordinal));

        Assert.Throws<InvalidDataException>(() => Render("<svg></svg>"));                        // no size
        Assert.Throws<InvalidDataException>(() => Render("<html/>"));                             // not SVG
        Assert.Throws<InvalidDataException>(() => Render("<svg width=\"9000\" height=\"10\"/>")); // too large
        Assert.Throws<InvalidDataException>(() => Render("""<!DOCTYPE svg [<!ENTITY x "y">]><svg width="1" height="1"/>""")); // DTDs refused
    }

    [Fact]
    public void Decode_RecognizesSvg_AndTheEngineLogoRenders()
    {
        var logo = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "mnvtengine-logo.svg"));
        Assert.True(SvgRasterizer.IsSvg(logo));
        var image = Image.Decode(logo, "mnvtengine-logo.svg", 0.5f);
        Assert.Equal((356, 86), (image.Width, image.Height));
        Assert.Contains(Enumerable.Range(0, image.Width * image.Height), i => image.Pixels[(i * 4) + 3] == 255); // white wordmark
        Assert.False(SvgRasterizer.IsSvg([0x89, (byte)'P', (byte)'N', (byte)'G']));
    }
}
