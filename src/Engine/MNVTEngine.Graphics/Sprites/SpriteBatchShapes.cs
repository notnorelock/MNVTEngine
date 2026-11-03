using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics;

/// <summary>Debug and UI primitives drawn as quads with the batch's white texture, so they batch with sprites.</summary>
public static class SpriteBatchShapes
{
    public static void DrawLine(this SpriteBatch batch, Vector2 from, Vector2 to, Color color, float thickness = 1f, float depth = 0f)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var delta = to - from;
        var length = delta.Magnitude;
        if (length <= 0f)
        {
            return;
        }

        var angle = MathF.Atan2(delta.Y, delta.X) * Mathf.Rad2Deg;
        batch.Draw(batch.WhitePixel, (from + to) * 0.5f, new Vector2(length, thickness), color, angle, depth: depth);
    }

    public static void FillRectangle(this SpriteBatch batch, Rect rect, Color color, float depth = 0f)
    {
        ArgumentNullException.ThrowIfNull(batch);
        batch.Draw(batch.WhitePixel, rect.Min, rect.Max - rect.Min, color, pivot: Vector2.Zero, depth: depth);
    }

    /// <summary>Outline drawn inside the rectangle so it never exceeds the given bounds.</summary>
    public static void DrawRectangle(this SpriteBatch batch, Rect rect, Color color, float thickness = 1f, float depth = 0f)
    {
        Vector2 min = rect.Min, max = rect.Max;
        batch.FillRectangle(Rect.FromMinMax(min, new Vector2(max.X, min.Y + thickness)), color, depth);
        batch.FillRectangle(Rect.FromMinMax(new Vector2(min.X, max.Y - thickness), max), color, depth);
        batch.FillRectangle(Rect.FromMinMax(new Vector2(min.X, min.Y + thickness), new Vector2(min.X + thickness, max.Y - thickness)), color, depth);
        batch.FillRectangle(Rect.FromMinMax(new Vector2(max.X - thickness, min.Y + thickness), new Vector2(max.X, max.Y - thickness)), color, depth);
    }

    public static void DrawCircle(this SpriteBatch batch, Vector2 center, float radius, Color color, float thickness = 1f, int segments = 32, float depth = 0f)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(segments, 3);
        var previous = center + new Vector2(radius, 0f);
        for (var i = 1; i <= segments; i++)
        {
            var (sin, cos) = MathF.SinCos(i * Mathf.Tau / segments);
            var next = center + new Vector2(cos * radius, sin * radius);
            batch.DrawLine(previous, next, color, thickness, depth);
            previous = next;
        }
    }
}
