using MNVTEngine.Mathematics;

namespace MNVTEngine.Terrain.Geometry;

public static class RayCasting
{
    /// <summary>Möller–Trumbore against both faces; the distance along the ray (in direction lengths) or null.</summary>
    public static double? IntersectTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c)
    {
        var (e1, e2) = (b - a, c - a);
        var p = direction.Cross(e2);
        double det = e1.Dot(p);
        if (Math.Abs(det) < 1e-12)
        {
            return null;
        }

        var s = origin - a;
        var u = s.Dot(p) / det;
        var q = s.Cross(e1);
        var v = direction.Dot(q) / det;
        var t = e2.Dot(q) / det;
        return u is < 0 or > 1 || v < 0 || u + v > 1 || t < 0 ? null : t;
    }
}
