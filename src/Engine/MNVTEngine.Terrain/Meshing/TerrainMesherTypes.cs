using MNVTEngine.Terrain.Geometry;

namespace MNVTEngine.Terrain.Meshing;

/// <summary>
/// A triangle of the world an element occupies (roads, intersections, rivers...), in world space. The terrain is cut
/// along these and blends toward their heights. <see cref="BankSlopeDegrees"/> overrides the terrain's max slope near
/// this triangle's boundary (a river demands a steeper bank than a road).
/// </summary>
public readonly record struct OccupiedTriangle(Vec3 A, Vec3 B, Vec3 C, double? BankSlopeDegrees = null);

/// <summary>An axis-aligned rectangle in plan view (x, z).</summary>
public readonly record struct TerrainRect(double MinX, double MaxX, double MinZ, double MaxZ);

public sealed record TerrainMesherSettings(double MeshDetail, int TriangleLimit, bool SmoothingEnabled, double SmoothingRadius, double MaxSlopeDegrees);

/// <summary>
/// A point that forces the terrain to a specific height, blending outward over <see cref="Radius"/>: single terrain cut
/// points and points sampled along a terrain cut spline or river.
/// </summary>
/// <param name="MaxSlopeDegrees">A local slope override: rivers turn their Bank Slope into a real bank angle with it.</param>
/// <param name="SlopeSmoothing">0 keeps a straight bank profile, 1 rounds its bottom and top.</param>
/// <param name="ProfileOnly">Pins an extra profile vertex without creating another radial influence.</param>
public sealed record TerrainCutPointInput(Vec3 Position, double Radius, double? MaxSlopeDegrees = null, double? SlopeSmoothing = null, bool ProfileOnly = false);

/// <summary>A painted height on the world-aligned paint lattice.</summary>
public sealed record TerrainPaintHeightInput(double GridX, double GridZ, double Height);

public sealed record TerrainPaintInput(double CellSize, IReadOnlyList<TerrainPaintHeightInput> Samples);

public sealed record TerrainMesherInput
{
    public required Vec3 Center { get; init; }

    public double? BaseHeight { get; init; }

    public required double Width { get; init; }

    public required double Length { get; init; }

    /// <summary>
    /// The surface to mesh. A group of touching terrain tiles passes every tile here and gets one continuous mesh over
    /// their union, the only way neighbouring tiles can share vertices and heights. Null means the single rectangle
    /// described by center/width/length.
    /// </summary>
    public IReadOnlyList<TerrainRect>? Footprint { get; init; }

    public required IReadOnlyList<OccupiedTriangle> CutAreas { get; init; }

    public IReadOnlyList<OccupiedTriangle>? InfluenceAreas { get; init; }

    public required IReadOnlyList<TerrainCutPointInput> CutPoints { get; init; }

    public TerrainPaintInput? Paint { get; init; }

    public required TerrainMesherSettings Settings { get; init; }
}
