using MNVTEngine.Terrain.Elements;
using MNVTEngine.Terrain.Geometry;

namespace MNVTEngine.Terrain.Meshing;

/// <summary>
/// Terrain booleans (TerrainHeaven's BooleanManager): which elements cut a terrain tile, and cutting a triangulated
/// surface by them, restoring heights from the source triangles.
/// </summary>
public static class TerrainBooleans
{
    private const double SnapScale = 1e6;
    private const double MinArea = 1e-8;
    private const double ContainEpsilon = 1e-5;

    public static IReadOnlyList<OccupiedTriangle> CutTerrainSurface(Elements.Terrain terrain, IReadOnlyList<WorldElement> elements)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        return CutOccupiedSurface(terrain.GetOccupiedArea(), GetTerrainCutAreas(terrain, elements));
    }

    public static IReadOnlyList<OccupiedTriangle> CutOccupiedSurface(IReadOnlyList<OccupiedTriangle> surface, IReadOnlyList<OccupiedTriangle> cutAreas)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(cutAreas);
        var surfaceArea = ToMultiPolygon(surface);
        if (surfaceArea.Count == 0)
        {
            return [];
        }

        if (cutAreas.Count == 0)
        {
            return TriangulateMultiPolygon(surfaceArea, surface);
        }

        var cutters = ToMultiPolygon(cutAreas);
        if (cutters.Count == 0)
        {
            return TriangulateMultiPolygon(surfaceArea, surface);
        }

        var difference = PolygonClipping.Difference(surfaceArea, cutters);
        return TriangulateMultiPolygon(difference, [.. cutAreas, .. surface]);
    }

    /// <summary>The occupied triangles of every element that cuts this tile and overlaps it.</summary>
    public static List<OccupiedTriangle> GetTerrainCutAreas(Elements.Terrain terrain, IReadOnlyList<WorldElement> elements)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(elements);
        var terrainBounds = GetBounds(terrain.GetOccupiedArea());
        var cutAreas = new List<OccupiedTriangle>();
        foreach (var element in elements)
        {
            if (element == terrain || element is Elements.Terrain || !element.CutsTerrainSurface())
            {
                continue;
            }

            var occupied = element.GetOccupiedArea();
            if (occupied.Count == 0 || !BoundsOverlap(terrainBounds, GetBounds(occupied)))
            {
                continue;
            }

            cutAreas.AddRange(occupied);
        }

        return cutAreas;
    }

    private static List<List<List<Vec2>>> ToMultiPolygon(IReadOnlyList<OccupiedTriangle> triangles)
    {
        var polygons = new List<List<List<List<Vec2>>>>();
        foreach (var tri in triangles)
        {
            var a = SnapPoint(tri.A.Xz);
            var b = SnapPoint(tri.B.Xz);
            var c = SnapPoint(tri.C.Xz);
            if (Math.Abs((b - a).Cross(c - a)) * 0.5 < MinArea)
            {
                continue;
            }

            polygons.Add([[[a, b, c, a]]]);
        }

        if (polygons.Count == 0)
        {
            return [];
        }

        var result = polygons[0];
        for (var i = 1; i < polygons.Count; i += 32)
        {
            result = PolygonClipping.Union([result, .. polygons.Skip(i).Take(32)]);
        }

        return result;
    }

    private static Vec2 SnapPoint(Vec2 p) => new(MathUtil.JsRound(p.X * SnapScale) / SnapScale, MathUtil.JsRound(p.Y * SnapScale) / SnapScale);

    private static List<OccupiedTriangle> TriangulateMultiPolygon(List<List<List<Vec2>>> multi, IReadOnlyList<OccupiedTriangle> sourceTriangles)
    {
        var result = new List<OccupiedTriangle>();
        foreach (var polygon in multi)
        {
            if (polygon.Count == 0)
            {
                continue;
            }

            var contour = ToRing(polygon[0]);
            if (contour.Count < 3)
            {
                continue;
            }

            var holes = polygon.Skip(1).Select(ToRing).Where(ring => ring.Count >= 3).ToList();
            var points = contour.Concat(holes.SelectMany(h => h)).ToList();
            foreach (var face in ShapeUtils.TriangulateShape(contour, holes))
            {
                result.Add(new OccupiedTriangle(
                    RestorePoint3D(points[face[0]], sourceTriangles),
                    RestorePoint3D(points[face[1]], sourceTriangles),
                    RestorePoint3D(points[face[2]], sourceTriangles)));
            }
        }

        return result;
    }

    private static List<Vec2> ToRing(List<Vec2> ring)
    {
        var result = new List<Vec2>();
        for (var i = 0; i < ring.Count; i++)
        {
            if (i == ring.Count - 1 && ring[i] == ring[0])
            {
                continue;
            }

            result.Add(ring[i]);
        }

        return result;
    }

    private static Vec3 RestorePoint3D(Vec2 point, IReadOnlyList<OccupiedTriangle> sourceTriangles)
    {
        foreach (var tri in sourceTriangles)
        {
            if (GetBarycentricXZ(point, tri) is { } b)
            {
                return new Vec3(point.X, (tri.A.Y * b.U) + (tri.B.Y * b.V) + (tri.C.Y * b.W), point.Y);
            }
        }

        return new Vec3(point.X, 0, point.Y);
    }

    private static (double U, double V, double W)? GetBarycentricXZ(Vec2 point, OccupiedTriangle tri)
    {
        var denominator = ((tri.B.Z - tri.C.Z) * (tri.A.X - tri.C.X)) + ((tri.C.X - tri.B.X) * (tri.A.Z - tri.C.Z));
        if (Math.Abs(denominator) < MinArea)
        {
            return null;
        }

        var u = (((tri.B.Z - tri.C.Z) * (point.X - tri.C.X)) + ((tri.C.X - tri.B.X) * (point.Y - tri.C.Z))) / denominator;
        var v = (((tri.C.Z - tri.A.Z) * (point.X - tri.C.X)) + ((tri.A.X - tri.C.X) * (point.Y - tri.C.Z))) / denominator;
        var w = 1 - u - v;
        if (u < -ContainEpsilon || v < -ContainEpsilon || w < -ContainEpsilon || u > 1 + ContainEpsilon || v > 1 + ContainEpsilon || w > 1 + ContainEpsilon)
        {
            return null;
        }

        return (u, v, w);
    }

    internal static TerrainRect GetBounds(IReadOnlyList<OccupiedTriangle> triangles)
    {
        double minX = double.PositiveInfinity, minZ = double.PositiveInfinity, maxX = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        foreach (var tri in triangles)
        {
            minX = Math.Min(minX, Math.Min(tri.A.X, Math.Min(tri.B.X, tri.C.X)));
            minZ = Math.Min(minZ, Math.Min(tri.A.Z, Math.Min(tri.B.Z, tri.C.Z)));
            maxX = Math.Max(maxX, Math.Max(tri.A.X, Math.Max(tri.B.X, tri.C.X)));
            maxZ = Math.Max(maxZ, Math.Max(tri.A.Z, Math.Max(tri.B.Z, tri.C.Z)));
        }

        return new TerrainRect(minX, maxX, minZ, maxZ);
    }

    private static bool BoundsOverlap(TerrainRect a, TerrainRect b) => a.MinX <= b.MaxX && a.MaxX >= b.MinX && a.MinZ <= b.MaxZ && a.MaxZ >= b.MinZ;
}
