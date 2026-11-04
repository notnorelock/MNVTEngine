using MNVTEngine.Graphics.Text;
using MNVTEngine.Mathematics;

namespace MNVTEngine.UI;

/// <summary>
/// A widget's cached text layout. Text is laid out at the exact framebuffer pixel size it is drawn at (font size x UI
/// scale x DPI), so glyphs are rasterized sharp; logical sizes are that divided by the pixel scale. The layout is
/// rebuilt only when the text, style, width or scale changes.
/// </summary>
public sealed class TextBlock
{
    private (string Text, Font Font, float Size, float Width, TextAlign Align, bool Wrap, float LineHeight, float LetterSpacing, float Scale) _key;

    public TextLayout? Layout { get; private set; }

    /// <summary>Framebuffer pixels per logical pixel the layout was made for.</summary>
    public float PixelScale { get; private set; } = 1f;

    /// <summary>Layouts built (cache misses), for diagnostics.</summary>
    public static long Builds { get; private set; }

    /// <summary>The layout for these parameters (cached). <paramref name="maxWidth"/> in logical pixels; infinite for no limit.</summary>
    public TextLayout Get(UIElement owner, string text, in ResolvedStyle style, float maxWidth, TextAlign align = TextAlign.Left, bool wrap = false, float lineHeight = 1f, float letterSpacing = 0f)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var font = style.Font ?? owner.Context?.DefaultFont ?? throw new InvalidOperationException("Text needs a font: the element is not in a UI context and has none set.");
        var scale = owner.Context is { } context ? context.Scale * context.FramebufferScale : 1f;
        var key = (text, font, style.FontSize, maxWidth, align, wrap, lineHeight, letterSpacing, scale);
        if (Layout is null || key != _key)
        {
            _key = key;
            PixelScale = scale;
            Layout = TextLayout.Create(text, new TextStyle(font, style.FontSize * scale, align, wrap, lineHeight, letterSpacing * scale), maxWidth * scale);
            Builds++;
        }

        return Layout;
    }

    /// <summary>Size of the current layout in logical pixels.</summary>
    public Vector2 Size => Layout is null ? Vector2.Zero : Layout.Size / PixelScale;
}
