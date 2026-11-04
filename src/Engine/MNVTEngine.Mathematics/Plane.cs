using System.Globalization;

namespace MNVTEngine.Mathematics;

/// <summary>
/// Plane <c>Dot(Normal, p) + Distance = 0</c> with a unit <see cref="Normal"/>. Points with a positive signed distance
/// are on the side the normal points to.
/// </summary>
public readonly struct Plane : IEquatable<Plane>
{
    /// <param name="normal">Normalized by the constructor.</param>
    public Plane(Vector3 normal, float distance)
    {
        var length = normal.Magnitude;
        Normal = length > Mathf.Epsilon ? normal / length : Vector3.Up;
        Distance = length > Mathf.Epsilon ? distance / length : distance;
    }

    public Vector3 Normal { get; }

    public float Distance { get; }

    /// <summary>The plane through <paramref name="point"/> facing <paramref name="normal"/>.</summary>
    public static Plane FromPointNormal(Vector3 point, Vector3 normal)
    {
        var n = normal.Normalized;
        return new Plane(n, -Vector3.Dot(n, point));
    }

    /// <summary>The plane through three points; the normal follows the right-hand rule (a, b, c counter-clockwise).</summary>
    public static Plane FromPoints(Vector3 a, Vector3 b, Vector3 c) => FromPointNormal(a, Vector3.Cross(b - a, c - a));

    /// <summary>Signed distance: positive on the normal's side.</summary>
    public float GetDistanceToPoint(Vector3 point) => Vector3.Dot(Normal, point) + Distance;

    public bool GetSide(Vector3 point) => GetDistanceToPoint(point) > 0f;

    public Vector3 ClosestPointOnPlane(Vector3 point) => point - (Normal * GetDistanceToPoint(point));

    /// <summary>Where the ray meets the plane (from either side). False when parallel or the plane is behind the ray.</summary>
    public bool IntersectRay(Ray ray, out float distance)
    {
        distance = 0f;
        var denominator = Vector3.Dot(Normal, ray.Direction);
        if (MathF.Abs(denominator) < Mathf.Epsilon)
        {
            return false;
        }

        distance = -GetDistanceToPoint(ray.Origin) / denominator;
        return distance >= 0f;
    }

    public static bool operator ==(Plane a, Plane b) => a.Equals(b);

    public static bool operator !=(Plane a, Plane b) => !a.Equals(b);

    public bool Equals(Plane other) => Normal.Equals(other.Normal) && Distance.Equals(other.Distance);

    public override bool Equals(object? obj) => obj is Plane other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Normal, Distance);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"Plane(normal: {Normal}, distance: {Distance})");
}

/// <summary>Sphere for culling and coarse intersection tests.</summary>
public readonly struct BoundingSphere : IEquatable<BoundingSphere>
{
    public BoundingSphere(Vector3 center, float radius)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(radius);
        Center = center;
        Radius = radius;
    }

    public Vector3 Center { get; }

    public float Radius { get; }

    /// <summary>The sphere around a box (through its corners).</summary>
    public static BoundingSphere FromBounds(Bounds bounds) => new(bounds.Center, bounds.Extents.Magnitude);

    public bool Contains(Vector3 point) => Vector3.DistanceSquared(Center, point) <= Radius * Radius;

    public bool Intersects(BoundingSphere other)
    {
        var reach = Radius + other.Radius;
        return Vector3.DistanceSquared(Center, other.Center) <= reach * reach;
    }

    public bool Intersects(Bounds bounds) => Vector3.DistanceSquared(bounds.ClosestPoint(Center), Center) <= Radius * Radius;

    /// <summary>Nearest hit in front of the ray (0 when it starts inside).</summary>
    public bool IntersectRay(Ray ray, out float distance)
    {
        distance = 0f;
        var toCenter = Center - ray.Origin;
        var along = Vector3.Dot(toCenter, ray.Direction);
        var squared = toCenter.SqrMagnitude - (along * along);
        var radiusSquared = Radius * Radius;
        if (squared > radiusSquared)
        {
            return false;
        }

        var half = MathF.Sqrt(radiusSquared - squared);
        var near = along - half;
        var far = along + half;
        if (far < 0f)
        {
            return false;
        }

        distance = MathF.Max(near, 0f);
        return true;
    }

    public static bool operator ==(BoundingSphere a, BoundingSphere b) => a.Equals(b);

    public static bool operator !=(BoundingSphere a, BoundingSphere b) => !a.Equals(b);

    public bool Equals(BoundingSphere other) => Center.Equals(other.Center) && Radius.Equals(other.Radius);

    public override bool Equals(object? obj) => obj is BoundingSphere other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Center, Radius);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"BoundingSphere(center: {Center}, radius: {Radius})");
}
