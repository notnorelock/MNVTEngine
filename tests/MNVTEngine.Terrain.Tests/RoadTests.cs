using MNVTEngine.Terrain.Elements;
using MNVTEngine.Terrain.Geometry;

namespace MNVTEngine.Terrain.Tests;

/// <summary>Ported from TerrainHeaven's src/elements/Road.test.ts.</summary>
public sealed class RoadTests
{
    private static List<Triangle> Group(WorldElement element, string name) =>
        element.ComputeGeometry().FirstOrDefault(g => g.Name == name)?.Triangles ?? [];

    private static IEnumerable<Vec3> Vertices(IEnumerable<Triangle> triangles) => triangles.SelectMany(t => new[] { t.A, t.B, t.C });

    [Fact]
    public void Uv_ScalesCurbSideUVsWithTheCurbHeight()
    {
        var road = new Road(Vec3.Zero, new Vec3(10, 0, 0)) { EdgeType = "sidewalk", CurbHeight = 0.4, SidewalkWidth = 1.5, SidewalkTexStretch = 2 };
        var curb = Group(road, "sidewalk").First(t =>
        {
            var heights = new[] { t.A.Y, t.B.Y, t.C.Y };
            return heights.Min() < 1e-8 && heights.Max() > 0.39;
        });
        var u = new[] { curb.UvA.X, curb.UvB.X, curb.UvC.X };
        Assert.Equal(0.8, u.Max() - u.Min(), 6);
    }

    [Fact]
    public void Uv_KeepsTheLongitudinalUVConstantAcrossCurvedSections()
    {
        var road = new Road(new Vec3(-8, 0, 0), new Vec3(8, 0, 0)) { Lanes = 1, Width = 6 };
        road.Divisions = 12;
        road.SetCurvePointA(new Vec3(-5, 0, 10));
        road.SetCurvePointB(new Vec3(5, 0, 10));
        var triangles = Group(road, "road");
        Assert.True(triangles.Count > 4);
        for (var index = 0; index < triangles.Count; index += 2)
        {
            var (first, second) = (triangles[index], triangles[index + 1]);
            Assert.Equal(first.UvB.Y, first.UvA.Y, 8);
            Assert.Equal(first.UvA.Y, second.UvA.Y, 8);
            Assert.Equal(second.UvC.Y, second.UvB.Y, 8);
        }
    }

    [Fact]
    public void Crown_CreatesAnExactCentreRidge()
    {
        var road = new Road(Vec3.Zero, new Vec3(10, 0, 0)) { Lanes = 1, RoadCrown = 0.6 };
        var heights = Vertices(Group(road, "road")).Select(v => v.Y).ToList();
        Assert.Equal(0.6, heights.Max(), 8);
        Assert.Contains(heights, h => Math.Abs(h) < 1e-8);
    }

    [Fact]
    public void Crown_KeepsAMidpointSectionWhenConnectedCrownsDiffer()
    {
        var road = new Road(new Vec3(-10, 0, 0), Vec3.Zero) { Lanes = 1, RoadCrown = 1 };
        var neighbour = new Road(Vec3.Zero, new Vec3(10, 0, 0)) { Lanes = 1, RoadCrown = 0 };
        road.ConnectWith(1, neighbour, 0);
        var centre = Vertices(Group(road, "road")).Where(v => Math.Abs(v.Z) < 1e-8).ToList();
        Assert.Contains(centre, v => Math.Abs(v.X + 5) < 1e-8 && Math.Abs(v.Y - 1) < 1e-8);
        Assert.Contains(centre, v => Math.Abs(v.X) < 1e-8 && Math.Abs(v.Y - 0.5) < 1e-8);
    }

    [Theory]
    [InlineData("solid", 24)]
    [InlineData("plane", 8)]
    [InlineData("spaced", 1)]
    [InlineData("truss", 1)]
    public void BridgeEdges_BuildEveryVariant(string style, int minimumTriangles)
    {
        var road = new Road(Vec3.Zero, new Vec3(12, 0, 0)) { EdgeType = "bridge", BridgeEdgeStyle = style, BridgeEdgeDistance = 3, BridgeEdgeLength = 1 };
        Assert.True(Group(road, "bridgeEdges").Count >= minimumTriangles);
        Assert.Contains("bridgeEdges", road.GetUVGroups());
    }

    [Fact]
    public void BridgeEdges_RoundTripThroughProjectData()
    {
        var road = new Road(Vec3.Zero, new Vec3(12, 0, 0))
        {
            EdgeType = "bridge", BridgeEdgeStyle = "spaced", BridgeEdgeHeight = 1.2, BridgeEdgeWidth = 0.35, BridgeEdgeDistance = 4,
            BridgeEdgeLength = 1.5, BridgeEdgeCapEnabled = true, BridgeEdgeCapHeight = 0.18, BridgeEdgeCapWidth = 0.44, BridgeEdgeCapJoined = true,
        };
        var r = Road.Deserialize(road.Serialize(1));
        Assert.Equal(("bridge", "spaced", 1.2, 0.35, 4.0, 1.5), (r.EdgeType, r.BridgeEdgeStyle, r.BridgeEdgeHeight, r.BridgeEdgeWidth, r.BridgeEdgeDistance, r.BridgeEdgeLength));
        Assert.Equal((true, 0.18, 0.44, true), (r.BridgeEdgeCapEnabled, r.BridgeEdgeCapHeight, r.BridgeEdgeCapWidth, r.BridgeEdgeCapJoined));
    }

    [Theory]
    [InlineData("solid")]
    [InlineData("spaced")]
    public void BridgeEdges_CloseWallsAtTheDeckUnderside(string style)
    {
        var road = new Road(Vec3.Zero, new Vec3(12, 0, 0)) { EdgeType = "bridge", BridgeEdgeStyle = style, BridgeDeckThickness = 0.7 };
        var triangles = Group(road, "bridgeEdges");
        Assert.Equal(-0.7, Vertices(triangles).Min(v => v.Y), 8);
        Assert.Contains(triangles, t => Math.Abs(t.A.Y + 0.7) < 1e-8 && Math.Abs(t.B.Y + 0.7) < 1e-8 && Math.Abs(t.C.Y + 0.7) < 1e-8);
    }

    [Fact]
    public void BridgeEdges_SpacedWallsWindOutwardOnBothSides()
    {
        var road = new Road(Vec3.Zero, new Vec3(12, 0, 0)) { Width = 4, EdgeType = "bridge", BridgeEdgeStyle = "spaced", BridgeEdgeWidth = 0.25 };
        static Vec3 Normal(Triangle t) => (t.B - t.A).Cross(t.C - t.A).Normalized();
        foreach (var triangle in Group(road, "bridgeEdges").Where(t => Math.Abs(Normal(t).Z) > 0.9))
        {
            var z = (triangle.A.Z + triangle.B.Z + triangle.C.Z) / 3;
            var isOuterFace = Math.Abs(z) > (road.Width * 0.5) + (road.BridgeEdgeWidth * 0.5);
            Assert.Equal(isOuterFace ? Math.Sign(z) : -Math.Sign(z), Math.Sign(Normal(triangle).Z));
        }
    }

    [Fact]
    public void BridgeDeck_DoesNotMirrorEdgeWalls()
    {
        var road = new Road(Vec3.Zero, new Vec3(12, 0, 0)) { Lanes = 1, EdgeType = "bridge", BridgeEdgeStyle = "solid", BridgeEdgeHeight = 1, BridgeDeckThickness = 0.4, BridgeEnabled = true };
        var deck = Vertices(Group(road, "bridgeDeck")).ToList();
        Assert.NotEmpty(deck);
        Assert.True(deck.Max(v => v.Y) <= 1e-8);
    }

    [Fact]
    public void BridgeEdges_PlanesAreDoubleSidedWithoutDuplicateFaces()
    {
        var road = new Road(Vec3.Zero, new Vec3(12, 0, 0)) { EdgeType = "bridge", BridgeEdgeStyle = "plane" };
        Assert.Equal(8, Group(road, "bridgeEdges").Count);
        road.Update();
        Assert.True(road.GetMaterialHint("bridgeEdges")!.DoubleSided);
    }

    [Fact]
    public void BridgeEdges_SolidWallsAreClosedAtBothEnds()
    {
        var road = new Road(Vec3.Zero, new Vec3(12, 0, 0)) { EdgeType = "bridge", BridgeEdgeStyle = "solid" };
        var endCaps = Group(road, "bridgeEdges").Where(t =>
        {
            var xs = new[] { t.A.X, t.B.X, t.C.X };
            return xs.All(x => Math.Abs(x) < 1e-8) || xs.All(x => Math.Abs(x - 12) < 1e-8);
        });
        Assert.Equal(8, endCaps.Count());
    }

    [Fact]
    public void BridgeEdges_NoEndCapsBetweenCompatibleConnectedRoads()
    {
        var first = new Road(new Vec3(-10, 0, 0), Vec3.Zero) { EdgeType = "bridge", BridgeEdgeStyle = "solid" };
        var second = new Road(Vec3.Zero, new Vec3(10, 0, 0)) { EdgeType = "bridge", BridgeEdgeStyle = "solid" };
        first.ConnectWith(1, second, 0);
        foreach (var road in new[] { first, second })
        {
            Assert.DoesNotContain(Group(road, "bridgeEdges"), t => new[] { t.A.X, t.B.X, t.C.X }.All(x => Math.Abs(x) < 1e-8));
        }
    }

    [Fact]
    public void BridgeEdges_CapsOnlySpacedWalls()
    {
        var road = new Road(Vec3.Zero, new Vec3(12, 0, 0)) { EdgeType = "bridge", BridgeEdgeStyle = "spaced", BridgeEdgeHeight = 0.8, BridgeEdgeCapHeight = 0.2 };
        var withoutCap = Group(road, "bridgeEdges").Count;
        road.BridgeEdgeCapEnabled = true;
        var capped = Group(road, "bridgeEdges");
        Assert.True(capped.Count > withoutCap);
        Assert.Contains(capped, t => Math.Abs(t.A.Y - 1) < 1e-8 && Math.Abs(t.B.Y - 1) < 1e-8 && Math.Abs(t.C.Y - 1) < 1e-8);
    }

    [Fact]
    public void BridgeEdges_JoinsSpacedCapsIntoAContinuousBox()
    {
        var road = new Road(Vec3.Zero, new Vec3(12, 0, 0))
        {
            EdgeType = "bridge", BridgeEdgeStyle = "spaced", BridgeEdgeLength = 1, BridgeEdgeCapEnabled = true, BridgeEdgeCapJoined = true,
            BridgeEdgeCapWidth = 0.5, BridgeEdgeCapHeight = 0.2,
        };
        Assert.Contains(Group(road, "bridgeEdges"), t =>
        {
            var xs = new[] { t.A.X, t.B.X, t.C.X };
            return new[] { t.A.Y, t.B.Y, t.C.Y }.All(y => Math.Abs(y - 1) < 1e-8) && xs.Max() - xs.Min() > road.BridgeEdgeLength;
        });
    }

    [Fact]
    public void BridgeEdges_BuildsTwoRaisedSteelTrusses()
    {
        var road = new Road(Vec3.Zero, new Vec3(20, 0, 0)) { Width = 5, EdgeType = "bridge", BridgeEdgeStyle = "truss", BridgeEdgeHeight = 4, BridgeEdgeWidth = 0.2, BridgeEdgeDistance = 3 };
        var triangles = Group(road, "bridgeEdges");
        var vertices = Vertices(triangles).ToList();
        Assert.True(triangles.Count > 200);
        Assert.True(vertices.Max(v => v.Y) > 3.5);
        Assert.Contains(vertices, v => v.Z > road.Width * 0.5);
        Assert.Contains(vertices, v => v.Z < -road.Width * 0.5);
    }

    [Fact]
    public void Bridge_PillarsReachTheTerrain()
    {
        var world = new TerrainWorld();
        var terrain = new Elements.Terrain(new Vec3(0, -6, 0), 40, 40);
        world.Add(terrain);
        var road = new Road(new Vec3(-15, 0, 0), new Vec3(15, 0, 0)) { BridgeEnabled = true, BridgePillarDistance = 8 };
        world.Add(road);
        world.Update();
        var pillars = Group(road, "bridgePillars");
        Assert.NotEmpty(pillars);
        Assert.Equal(-6.5, Vertices(pillars).Min(v => v.Y), 3); // terrain height minus the embed depth
        Assert.Empty(road.GetOccupiedArea()); // a bridge does not cut the terrain
    }
}
