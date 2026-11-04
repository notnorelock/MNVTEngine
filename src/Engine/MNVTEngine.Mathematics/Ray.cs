using System.Globalization;

namespace MNVTEngine.Mathematics;

/// <summary>Half-line from <see cref="Origin"/> along a unit-length <see cref="Direction"/>.</summary>
public readonly struct Ray : IEquatable<Ray>
{
    /// <param name="direction">Normalized by the constructor.</param>
    public Ray(Vector3 origin, Vector3 direction)
    {
        Origin = origin;
        Direction = direction.Normalized;
    }

    public Vector3 Origin { get; }
    public Vector3 Direction { get; }

    public Vector3 GetPoint(float distance) => Origin + (Direction * distance);

    /// <summary>Hit with triangle a, b, c from either side (Möller–Trumbore); <paramref name="distance"/> along the ray.</summary>
    public bool IntersectTriangle(Vector3 a, Vector3 b, Vector3 c, out float distance)
    {
        distance = 0f;
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(Direction, e2);
        var det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-9f)
        {
            return false;
        }

        var inverse = 1f / det;
        var s = Origin - a;
        var u = Vector3.Dot(s, p) * inverse;
        if (u is < 0f or > 1f)
        {
            return false;
        }

        var q = Vector3.Cross(s, e1);
        var v = Vector3.Dot(Direction, q) * inverse;
        if (v < 0f || u + v > 1f)
        {
            return false;
        }

        distance = Vector3.Dot(e2, q) * inverse;
        return distance >= 0f;
    }

    public static bool operator ==(Ray a, Ray b) => a.Equals(b);
    public static bool operator !=(Ray a, Ray b) => !a.Equals(b);

    public bool Equals(Ray other) => Origin.Equals(other.Origin) && Direction.Equals(other.Direction);
    public override bool Equals(object? obj) => obj is Ray other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Origin, Direction);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"Ray(origin: {Origin}, direction: {Direction})");
}
