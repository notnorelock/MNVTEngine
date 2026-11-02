using System.Globalization;

namespace MNVTEngine.Mathematics;

/// <summary>Axis-aligned bounding box defined by a center and half-size (extents). Containment is inclusive.</summary>
public struct Bounds : IEquatable<Bounds>, IFormattable
{
    public Vector3 Center;

    /// <summary>Half the size on each axis. Expected to be non-negative.</summary>
    public Vector3 Extents;

    public Bounds(Vector3 center, Vector3 size)
    {
        Center = center;
        Extents = size * 0.5f;
    }

    public static Bounds FromMinMax(Vector3 min, Vector3 max) => new((min + max) * 0.5f, max - min);

    public readonly Vector3 Size => Extents * 2f;
    public readonly Vector3 Min => Center - Extents;
    public readonly Vector3 Max => Center + Extents;

    public readonly bool Contains(Vector3 point)
    {
        Vector3 min = Min, max = Max;
        return point.X >= min.X && point.X <= max.X
            && point.Y >= min.Y && point.Y <= max.Y
            && point.Z >= min.Z && point.Z <= max.Z;
    }

    /// <summary>True when the boxes overlap or touch.</summary>
    public readonly bool Intersects(Bounds other)
    {
        Vector3 aMin = Min, aMax = Max, bMin = other.Min, bMax = other.Max;
        return aMin.X <= bMax.X && bMin.X <= aMax.X
            && aMin.Y <= bMax.Y && bMin.Y <= aMax.Y
            && aMin.Z <= bMax.Z && bMin.Z <= aMax.Z;
    }

    public readonly Vector3 ClosestPoint(Vector3 point) => Vector3.Min(Vector3.Max(point, Min), Max);

    public void Encapsulate(Vector3 point) => this = FromMinMax(Vector3.Min(Min, point), Vector3.Max(Max, point));

    public void Encapsulate(Bounds other) => this = FromMinMax(Vector3.Min(Min, other.Min), Vector3.Max(Max, other.Max));

    /// <summary>Grows the total size by <paramref name="amount"/> on every axis.</summary>
    public void Expand(float amount) => Extents += Vector3.One * (amount * 0.5f);

    /// <summary>Slab test. <paramref name="distance"/> is 0 when the ray starts inside.</summary>
    public readonly bool IntersectRay(Ray ray, out float distance)
    {
        distance = 0f;
        var tMin = 0f;
        var tMax = float.PositiveInfinity;
        Vector3 min = Min, max = Max;

        for (var axis = 0; axis < 3; axis++)
        {
            var origin = Component(ray.Origin, axis);
            var direction = Component(ray.Direction, axis);
            var lo = Component(min, axis);
            var hi = Component(max, axis);

            if (MathF.Abs(direction) < Mathf.Epsilon)
            {
                if (origin < lo || origin > hi)
                {
                    return false;
                }

                continue;
            }

            var t1 = (lo - origin) / direction;
            var t2 = (hi - origin) / direction;
            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
            }

            tMin = MathF.Max(tMin, t1);
            tMax = MathF.Min(tMax, t2);
            if (tMin > tMax)
            {
                return false;
            }
        }

        distance = tMin;
        return true;
    }

    private static float Component(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    public static bool operator ==(Bounds a, Bounds b) => a.Equals(b);
    public static bool operator !=(Bounds a, Bounds b) => !a.Equals(b);

    public readonly bool Equals(Bounds other) => Center.Equals(other.Center) && Extents.Equals(other.Extents);
    public override readonly bool Equals(object? obj) => obj is Bounds other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(Center, Extents);
    public override readonly string ToString() => ToString("G", CultureInfo.InvariantCulture);

    public readonly string ToString(string? format, IFormatProvider? formatProvider) =>
        $"Bounds(center: {Center.ToString(format, formatProvider)}, extents: {Extents.ToString(format, formatProvider)})";
}
