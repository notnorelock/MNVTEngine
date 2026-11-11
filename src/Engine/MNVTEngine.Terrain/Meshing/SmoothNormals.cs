using MNVTEngine.Mathematics;
using MNVTEngine.Terrain.Elements;
using MNVTEngine.Terrain.Geometry;

namespace MNVTEngine.Terrain.Meshing;

/// <summary>
/// Angle-based "auto smooth" normals (as Blender's): a vertex averages the face normals of every triangle meeting at its
/// position, across all groups, that lie within the threshold of its own face. Curved surfaces shade smoothly; sharp
/// corners (curbs, bank edges) stay crisp. Element triangles share no vertices, so positions are matched within 1e-4.
/// </summary>
public static class SmoothNormals
{
    private const double WeldEpsilon = 1e-4;

    /// <summary>One normal per triangle corner (A, B, C of each triangle in order), per group.</summary>
    public static Vector3[][] Compute(IReadOnlyList<GeometryGroup> groups, double thresholdDegrees = 30)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var cosThreshold = Math.Cos(thresholdDegrees * Math.PI / 180);
        var faces = groups.SelectMany(g => g.Triangles).ToList();
        var keys = faces.Select(t => new[] { Key(t.A), Key(t.B), Key(t.C) }).ToList();
        var normals = faces.Select(t => (t.B - t.A).Cross(t.C - t.A).Normalized).ToList();
        var facesAt = new Dictionary<(long, long, long), List<int>>();
        for (var face = 0; face < faces.Count; face++)
        {
            foreach (var key in keys[face])
            {
                if (!facesAt.TryGetValue(key, out var list))
                {
                    facesAt[key] = list = [];
                }

                list.Add(face);
            }
        }

        var result = new Vector3[groups.Count][];
        var start = 0;
        for (var g = 0; g < groups.Count; g++)
        {
            var count = groups[g].Triangles.Count;
            var output = new Vector3[count * 3];
            for (var local = 0; local < count; local++)
            {
                var face = start + local;
                for (var corner = 0; corner < 3; corner++)
                {
                    var sum = Vector3.Zero;
                    foreach (var neighbour in facesAt[keys[face][corner]])
                    {
                        if (normals[face].Dot(normals[neighbour]) >= cosThreshold)
                        {
                            sum += normals[neighbour];
                        }
                    }

                    output[(local * 3) + corner] = sum.Normalized;
                }
            }

            result[g] = output;
            start += count;
        }

        return result;
    }

    private static (long, long, long) Key(Vector3 p) =>
        ((long)MathUtil.JsRound(p.X / WeldEpsilon), (long)MathUtil.JsRound(p.Y / WeldEpsilon), (long)MathUtil.JsRound(p.Z / WeldEpsilon));
}
