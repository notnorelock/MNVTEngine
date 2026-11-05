using MNVTEngine.Terrain.Elements;
using MNVTEngine.Terrain.Geometry;

namespace MNVTEngine.Terrain.Meshing;

/// <summary>A point of a cross-section: sideways offset, height above the section's origin, and its texture coordinates.</summary>
public readonly record struct GeometryLineVertex(double Lateral, double Height, double U, double V);

/// <summary>One line of a cross-section, swept along the path into a strip of the named group.</summary>
public readonly record struct GeometryLineSegment(string Group, GeometryLineVertex Start, GeometryLineVertex End);

/// <summary>A cross-section at a point of the path: its origin, its sideways axis and its segments.</summary>
public sealed record GeometryLineSection(Vec3 Origin, Vec3 Right, IReadOnlyList<GeometryLineSegment> Segments);

/// <summary>Sweeps cross-sections along a path into triangle strips (TerrainHeaven's GeometrySweepManager).</summary>
public static class GeometrySweep
{
    /// <summary>Joins matching segments of consecutive sections into quads, grouped by name in first-seen order.</summary>
    public static List<GeometryGroup> SweepSections(IReadOnlyList<GeometryLineSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var groups = new List<GeometryGroup>();
        var byName = new Dictionary<string, GeometryGroup>(StringComparer.Ordinal);
        for (var i = 0; i < sections.Count - 1; i++)
        {
            var current = sections[i];
            var next = sections[i + 1];
            var count = Math.Min(current.Segments.Count, next.Segments.Count);
            for (var s = 0; s < count; s++)
            {
                var currentSegment = current.Segments[s];
                var nextSegment = next.Segments[s];
                if (currentSegment.Group != nextSegment.Group)
                {
                    continue;
                }

                var bl = Place(current, currentSegment.Start);
                var br = Place(current, currentSegment.End);
                var tl = Place(next, nextSegment.Start);
                var tr = Place(next, nextSegment.End);
                if (!byName.TryGetValue(currentSegment.Group, out var group))
                {
                    byName[currentSegment.Group] = group = new GeometryGroup(currentSegment.Group, []);
                    groups.Add(group);
                }

                group.Triangles.Add(new Triangle(bl, br, tr, Uv(currentSegment.Start), Uv(currentSegment.End), Uv(nextSegment.End)));
                group.Triangles.Add(new Triangle(bl, tr, tl, Uv(currentSegment.Start), Uv(nextSegment.End), Uv(nextSegment.Start)));
            }
        }

        return groups;
    }

    private static Vec3 Place(GeometryLineSection section, GeometryLineVertex vertex) =>
        section.Origin + (section.Right * vertex.Lateral) + new Vec3(0, vertex.Height, 0);

    private static Vec2 Uv(GeometryLineVertex vertex) => new(vertex.U, vertex.V);
}

/// <summary>
/// The highest surface at a plan-view point over a set of triangles, through a grid of buckets (TerrainHeaven's
/// SurfaceHeightSampler, and the downward raycast bridge pillars use against the terrain).
/// </summary>
public sealed class SurfaceHeightSampler
{
    private const double Epsilon = 1e-7;
    private readonly List<(Vec3 A, Vec3 B, Vec3 C)> _triangles = [];
    private readonly Dictionary<(long, long), List<int>> _buckets = [];
    private readonly List<int> _largeTriangles = [];
    private double _cellSize = 1;

    public SurfaceHeightSampler(IEnumerable<(Vec3 A, Vec3 B, Vec3 C)> triangles)
    {
        ArgumentNullException.ThrowIfNull(triangles);
        foreach (var (a, b, c) in triangles)
        {
            var projectedArea = ((b.X - a.X) * (c.Z - a.Z)) - ((b.Z - a.Z) * (c.X - a.X));
            if (Math.Abs(projectedArea) > Epsilon)
            {
                _triangles.Add((a, b, c));
            }
        }

        BuildIndex();
    }

    /// <summary>A sampler over the triangles of every terrain tile in the world.</summary>
    public static SurfaceHeightSampler ForTerrain(TerrainWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return new SurfaceHeightSampler(world.Terrains.SelectMany(t => t.Geometry).SelectMany(g => g.Triangles).Select(t => (t.A, t.B, t.C)));
    }

    public bool IsEmpty => _triangles.Count == 0;

    /// <summary>The highest surface height at (x, z), or null where there is none.</summary>
    public double? Sample(double x, double z)
    {
        if (IsEmpty)
        {
            return null;
        }

        double? highest = null;
        void Visit(IEnumerable<int> indices)
        {
            foreach (var index in indices)
            {
                if (SampleTriangle(_triangles[index], x, z) is { } height && (highest is null || height > highest))
                {
                    highest = height;
                }
            }
        }

        Visit(_buckets.GetValueOrDefault(BucketKey(x, z)) ?? []);
        Visit(_largeTriangles);
        return highest;
    }

    private void BuildIndex()
    {
        if (IsEmpty)
        {
            return;
        }

        var minX = _triangles.Min(t => Math.Min(t.A.X, Math.Min(t.B.X, t.C.X)));
        var minZ = _triangles.Min(t => Math.Min(t.A.Z, Math.Min(t.B.Z, t.C.Z)));
        var maxX = _triangles.Max(t => Math.Max(t.A.X, Math.Max(t.B.X, t.C.X)));
        var maxZ = _triangles.Max(t => Math.Max(t.A.Z, Math.Max(t.B.Z, t.C.Z)));
        _cellSize = Math.Max(0.5, Math.Max(maxX - minX, maxZ - minZ) / 64);
        for (var index = 0; index < _triangles.Count; index++)
        {
            var (a, b, c) = _triangles[index];
            var firstX = (long)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X)) / _cellSize);
            var lastX = (long)Math.Floor(Math.Max(a.X, Math.Max(b.X, c.X)) / _cellSize);
            var firstZ = (long)Math.Floor(Math.Min(a.Z, Math.Min(b.Z, c.Z)) / _cellSize);
            var lastZ = (long)Math.Floor(Math.Max(a.Z, Math.Max(b.Z, c.Z)) / _cellSize);
            if ((lastX - firstX + 1) * (lastZ - firstZ + 1) > 4096)
            {
                _largeTriangles.Add(index);
                continue;
            }

            for (var cellX = firstX; cellX <= lastX; cellX++)
            {
                for (var cellZ = firstZ; cellZ <= lastZ; cellZ++)
                {
                    if (!_buckets.TryGetValue((cellX, cellZ), out var bucket))
                    {
                        _buckets[(cellX, cellZ)] = bucket = [];
                    }

                    bucket.Add(index);
                }
            }
        }
    }

    private (long, long) BucketKey(double x, double z) => ((long)Math.Floor(x / _cellSize), (long)Math.Floor(z / _cellSize));

    private static double? SampleTriangle((Vec3 A, Vec3 B, Vec3 C) t, double x, double z)
    {
        var denominator = ((t.B.Z - t.C.Z) * (t.A.X - t.C.X)) + ((t.C.X - t.B.X) * (t.A.Z - t.C.Z));
        if (Math.Abs(denominator) <= Epsilon)
        {
            return null;
        }

        var u = (((t.B.Z - t.C.Z) * (x - t.C.X)) + ((t.C.X - t.B.X) * (z - t.C.Z))) / denominator;
        var v = (((t.C.Z - t.A.Z) * (x - t.C.X)) + ((t.A.X - t.C.X) * (z - t.C.Z))) / denominator;
        var w = 1 - u - v;
        if (u < -Epsilon || v < -Epsilon || w < -Epsilon)
        {
            return null;
        }

        return (t.A.Y * u) + (t.B.Y * v) + (t.C.Y * w);
    }
}
