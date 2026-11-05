using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MNVTEngine.Terrain.Geometry;

namespace MNVTEngine.Terrain.Export;

/// <summary>One OBJ group: an unindexed triangle soup (xyz and uv per vertex) and its material name.</summary>
public sealed record ObjGroup(string Name, string MaterialName, IReadOnlyList<double> Positions, IReadOnlyList<double> Uvs);

/// <summary>An MTL material; <see cref="TextureRelativePath"/> is relative to the .mtl file (e.g. "../Textures/grass.png").</summary>
public sealed record ObjMaterial(string Name, string? TextureRelativePath);

/// <summary>
/// Writes Wavefront OBJ and MTL text from triangle soups, with angle-based "auto smooth" normals: a vertex averages the
/// face normals of every triangle meeting at its position (across all groups) within the threshold of its own face, so
/// curved surfaces shade smoothly and sharp corners stay crisp.
/// </summary>
public static partial class ObjWriter
{
    /// <summary>Vertices closer than this count as one point when looking for neighbouring triangles.</summary>
    private const double WeldEpsilon = 1e-4;

    public static string BuildObj(string mtlFileName, string objectName, IReadOnlyList<ObjGroup> groups, double autoSmoothDegrees = 30)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var builder = new StringBuilder();
        builder.Append("mtllib ").Append(mtlFileName).Append('\n').Append("o ").Append(SanitizeName(objectName)).Append('\n');
        var normalsByGroup = ComputeAutoSmoothNormals(groups, autoSmoothDegrees);
        var vertexCount = 0;
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex];
            var triangleCount = group.Positions.Count / 9;
            if (triangleCount == 0)
            {
                continue;
            }

            var normals = normalsByGroup[groupIndex];
            builder.Append("g ").Append(SanitizeName(group.Name)).Append('\n').Append("usemtl ").Append(SanitizeName(group.MaterialName)).Append('\n');
            for (var tri = 0; tri < triangleCount; tri++)
            {
                for (var vert = 0; vert < 3; vert++)
                {
                    var pi = ((tri * 3) + vert) * 3;
                    var ti = ((tri * 3) + vert) * 2;
                    builder.Append("v ").Append(Format(group.Positions[pi])).Append(' ').Append(Format(group.Positions[pi + 1])).Append(' ').Append(Format(group.Positions[pi + 2])).Append('\n');
                    builder.Append("vt ").Append(Format(At(group.Uvs, ti))).Append(' ').Append(Format(At(group.Uvs, ti + 1))).Append('\n');
                    builder.Append("vn ").Append(Format(normals[pi])).Append(' ').Append(Format(normals[pi + 1])).Append(' ').Append(Format(normals[pi + 2])).Append('\n');
                }

                var (a, b, c) = (vertexCount + 1, vertexCount + 2, vertexCount + 3);
                builder.Append(CultureInfo.InvariantCulture, $"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}\n");
                vertexCount += 3;
            }
        }

        return builder.ToString();
    }

    public static string BuildMtl(IEnumerable<ObjMaterial> materials)
    {
        ArgumentNullException.ThrowIfNull(materials);
        var builder = new StringBuilder();
        foreach (var material in materials)
        {
            builder.Append("newmtl ").Append(SanitizeName(material.Name)).Append("\nKd 1 1 1\nKa 0 0 0\nKs 0 0 0\nd 1\nillum 1\n");
            if (!string.IsNullOrEmpty(material.TextureRelativePath))
            {
                // The same image supplies opacity when it has an alpha channel (importers that read map_d keep it).
                builder.Append("map_Kd ").Append(material.TextureRelativePath).Append('\n');
                builder.Append("map_d ").Append(material.TextureRelativePath).Append('\n');
            }
        }

        return builder.ToString();
    }

    public static string SanitizeName(string name) => InvalidNameChars().Replace(Whitespace().Replace(name ?? "", "_"), "");

    private static double At(IReadOnlyList<double> values, int index) => index < values.Count ? values[index] : 0;

    // JS toFixed(6): -0 prints unsigned, small negatives keep their sign ("-0.000000").
    private static string Format(double value) => double.IsFinite(value) ? (value == 0 ? 0d : value).ToString("F6", CultureInfo.InvariantCulture) : "0";

    private static (long, long, long) PositionKey(double x, double y, double z) =>
        ((long)MathUtil.JsRound(x / WeldEpsilon), (long)MathUtil.JsRound(y / WeldEpsilon), (long)MathUtil.JsRound(z / WeldEpsilon));

    private static double[][] ComputeAutoSmoothNormals(IReadOnlyList<ObjGroup> groups, double thresholdDegrees)
    {
        var cosThreshold = Math.Cos(thresholdDegrees * Math.PI / 180);
        var keys = new List<(long, long, long)[]>();
        var normals = new List<Vec3>();
        var starts = new int[groups.Count];
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            starts[groupIndex] = normals.Count;
            var p = groups[groupIndex].Positions;
            for (var o = 0; o + 8 < p.Count; o += 9)
            {
                var a = new Vec3(p[o], p[o + 1], p[o + 2]);
                var b = new Vec3(p[o + 3], p[o + 4], p[o + 5]);
                var c = new Vec3(p[o + 6], p[o + 7], p[o + 8]);
                keys.Add([PositionKey(a.X, a.Y, a.Z), PositionKey(b.X, b.Y, b.Z), PositionKey(c.X, c.Y, c.Z)]);
                var n = (b - a).Cross(c - a);
                normals.Add(n.Length == 0 ? n : n / n.Length);
            }
        }

        var facesAt = new Dictionary<(long, long, long), List<int>>();
        for (var face = 0; face < keys.Count; face++)
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

        var result = new double[groups.Count][];
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var count = groups[groupIndex].Positions.Count / 9;
            var output = new double[count * 9];
            for (var local = 0; local < count; local++)
            {
                var face = starts[groupIndex] + local;
                for (var corner = 0; corner < 3; corner++)
                {
                    var sum = Vec3.Zero;
                    foreach (var neighbour in facesAt[keys[face][corner]])
                    {
                        if (normals[face].Dot(normals[neighbour]) >= cosThreshold)
                        {
                            sum += normals[neighbour];
                        }
                    }

                    var length = sum.Length == 0 ? 1 : sum.Length;
                    var o = ((local * 3) + corner) * 3;
                    (output[o], output[o + 1], output[o + 2]) = (sum.X / length, sum.Y / length, sum.Z / length);
                }
            }

            result[groupIndex] = output;
        }

        return result;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("[^a-zA-Z0-9_.-]")]
    private static partial Regex InvalidNameChars();
}
