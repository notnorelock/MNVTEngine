using MNVTEngine.Mathematics;
using Clipper2Lib;

namespace MNVTEngine.Terrain.Geometry;

/// <summary>
/// Polygon booleans in the shape of the polygon-clipping library TerrainHeaven uses, computed with Clipper2: a
/// multipolygon is a list of polygons, each a list of rings (the outer ring, then its holes), each ring closed (its first
/// point repeated at the end). Results have counter-clockwise outer rings and clockwise holes; islands inside holes are
/// separate polygons.
/// </summary>
public static class PolygonClipping
{
    /// <summary>Decimal places Clipper2 keeps (it computes on an integer grid of this precision).</summary>
    private const int Precision = 8;

    public static List<List<List<Vector2>>> Union(IEnumerable<List<List<List<Vector2>>>> multipolygons) =>
        Execute(ClipType.Union, [.. multipolygons], []);

    public static List<List<List<Vector2>>> Union(params List<List<List<Vector2>>>[] multipolygons) => Union((IEnumerable<List<List<List<Vector2>>>>)multipolygons);

    public static List<List<List<Vector2>>> Difference(List<List<List<Vector2>>> subject, params List<List<List<Vector2>>>[] clips) =>
        Execute(ClipType.Difference, [subject], clips);

    public static List<List<List<Vector2>>> Intersection(List<List<List<Vector2>>> subject, params List<List<List<Vector2>>>[] clips)
    {
        // polygon-clipping intersects every argument together; Clipper2 intersects subject with the union of clips.
        var result = subject;
        foreach (var clip in clips)
        {
            result = Execute(ClipType.Intersection, [result], [clip]);
        }

        return result;
    }

    /// <summary>A multipolygon of one polygon with one ring (convenience for triangles and rectangles).</summary>
    public static List<List<List<Vector2>>> Single(IEnumerable<Vector2> ring) => [[Close([.. ring])]];

    private static List<List<List<Vector2>>> Execute(ClipType type, IReadOnlyList<List<List<List<Vector2>>>> subjects, IReadOnlyList<List<List<List<Vector2>>>> clips)
    {
        var clipper = new ClipperD(Precision) { PreserveCollinear = false }; // polygon-clipping drops collinear points too
        foreach (var multipolygon in subjects)
        {
            clipper.AddSubject(ToPaths(multipolygon));
        }

        foreach (var multipolygon in clips)
        {
            clipper.AddClip(ToPaths(multipolygon));
        }

        var tree = new PolyTreeD();
        clipper.Execute(type, FillRule.NonZero, tree);
        var result = new List<List<List<Vector2>>>();
        AddOuters(tree, result);
        return result;
    }

    /// <summary>polygon-clipping input: every polygon's outer ring counts positive and its holes negative, whatever their winding.</summary>
    private static PathsD ToPaths(List<List<List<Vector2>>> multipolygon)
    {
        var paths = new PathsD();
        foreach (var polygon in multipolygon)
        {
            for (var r = 0; r < polygon.Count; r++)
            {
                var ring = Open(polygon[r]);
                if (ring.Count < 3)
                {
                    continue;
                }

                var path = new PathD(ring.Count);
                foreach (var p in ring)
                {
                    path.Add(new PointD(p.X, p.Y));
                }

                // NonZero over (outer = CCW, hole = CW) gives polygon-clipping's even-hole semantics for each polygon.
                if ((Clipper.Area(path) > 0) != (r == 0))
                {
                    path.Reverse();
                }

                paths.Add(path);
            }
        }

        return paths;
    }

    private static void AddOuters(PolyPathBase parent, List<List<List<Vector2>>> into)
    {
        foreach (PolyPathD outer in parent)
        {
            var polygon = new List<List<Vector2>> { Ring(outer.Polygon!, counterClockwise: true) };
            foreach (PolyPathD hole in outer)
            {
                polygon.Add(Ring(hole.Polygon!, counterClockwise: false));
                AddOuters(hole, into); // islands inside the hole
            }

            if (polygon[0].Count >= 4)
            {
                into.Add(polygon);
            }
        }
    }

    private static List<Vector2> Ring(PathD path, bool counterClockwise)
    {
        var ring = path.Select(p => new Vector2(p.x, p.y)).ToList();
        if ((SignedArea(ring) > 0) != counterClockwise)
        {
            ring.Reverse();
        }

        return Close(ring);
    }

    private static List<Vector2> Close(List<Vector2> ring)
    {
        if (ring.Count > 0 && ring[0] != ring[^1])
        {
            ring.Add(ring[0]);
        }

        return ring;
    }

    private static List<Vector2> Open(List<Vector2> ring) => ring.Count > 1 && ring[0] == ring[^1] ? ring[..^1] : ring;

    private static double SignedArea(List<Vector2> ring)
    {
        var area = 0.0;
        for (var i = 0; i < ring.Count; i++)
        {
            var (a, b) = (ring[i], ring[(i + 1) % ring.Count]);
            area += (a.X * b.Y) - (b.X * a.Y);
        }

        return area / 2;
    }
}
