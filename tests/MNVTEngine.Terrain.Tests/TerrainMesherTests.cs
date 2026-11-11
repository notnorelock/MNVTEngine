using MNVTEngine.Mathematics;
using System.Globalization;
using MNVTEngine.Terrain.Geometry;
using MNVTEngine.Terrain.Meshing;

namespace MNVTEngine.Terrain.Tests;

/// <summary>Ported from TerrainHeaven's src/terrain/TerrainMesher.test.ts.</summary>
public sealed class TerrainMesherTests
{
    private static readonly TerrainMesherSettings DefaultSettings = new(2, 400, true, 4, 35);

    private static TerrainCutPointInput CutPoint(Vector3 position, double radius = 4) => new(position, radius);

    private static TerrainMesherInput MakeInput(Vector3? center = null, double length = 30, IReadOnlyList<OccupiedTriangle>? cutAreas = null,
        IReadOnlyList<TerrainCutPointInput>? cutPoints = null, TerrainPaintInput? paint = null, TerrainMesherSettings? settings = null) => new()
    {
        Center = center ?? Vector3.Zero,
        Width = 30,
        Length = length,
        CutAreas = cutAreas ?? [],
        CutPoints = cutPoints ?? [],
        Paint = paint,
        Settings = settings ?? DefaultSettings,
    };

    internal static List<OccupiedTriangle> RectangleCut(double minX, double maxX, double minZ, double maxZ, double y)
    {
        var bl = new Vector3(minX, y, minZ);
        var br = new Vector3(maxX, y, minZ);
        var tr = new Vector3(maxX, y, maxZ);
        var tl = new Vector3(minX, y, maxZ);
        return [new(bl, br, tr), new(bl, tr, tl)];
    }

    private static double SignedArea(OccupiedTriangle tri) =>
        ((tri.B.X - tri.A.X) * (tri.C.Z - tri.A.Z)) - ((tri.B.Z - tri.A.Z) * (tri.C.X - tri.A.X));

    private static IEnumerable<Vector3> Vertices(IEnumerable<OccupiedTriangle> triangles) => triangles.SelectMany(t => new[] { t.A, t.B, t.C });

    private static double? VertexHeight(IEnumerable<OccupiedTriangle> triangles, double x, double z) =>
        Vertices(triangles).Where(p => Math.Abs(p.X - x) < 1e-6 && Math.Abs(p.Z - z) < 1e-6).Select(p => (double?)p.Y).FirstOrDefault();

    private static string Key(Vector3 p) => $"{p.X.ToString("F5", CultureInfo.InvariantCulture)},{p.Z.ToString("F5", CultureInfo.InvariantCulture)}";

    private static List<string> TopologyKey(IEnumerable<OccupiedTriangle> triangles) =>
        triangles.Select(t => string.Join('|', new[] { t.A, t.B, t.C }.Select(Key).Order(StringComparer.Ordinal))).Order(StringComparer.Ordinal).ToList();

    [Fact]
    public void KeepsAnUnconstrainedFlatTerrainMinimal()
    {
        var triangles = new TerrainMesher().Build(MakeInput());
        Assert.Equal(2, triangles.Count);
        Assert.All(triangles, tri => Assert.True(Math.Abs(SignedArea(tri)) > 1e-8));
    }

    [Fact]
    public void PreservesARoadHoleAndItsExactBoundaryHeight()
    {
        var triangles = new TerrainMesher().Build(MakeInput(cutAreas: RectangleCut(-2, 2, -10, 10, 0.5)));
        foreach (var tri in triangles)
        {
            var x = (tri.A.X + tri.B.X + tri.C.X) / 3;
            var z = (tri.A.Z + tri.B.Z + tri.C.Z) / 3;
            Assert.False(x > -2 && x < 2 && z > -10 && z < 10);
            Assert.True(Math.Abs(SignedArea(tri)) > 1e-8);
        }

        var boundary = Vertices(triangles).Where(p => Math.Abs(Math.Abs(p.X) - 2) < 1e-5 && p.Z >= -10 && p.Z <= 10).ToList();
        Assert.NotEmpty(boundary);
        Assert.All(boundary, p => Assert.True(Math.Abs(p.Y - 0.5) < 1e-5));
    }

    [Fact]
    public void KeepsATerrainCutPointExactAndRespectsTheTriangleBudget()
    {
        var point = new Vector3(1.25, 6, -0.75);
        var input = MakeInput(cutPoints: [CutPoint(point)]);
        var triangles = new TerrainMesher().Build(input);
        var matching = Vertices(triangles).Where(p => Math.Abs(p.X - point.X) < 1e-5 && Math.Abs(p.Z - point.Z) < 1e-5).ToList();
        Assert.NotEmpty(matching);
        Assert.All(matching, p => Assert.True(Math.Abs(p.Y - point.Y) < 1e-5));
        Assert.True(triangles.Count <= input.Settings.TriangleLimit);
    }

    [Fact]
    public void ReusesTheSameXZTopologyWhenOnlyACutPointHeightChanges()
    {
        var mesher = new TerrainMesher();
        var low = mesher.Build(MakeInput(cutPoints: [CutPoint(new Vector3(0, 2, 0))]));
        var high = mesher.Build(MakeInput(cutPoints: [CutPoint(new Vector3(0, 8, 0))]));
        Assert.Equal(TopologyKey(low), TopologyKey(high));
        Assert.Contains(Vertices(high), p => p.X == 0 && p.Z == 0 && p.Y == 8);
    }

    [Fact]
    public void DoesNotCreateNonManifoldInteriorEdges()
    {
        var triangles = new TerrainMesher().Build(MakeInput(
            cutAreas: RectangleCut(-3, 3, -8, 8, -1),
            cutPoints: [CutPoint(new Vector3(-8, 4, 0)), CutPoint(new Vector3(8, -3, 0))]));
        var edges = new Dictionary<string, int>();
        static string EdgeKey(Vector3 a, Vector3 b)
        {
            var (ka, kb) = (Key(a), Key(b));
            return string.CompareOrdinal(ka, kb) < 0 ? $"{ka}|{kb}" : $"{kb}|{ka}";
        }

        foreach (var tri in triangles)
        {
            foreach (var (a, b) in new[] { (tri.A, tri.B), (tri.B, tri.C), (tri.C, tri.A) })
            {
                var edge = EdgeKey(a, b);
                edges[edge] = edges.GetValueOrDefault(edge) + 1;
            }
        }

        Assert.All(edges.Values, count => Assert.True(count is 1 or 2));
    }

    [Fact]
    public void ReadsPaintedHeightsInWorldSpace_SoNeighbouringTilesAgreeOnASharedEdge()
    {
        var paint = new TerrainPaintInput(1, [.. new[] { -15.0, 15.0 }.SelectMany(gridZ => new TerrainPaintHeightInput[]
        {
            new(-1, gridZ, 3), new(0, gridZ, 5), new(1, gridZ, 3),
        })]);

        // Smoothing off isolates the painted field: every vertex height is the field itself.
        var settings = DefaultSettings with { SmoothingEnabled = false };
        var left = new TerrainMesher().Build(MakeInput(center: new Vector3(-15, 0, 0), paint: paint, settings: settings));
        var right = new TerrainMesher().Build(MakeInput(center: new Vector3(15, 0, 0), paint: paint, settings: settings));
        foreach (var z in new[] { -15.0, 15.0 })
        {
            Assert.Equal(5, VertexHeight(left, 0, z)!.Value, 5);
            Assert.Equal(5, VertexHeight(right, 0, z)!.Value, 5);
        }
    }

    [Fact]
    public void KeepsACutPointShapingATileItsSlopeReachesFromOutside()
    {
        // The point sits 4 units past the edge with a radius of only 2, but dropping 6 units at 35 degrees needs
        // roughly 13 units of run, so the edge has to follow it down.
        var triangles = new TerrainMesher().Build(MakeInput(center: new Vector3(15, 0, 0), length: 8, cutPoints: [CutPoint(new Vector3(-4, -6, 0), 2)]));
        Assert.True(VertexHeight(triangles, 0, -4) < -0.5);
        Assert.Equal(0, VertexHeight(triangles, 30, -4)!.Value, 5);
    }

    [Fact]
    public void AppliesALocalCutPointSlopeAsAGeometricAngle()
    {
        var mesher = new TerrainMesher();
        var bed = new Vector3(0, -2, 0);
        TerrainCutPointInput SlopePoint(double degrees) => new(bed, 2 / Math.Tan(MathUtil.DegToRad(degrees)), degrees);
        var settings = DefaultSettings with { MeshDetail = 0.5, TriangleLimit = 1000 };
        var shallow = mesher.Build(MakeInput(cutPoints: [SlopePoint(20)], settings: settings));
        var steep = mesher.Build(MakeInput(cutPoints: [SlopePoint(70)], settings: settings));
        var shallowHeights = new Dictionary<string, double>();
        foreach (var p in Vertices(shallow))
        {
            shallowHeights[Key(p)] = p.Y;
        }

        Assert.Contains(Vertices(steep), p => shallowHeights.TryGetValue(Key(p), out var h) && h < p.Y - 0.25);
    }
}
