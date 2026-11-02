using System.Globalization;

namespace MNVTEngine.Mathematics;

/// <summary>
/// Axis-aligned 2D rectangle. (X, Y) is the minimum corner (bottom-left in Y-up world space).
/// Containment is half-open: Min is inside, Max is outside, so adjacent rectangles never both contain a point.
/// </summary>
public struct Rect : IEquatable<Rect>, IFormattable
{
    public float X;
    public float Y;
    public float Width;
    public float Height;

    public Rect(float x, float y, float width, float height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public Rect(Vector2 position, Vector2 size)
        : this(position.X, position.Y, size.X, size.Y)
    {
    }

    public static Rect Zero => default;

    public static Rect FromMinMax(Vector2 min, Vector2 max) => new(min.X, min.Y, max.X - min.X, max.Y - min.Y);

    public readonly Vector2 Position => new(X, Y);
    public readonly Vector2 Size => new(Width, Height);
    public readonly Vector2 Min => new(MathF.Min(X, X + Width), MathF.Min(Y, Y + Height));
    public readonly Vector2 Max => new(MathF.Max(X, X + Width), MathF.Max(Y, Y + Height));
    public readonly Vector2 Center => new(X + (Width * 0.5f), Y + (Height * 0.5f));
    public readonly float Area => MathF.Abs(Width * Height);

    public readonly bool Contains(Vector2 point)
    {
        Vector2 min = Min, max = Max;
        return point.X >= min.X && point.X < max.X && point.Y >= min.Y && point.Y < max.Y;
    }

    /// <summary>True when the interiors intersect; touching edges do not count.</summary>
    public readonly bool Overlaps(Rect other)
    {
        Vector2 aMin = Min, aMax = Max, bMin = other.Min, bMax = other.Max;
        return aMin.X < bMax.X && bMin.X < aMax.X && aMin.Y < bMax.Y && bMin.Y < aMax.Y;
    }

    /// <summary>Overlapping region, if any.</summary>
    public readonly bool TryIntersect(Rect other, out Rect intersection)
    {
        if (!Overlaps(other))
        {
            intersection = default;
            return false;
        }

        intersection = FromMinMax(Vector2.Max(Min, other.Min), Vector2.Min(Max, other.Max));
        return true;
    }

    /// <summary>Smallest rectangle containing both.</summary>
    public static Rect Union(Rect a, Rect b) => FromMinMax(Vector2.Min(a.Min, b.Min), Vector2.Max(a.Max, b.Max));

    public static bool operator ==(Rect a, Rect b) => a.Equals(b);
    public static bool operator !=(Rect a, Rect b) => !a.Equals(b);

    public readonly bool Equals(Rect other) =>
        X.Equals(other.X) && Y.Equals(other.Y) && Width.Equals(other.Width) && Height.Equals(other.Height);

    public override readonly bool Equals(object? obj) => obj is Rect other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(X, Y, Width, Height);
    public override readonly string ToString() => ToString("G", CultureInfo.InvariantCulture);

    public readonly string ToString(string? format, IFormatProvider? formatProvider) =>
        $"Rect(x: {X.ToString(format, formatProvider)}, y: {Y.ToString(format, formatProvider)}, w: {Width.ToString(format, formatProvider)}, h: {Height.ToString(format, formatProvider)})";
}
