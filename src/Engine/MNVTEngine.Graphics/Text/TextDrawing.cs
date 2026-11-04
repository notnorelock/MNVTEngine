using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics.Text;

/// <summary>
/// Draws text with a <see cref="SpriteBatch"/>: every glyph is a quad from a <see cref="GlyphAtlas"/> page, so text
/// batches with sprites that share the page (usually one draw call per page). For text in world space (Y up) or in a
/// pixel camera; UI uses its own geometry with the same atlas.
/// </summary>
public static class TextDrawing
{
    /// <summary>Draws a laid-out text with its top-left corner at <paramref name="topLeft"/> (world units, Y up).</summary>
    /// <param name="unitsPerPixel">World units per layout pixel (1 for a pixel camera).</param>
    public static void DrawText(this SpriteBatch batch, GlyphAtlas atlas, TextLayout layout, Vector2 topLeft, Color color, float unitsPerPixel = 1f, float depth = 0f)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(layout);
        foreach (var glyph in layout.Glyphs)
        {
            var region = atlas.GetGlyph(glyph.Font, glyph.Glyph, layout.Style.Size);
            if (region.IsEmpty)
            {
                continue;
            }

            // Layout is y-down from the top; the world is y-up: flip around the top edge.
            var x = MathF.Round(glyph.X + region.Offset.X);
            var y = MathF.Round(glyph.Baseline) + region.Offset.Y;
            var size = region.Size * unitsPerPixel;
            var bottomLeft = new Vector2(topLeft.X + (x * unitsPerPixel), topLeft.Y - ((y + region.Size.Y) * unitsPerPixel));
            batch.Draw(region.Page, bottomLeft, size, color, 0f, Vector2.Zero, region.Source, depth: depth);
        }
    }

    /// <summary>Lays out and draws a string; <paramref name="anchor"/> (0..1 per axis, (0, 1) = top-left) is the point of the text placed at <paramref name="position"/>.</summary>
    public static Vector2 DrawText(this SpriteBatch batch, GlyphAtlas atlas, string text, TextStyle style, Vector2 position, Color color, Vector2 anchor, float unitsPerPixel = 1f)
    {
        var layout = TextLayout.Create(text, style);
        var size = layout.Size * unitsPerPixel;
        var topLeft = new Vector2(position.X - (size.X * anchor.X), position.Y + (size.Y * (1f - anchor.Y)));
        batch.DrawText(atlas, layout, topLeft, color, unitsPerPixel);
        return size;
    }
}
