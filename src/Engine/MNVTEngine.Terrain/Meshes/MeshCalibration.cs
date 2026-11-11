using MNVTEngine.Mathematics;
using MNVTEngine.Terrain.Geometry;

namespace MNVTEngine.Terrain.Meshes;

public readonly record struct MeshBounds(Vector3 Min, Vector3 Max)
{
    public double Width => Max.X - Min.X;

    public double Height => Max.Y - Min.Y;

    public double Depth => Max.Z - Min.Z;
}

/// <summary>
/// Per-asset unit and pivot correction. Uploaded models often come in the wrong unit (centimeters) or with the pivot
/// away from the base, and their formats carry no metadata to trust, so the user calibrates once (scale, then offset)
/// and the renderer applies it to every placement.
/// </summary>
public static class MeshCalibration
{
    /// <summary>Bounds of a model's raw (uncalibrated) vertices; none gives zero bounds.</summary>
    public static MeshBounds MeasureBounds(IEnumerable<Vector3> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        var max = -min;
        foreach (var p in positions)
        {
            (min, max) = (Vector3.Min(min, p), Vector3.Max(max, p));
        }

        return float.IsFinite(min.X) ? new MeshBounds(min, max) : default;
    }

    /// <summary>"Center Mesh": the offset that centers the bounds on X/Z and rests the lowest point on Y = 0 at a scale.</summary>
    public static Vector3 ComputeGroundedCenterOffset(MeshBounds bounds, double baseScale)
    {
        var scale = baseScale > 0 ? baseScale : 1;
        return new Vector3(-((bounds.Min.X + bounds.Max.X) / 2) * scale, -bounds.Min.Y * scale, -((bounds.Min.Z + bounds.Max.Z) / 2) * scale);
    }
}
