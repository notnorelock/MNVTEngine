using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Text;
using MNVTEngine.Mathematics;

namespace MNVTEngine.UI;

/// <summary>
/// Records UI drawing as textured quads in framebuffer pixels: boxes with rounded corners and borders, images and
/// text. Quads are clipped on the CPU (rectangle and UVs trimmed), so clipping never splits a batch. Solid shapes,
/// corner pieces and glyphs all come from the <see cref="GlyphAtlas"/>, so a typical UI is one texture and one draw.
/// Elements receive logical coordinates; the painter scales them by <see cref="PixelScale"/>.
/// </summary>
public sealed class UIPainter
{
    private readonly GlyphAtlas _atlas;
    private readonly List<SpriteVertex> _vertices;
    private readonly List<UIDrawRun> _runs;
    private readonly Stack<Rect> _clips = new();
    private readonly Stack<float> _opacity = new();
    private Rect _clip;
    private float _alpha = 1f;

    internal UIPainter(GlyphAtlas atlas, float pixelScale, List<SpriteVertex> vertices, List<UIDrawRun> runs, Rect viewport)
    {
        _atlas = atlas;
        PixelScale = pixelScale;
        _vertices = vertices;
        _runs = runs;
        _clip = Scale(viewport);
    }

    /// <summary>Framebuffer pixels per logical pixel.</summary>
    public float PixelScale { get; }

    public GlyphAtlas Atlas => _atlas;

    /// <summary>Restricts drawing to <paramref name="logical"/> (intersected with the current clip) until <see cref="PopClip"/>.</summary>
    public void PushClip(Rect logical)
    {
        _clips.Push(_clip);
        _clip = LayoutEngine.Intersect(_clip, Scale(logical));
    }

    public void PopClip() => _clip = _clips.Pop();

    /// <summary>Multiplies the alpha of everything drawn until <see cref="PopOpacity"/>.</summary>
    public void PushOpacity(float opacity)
    {
        _opacity.Push(_alpha);
        _alpha *= opacity;
    }

    public void PopOpacity() => _alpha = _opacity.Pop();

    /// <summary>Shadow, background and border of a style.</summary>
    public void DrawBox(Rect bounds, in ResolvedStyle style)
    {
        if (style.Shadow.A > 0f)
        {
            FillRect(new Rect(bounds.X + style.ShadowOffset.X, bounds.Y + style.ShadowOffset.Y, bounds.Width, bounds.Height), style.Shadow, style.CornerRadius);
        }

        if (style.Background.A > 0f)
        {
            FillRect(bounds, style.Background, style.CornerRadius);
        }

        if (style.BorderWidth > 0f && style.BorderColor.A > 0f)
        {
            StrokeRect(bounds, style.BorderColor, style.BorderWidth, style.CornerRadius);
        }
    }

    public void FillRect(Rect logical, Color color, float cornerRadius = 0f)
    {
        var r = Snap(Scale(logical));
        var radius = (int)MathF.Min(MathF.Round(cornerRadius * PixelScale), MathF.Floor(MathF.Min(r.Width, r.Height) / 2f));
        if (radius <= 0)
        {
            Solid(r, color);
            return;
        }

        var circle = Circle(radius, 0);
        Corners(r, radius, circle, color);
        Solid(new Rect(r.X + radius, r.Y, r.Width - (2 * radius), r.Height), color);
        Solid(new Rect(r.X, r.Y + radius, radius, r.Height - (2 * radius)), color);
        Solid(new Rect(r.X + r.Width - radius, r.Y + radius, radius, r.Height - (2 * radius)), color);
    }

    public void StrokeRect(Rect logical, Color color, float width, float cornerRadius = 0f)
    {
        var r = Snap(Scale(logical));
        var t = Math.Max(1, (int)MathF.Round(width * PixelScale));
        var radius = (int)MathF.Min(MathF.Round(cornerRadius * PixelScale), MathF.Floor(MathF.Min(r.Width, r.Height) / 2f));
        if (radius < t)
        {
            Solid(new Rect(r.X, r.Y, r.Width, t), color);
            Solid(new Rect(r.X, r.Y + r.Height - t, r.Width, t), color);
            Solid(new Rect(r.X, r.Y + t, t, r.Height - (2 * t)), color);
            Solid(new Rect(r.X + r.Width - t, r.Y + t, t, r.Height - (2 * t)), color);
            return;
        }

        Corners(r, radius, Circle(radius, t), color);
        Solid(new Rect(r.X + radius, r.Y, r.Width - (2 * radius), t), color);
        Solid(new Rect(r.X + radius, r.Y + r.Height - t, r.Width - (2 * radius), t), color);
        Solid(new Rect(r.X, r.Y + radius, t, r.Height - (2 * radius)), color);
        Solid(new Rect(r.X + r.Width - t, r.Y + radius, t, r.Height - (2 * radius)), color);
    }

    /// <summary>Draws a texture into <paramref name="logical"/>; <paramref name="source"/> in texels (top-left origin), null for all.</summary>
    public void DrawImage(Texture2D texture, Rect logical, Color tint, Rect? source = null)
    {
        ArgumentNullException.ThrowIfNull(texture);
        var src = source ?? new Rect(0, 0, texture.Width, texture.Height);
        if (texture.IsFlippedVertically)
        {
            src = new Rect(src.X, texture.Height - src.Y, src.Width, -src.Height);
        }

        Quad(texture, Scale(logical), src, tint);
    }

    /// <summary>Draws text laid out at this painter's pixel scale (see <see cref="TextBlock"/>) with its top-left at <paramref name="logicalTopLeft"/>.</summary>
    public void DrawText(TextLayout layout, Vector2 logicalTopLeft, Color color)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var origin = new Vector2(MathF.Round(logicalTopLeft.X * PixelScale), MathF.Round(logicalTopLeft.Y * PixelScale));
        foreach (var glyph in layout.Glyphs)
        {
            var region = _atlas.GetGlyph(glyph.Font, glyph.Glyph, layout.Style.Size);
            if (region.IsEmpty)
            {
                continue;
            }

            var x = origin.X + MathF.Round(glyph.X) + region.Offset.X;
            var y = origin.Y + MathF.Round(glyph.Baseline) + region.Offset.Y;
            Quad(region.Page, new Rect(x, y, region.Size.X, region.Size.Y), region.Source, color);
        }
    }

    /// <summary>A solid rectangle in logical coordinates, without rounding (selections, carets).</summary>
    public void FillSolid(Rect logical, Color color) => Solid(Scale(logical), color);

    internal Rect Scale(Rect r) => new(r.X * PixelScale, r.Y * PixelScale, r.Width * PixelScale, r.Height * PixelScale);

    private static Rect Snap(Rect r)
    {
        var x0 = MathF.Round(r.X);
        var y0 = MathF.Round(r.Y);
        return new Rect(x0, y0, MathF.Round(r.X + r.Width) - x0, MathF.Round(r.Y + r.Height) - y0);
    }

    private void Solid(Rect r, Color color)
    {
        if (r.Width > 0 && r.Height > 0)
        {
            var white = _atlas.White;
            Quad(white.Page, r, white.Source, color);
        }
    }

    private void Corners(Rect r, int radius, AtlasRegion circle, Color color)
    {
        var s = circle.Source;
        Quad(circle.Page, new Rect(r.X, r.Y, radius, radius), new Rect(s.X, s.Y, radius, radius), color);
        Quad(circle.Page, new Rect(r.X + r.Width - radius, r.Y, radius, radius), new Rect(s.X + radius, s.Y, radius, radius), color);
        Quad(circle.Page, new Rect(r.X, r.Y + r.Height - radius, radius, radius), new Rect(s.X, s.Y + radius, radius, radius), color);
        Quad(circle.Page, new Rect(r.X + r.Width - radius, r.Y + r.Height - radius, radius, radius), new Rect(s.X + radius, s.Y + radius, radius, radius), color);
    }

    /// <summary>An anti-aliased disc (thickness 0) or ring of a pixel radius, generated once into the atlas.</summary>
    private AtlasRegion Circle(int radius, int thickness) =>
        _atlas.GetImage(((long)radius << 16) | (uint)thickness, 2 * radius, 2 * radius, (pixels, stride) =>
        {
            for (var y = 0; y < 2 * radius; y++)
            {
                for (var x = 0; x < 2 * radius; x++)
                {
                    var dx = x + 0.5f - radius;
                    var dy = y + 0.5f - radius;
                    var d = MathF.Sqrt((dx * dx) + (dy * dy));
                    var alpha = Math.Clamp(radius - d + 0.5f, 0f, 1f);
                    if (thickness > 0)
                    {
                        alpha *= Math.Clamp(d - (radius - thickness) + 0.5f, 0f, 1f);
                    }

                    var o = (y * stride) + (x * 4);
                    pixels[o] = pixels[o + 1] = pixels[o + 2] = 255;
                    pixels[o + 3] = (byte)(alpha * 255f);
                }
            }
        });

    /// <summary>Appends a quad, clipped to the current clip (trimming its texture coordinates to match).</summary>
    private void Quad(Texture2D texture, Rect dest, Rect src, Color color)
    {
        var x0 = MathF.Max(dest.X, _clip.X);
        var y0 = MathF.Max(dest.Y, _clip.Y);
        var x1 = MathF.Min(dest.X + dest.Width, _clip.X + _clip.Width);
        var y1 = MathF.Min(dest.Y + dest.Height, _clip.Y + _clip.Height);
        if (x1 <= x0 || y1 <= y0 || dest.Width <= 0 || dest.Height <= 0)
        {
            return;
        }

        float U(float x) => (src.X + ((x - dest.X) / dest.Width * src.Width)) / texture.Width;
        float V(float y) => (src.Y + ((y - dest.Y) / dest.Height * src.Height)) / texture.Height;
        var packed = new Color(color.R, color.G, color.B, color.A * _alpha).ToRgba32();
        _vertices.Add(new SpriteVertex(new Vector2(x0, y0), new Vector2(U(x0), V(y0)), packed));
        _vertices.Add(new SpriteVertex(new Vector2(x1, y0), new Vector2(U(x1), V(y0)), packed));
        _vertices.Add(new SpriteVertex(new Vector2(x1, y1), new Vector2(U(x1), V(y1)), packed));
        _vertices.Add(new SpriteVertex(new Vector2(x0, y1), new Vector2(U(x0), V(y1)), packed));
        if (_runs.Count > 0 && ReferenceEquals(_runs[^1].Texture, texture))
        {
            _runs[^1] = _runs[^1] with { Quads = _runs[^1].Quads + 1 };
        }
        else
        {
            _runs.Add(new UIDrawRun(texture, (_vertices.Count / 4) - 1, 1));
        }
    }
}

/// <summary>Consecutive quads with one texture.</summary>
internal readonly record struct UIDrawRun(Texture2D Texture, int FirstQuad, int Quads);
