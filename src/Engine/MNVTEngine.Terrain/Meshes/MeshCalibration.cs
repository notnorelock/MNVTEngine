using MNVTEngine.Terrain.Geometry;

namespace MNVTEngine.Terrain.Meshes;

/// <summary>
/// One material's worth of a loaded prop: an unindexed triangle soup (xyz per vertex, uv per vertex) and its material.
/// Normals are not carried: exporters and previews recompute them from positions.
/// </summary>
public sealed record MeshGroup(string Name, double[] Positions, double[] Uvs, Vec3 Color, string? TexturePath)
{
    public int VertexCount => Positions.Length / 3;
}

public readonly record struct MeshBounds(Vec3 Min, Vec3 Max)
{
    public double Width => Max.X - Min.X;

    public double Height => Max.Y - Min.Y;

    public double Depth => Max.Z - Min.Z;
}

/// <summary>
/// Per-asset unit and pivot correction. Uploaded files often come in the wrong unit (centimeters) or with the pivot
/// away from the base, and their formats carry no metadata to trust, so the user calibrates once and every consumer
/// bakes the same scale-then-offset into the vertex positions.
/// </summary>
public static class MeshCalibration
{
    public static List<MeshGroup> Calibrate(IEnumerable<MeshGroup> groups, double baseScale, Vec3 offset)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var scale = baseScale > 0 ? baseScale : 1;
        return groups.Select(group =>
        {
            var positions = new double[group.Positions.Length];
            for (var i = 0; i + 2 < positions.Length; i += 3)
            {
                positions[i] = (group.Positions[i] * scale) + offset.X;
                positions[i + 1] = (group.Positions[i + 1] * scale) + offset.Y;
                positions[i + 2] = (group.Positions[i + 2] * scale) + offset.Z;
            }

            return group with { Positions = positions };
        }).ToList();
    }

    /// <summary>Bounds of the raw (uncalibrated) groups; empty input gives zero bounds.</summary>
    public static MeshBounds MeasureBounds(IEnumerable<MeshGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var min = new Vec3(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var max = -min;
        foreach (var group in groups)
        {
            for (var i = 0; i + 2 < group.Positions.Length; i += 3)
            {
                var (x, y, z) = (group.Positions[i], group.Positions[i + 1], group.Positions[i + 2]);
                min = new Vec3(Math.Min(min.X, x), Math.Min(min.Y, y), Math.Min(min.Z, z));
                max = new Vec3(Math.Max(max.X, x), Math.Max(max.Y, y), Math.Max(max.Z, z));
            }
        }

        return double.IsFinite(min.X) ? new MeshBounds(min, max) : default;
    }

    /// <summary>"Center Mesh": the offset that centers the bounds on X/Z and rests the lowest point on Y = 0 at a scale.</summary>
    public static Vec3 ComputeGroundedCenterOffset(MeshBounds bounds, double baseScale)
    {
        var scale = baseScale > 0 ? baseScale : 1;
        return new Vec3(-((bounds.Min.X + bounds.Max.X) / 2) * scale, -bounds.Min.Y * scale, -((bounds.Min.Z + bounds.Max.Z) / 2) * scale);
    }
}
