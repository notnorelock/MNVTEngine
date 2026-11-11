using MNVTEngine.Mathematics;
using MNVTEngine.Terrain.Geometry;
using MNVTEngine.Terrain.Meshing;

namespace MNVTEngine.Terrain.Elements;

/// <summary>
/// Sculpts terrain height along a path instead of a single point: each sampled point along the curve forces the terrain
/// to that point's height, blending outward over <see cref="Distance"/>.
/// </summary>
public sealed class TerrainCutSpline(Vector3 positionA, Vector3 positionB) : SplineElement(positionA, positionB, NodeKind.TerrainCut)
{
    public const double DefaultRadius = 4;

    public double Distance { get; set; } = DefaultRadius;

    /// <summary>
    /// The points fed into the terrain mesher. A non-zero LOD thins the row count to match how much coarser the terrain
    /// itself gets at that level, keeping the mesher's point budget compatible.
    /// </summary>
    public IReadOnlyList<TerrainCutPointInput> GetSampledCutPoints(int lodIndex = 0)
    {
        int? divisions = lodIndex > 0 ? LodLevels.Divisions(Divisions, lodIndex) : null;
        return GetBezierCurvePoints(divisions).Select(point => new TerrainCutPointInput(point, Math.Max(0.1, Distance))).ToList();
    }

    public override IReadOnlyList<GuideLine> GetGuideLines() => [new GuideLine(GetBezierCurvePoints(), NodeKind.TerrainCut), .. base.GetGuideLines()];

    public override IReadOnlyList<OccupiedTriangle> GetOccupiedArea() => [];

    protected override List<GeometryGroup> GetGeometry() => [];

    public override PropertyDefinition GetProperties() => new("Terrain Cut Spline", "∿",
    [
        new PropertySection("Terrain Cut Spline",
        [
            new NumberProperty("Divisions", () => Divisions, v => Divisions = Math.Max(0, (int)MathUtil.JsRound(v)), 0, 32, 1),
            new NumberProperty("Distance", () => Distance, v =>
            {
                Distance = Math.Max(0.1, v);
                Update();
            }, 0.1, 100, 0.1),
        ]),
    ]);

    public override ElementData Serialize(int id)
    {
        var data = new ElementData { Type = "terrainCutSpline", Id = id, TerrainCutDistance = Distance };
        SerializeCurve(data);
        return data;
    }

    public static TerrainCutSpline Deserialize(ElementData data)
    {
        var (a, b) = EndPositions(data, new Vector3(1, 0, 0));
        var spline = new TerrainCutSpline(a, b) { Distance = Math.Max(0.1, data.TerrainCutDistance ?? DefaultRadius) };
        spline.DeserializeCurve(data);
        return spline;
    }
}
