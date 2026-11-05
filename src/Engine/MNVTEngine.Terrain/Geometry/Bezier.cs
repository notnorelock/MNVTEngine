using MNVTEngine.Terrain.Meshing;

namespace MNVTEngine.Terrain.Geometry;

public static class Bezier
{
    /// <summary>A cubic Bezier at t in [0, 1]: p0/p3 are the endpoints, p1/p2 the control points.</summary>
    public static Vec3 Cubic(Vec3 p0, Vec3 p1, Vec3 p2, Vec3 p3, double t)
    {
        var mt = 1 - t;
        return new Vec3(
            (mt * mt * mt * p0.X) + (3 * mt * mt * t * p1.X) + (3 * mt * t * t * p2.X) + (t * t * t * p3.X),
            (mt * mt * mt * p0.Y) + (3 * mt * mt * t * p1.Y) + (3 * mt * t * t * p2.Y) + (t * t * t * p3.Y),
            (mt * mt * mt * p0.Z) + (3 * mt * mt * t * p1.Z) + (3 * mt * t * t * p2.Z) + (t * t * t * p3.Z));
    }

    /// <summary><paramref name="divisions"/> + 2 points along the curve (0 divisions: just the two endpoints).</summary>
    public static List<Vec3> SampleCubic(Vec3 p0, Vec3 p1, Vec3 p2, Vec3 p3, int divisions)
    {
        var steps = Math.Max(1, divisions + 1);
        var points = new List<Vec3>(steps + 1);
        for (var i = 0; i <= steps; i++)
        {
            points.Add(Cubic(p0, p1, p2, p3, i / (double)steps));
        }

        return points;
    }
}

/// <summary>
/// The Unity-export LOD presets, shared by the editor's LOD Preview and the exporter so a previewed level is exactly
/// what gets exported. Index 0 is "as authored"; 1-3 are progressively coarser.
/// </summary>
public static class LodLevels
{
    public const int MaxLodIndex = 3;
    private static readonly double[] DetailMultiplier = [1, 2.5, 6, 16];
    private static readonly double[] TriangleLimitMultiplier = [1, 0.35, 0.1, 0.02];
    private static readonly double[] DivisionsDivisor = [1, 2, 5, 12];
    private static readonly double[] DetailLevelDivisor = [1, 1.6, 3, 6];

    /// <summary>The terrain's own mesher at a coarser detail and budget, so holes, banks and slopes stay right.</summary>
    public static TerrainMesherSettings ApplyTerrain(TerrainMesherSettings settings, int lodIndex)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (lodIndex <= 0)
        {
            return settings;
        }

        var index = Math.Min(lodIndex, MaxLodIndex);
        return settings with
        {
            MeshDetail = settings.MeshDetail * DetailMultiplier[index],
            TriangleLimit = Math.Max(50, (int)MathUtil.JsRound(settings.TriangleLimit * TriangleLimitMultiplier[index])),
        };
    }

    public static int Divisions(int divisions, int lodIndex) =>
        lodIndex <= 0 ? divisions : Math.Max(0, (int)MathUtil.JsRound(divisions / DivisionsDivisor[Math.Min(lodIndex, MaxLodIndex)]));

    public static double DetailLevel(double detailLevel, int lodIndex) =>
        lodIndex <= 0 ? detailLevel : Math.Max(0.1, detailLevel / DetailLevelDivisor[Math.Min(lodIndex, MaxLodIndex)]);
}
