namespace MNVTEngine.Mathematics;

/// <summary>Clip-space depth range of a projection matrix.</summary>
public enum DepthRange
{
    /// <summary>NDC z in [-1, 1] (<see cref="Matrix4x4.Perspective"/>, <see cref="Matrix4x4.Ortho"/>).</summary>
    MinusOneToOne,

    /// <summary>NDC z in [0, 1], Vulkan's native range (<see cref="Matrix4x4.PerspectiveZeroToOne"/>, <see cref="Matrix4x4.OrthoZeroToOne"/>).</summary>
    ZeroToOne,
}

/// <summary>
/// The six planes of a view volume, facing inwards (left, right, bottom, top, near, far), for culling. Built from a
/// view-projection matrix (column-vector convention).
/// </summary>
public sealed class Frustum
{
    private readonly Plane[] _planes;

    private Frustum(Plane[] planes) => _planes = planes;

    /// <summary>Left, right, bottom, top, near, far; normals point inside.</summary>
    public ReadOnlySpan<Plane> Planes => _planes;

    public static Frustum FromViewProjection(Matrix4x4 m, DepthRange depth = DepthRange.ZeroToOne)
    {
        Vector4 Row(int r) => new(m[r, 0], m[r, 1], m[r, 2], m[r, 3]);
        static Plane P(Vector4 v) => new(new Vector3(v.X, v.Y, v.Z), v.W);
        Vector4 r0 = Row(0), r1 = Row(1), r2 = Row(2), r3 = Row(3);
        return new Frustum(
        [
            P(r3 + r0), P(r3 - r0), P(r3 + r1), P(r3 - r1),
            P(depth == DepthRange.ZeroToOne ? r2 : r3 + r2), P(r3 - r2),
        ]);
    }

    public bool Contains(Vector3 point)
    {
        foreach (var plane in _planes)
        {
            if (plane.GetDistanceToPoint(point) < 0f)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// False when the box is certainly outside. Conservative: a box near a frustum corner may report true while
    /// outside (the usual plane test), which only costs a draw.
    /// </summary>
    public bool Intersects(Bounds bounds)
    {
        foreach (var plane in _planes)
        {
            var n = plane.Normal;
            var reach = (bounds.Extents.X * MathF.Abs(n.X)) + (bounds.Extents.Y * MathF.Abs(n.Y)) + (bounds.Extents.Z * MathF.Abs(n.Z));
            if (plane.GetDistanceToPoint(bounds.Center) < -reach)
            {
                return false;
            }
        }

        return true;
    }

    public bool Intersects(BoundingSphere sphere)
    {
        foreach (var plane in _planes)
        {
            if (plane.GetDistanceToPoint(sphere.Center) < -sphere.Radius)
            {
                return false;
            }
        }

        return true;
    }
}
