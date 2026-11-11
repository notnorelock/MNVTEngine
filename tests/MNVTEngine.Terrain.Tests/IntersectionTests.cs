using MNVTEngine.Mathematics;
using MNVTEngine.Terrain.Elements;
using MNVTEngine.Terrain.Geometry;

namespace MNVTEngine.Terrain.Tests;

/// <summary>Ported from TerrainHeaven's src/elements/Intersection.test.ts.</summary>
public sealed class IntersectionTests
{
    private static List<Vector3> Vertices(WorldElement element, string group) =>
        (element.ComputeGeometry().FirstOrDefault(g => g.Name == group)?.Triangles ?? []).SelectMany(t => new[] { t.A, t.B, t.C }).ToList();

    [Fact]
    public void OffersOnlyAThreeWayTOrAFourWayCross()
    {
        foreach (var (requested, expected) in new[] { (2, 3), (3, 3), (4, 4), (8, 4) })
        {
            Assert.Equal(expected, new Intersection(Vector3.Zero, requested).OutletCount);
        }
    }

    [Fact]
    public void BuildsThreeFixedOutletStubsForATJunction()
    {
        var intersection = new Intersection(Vector3.Zero, 3);
        var vertices = Vertices(intersection, "road");
        Assert.Equal((-6.0, 6.0, -6.0, 2.0), (vertices.Min(v => v.X), vertices.Max(v => v.X), vertices.Min(v => v.Z), vertices.Max(v => v.Z)));
        Assert.Equal(new Vector3(-6, 0, 0), intersection.GetNode(0).Position);
        Assert.Equal(new Vector3(6, 0, 0), intersection.GetNode(1).Position);
        Assert.Equal(new Vector3(0, 0, -6), intersection.GetNode(2).Position);
    }

    [Fact]
    public void AddsTheFourthOutletForACrossroads()
    {
        var intersection = new Intersection(Vector3.Zero, 4);
        var vertices = Vertices(intersection, "road");
        Assert.Equal(6, vertices.Max(v => v.Z), 5);
        Assert.Equal(new Vector3(0, 0, 6), intersection.GetNode(3).Position);
        Assert.All(vertices, v => Assert.True(Math.Abs(v.X) <= 2 + 1e-5 || Math.Abs(v.Z) <= 2 + 1e-5));
    }

    [Fact]
    public void JoinsSidewalksWithCleanLCorners()
    {
        var intersection = new Intersection(Vector3.Zero, 4) { EdgeType = "sidewalk", SidewalkWidth = 1, CurbHeight = 0.2 };
        var top = Vertices(intersection, "sidewalk").Where(v => Math.Abs(v.Y - 0.2) < 1e-5).ToList();
        bool HasPoint(double x, double z) => top.Any(v => Math.Abs(v.X - x) < 1e-5 && Math.Abs(v.Z - z) < 1e-5);
        foreach (var (x, z) in new[] { (-6.0, -2.0), (-2, -2), (-2, -6), (-6, -3), (-3, -3), (-3, -6) })
        {
            Assert.True(HasPoint(x, z), $"({x}, {z})");
        }

        Assert.DoesNotContain(top, v => Math.Abs(Math.Abs(v.X) - 4) < 1e-5 || Math.Abs(Math.Abs(v.Z) - 4) < 1e-5);
    }

    [Fact]
    public void ConnectingADifferentlySizedRoadDoesNotChangeTheIntersection()
    {
        var intersection = new Intersection(Vector3.Zero, 4);
        var before = Vertices(intersection, "road");
        var road = new Road(new Vector3(6, 0, 0), new Vector3(20, 0, 0)) { Width = 10 };
        road.Connect(0, intersection, 1);
        Assert.Equal(before, Vertices(intersection, "road"));
    }

    [Fact]
    public void TapersAConnectedRoadToTheOutletWidth()
    {
        var intersection = new Intersection(Vector3.Zero, 4) { OutletWidth = 4 };
        var road = new Road(new Vector3(6, 0, 0), new Vector3(20, 0, 0)) { Width = 10 };
        road.Connect(0, intersection, 1);
        var end = Vertices(road, "road").Where(v => Math.Abs(v.X - 6) < 1e-5).ToList();
        Assert.Equal(-2, end.Min(v => v.Z), 5);
        Assert.Equal(2, end.Max(v => v.Z), 5);
    }

    [Theory]
    [InlineData(6, 0, 20, 5, 0, 1, 6)]
    [InlineData(-20, 5, -6, 0, 1, 0, -6)]
    public void KeepsAnAngledRoadEndpointFlushWithTheMouth(double ax, double az, double bx, double bz, int roadNode, int outlet, double mouthX)
    {
        var intersection = new Intersection(Vector3.Zero, 4) { OutletWidth = 4 };
        var road = new Road(new Vector3(ax, 0, az), new Vector3(bx, 0, bz)) { Width = 4 };
        road.Connect(roadNode, intersection, outlet);
        var end = Vertices(road, "road").Where(v => Math.Abs(v.X - mouthX) < 1e-5).ToList();
        Assert.NotEmpty(end);
        Assert.Equal(-2, end.Min(v => v.Z), 5);
        Assert.Equal(2, end.Max(v => v.Z), 5);
    }

    [Fact]
    public void RoundTripsTheOutletSettings()
    {
        var intersection = new Intersection(new Vector3(2, 1, -3), 3) { Width = 11, Length = 9, OutletWidth = 3.5, OutletLength = 2.75, Rotation = 45 };
        var restored = Intersection.Deserialize(intersection.Serialize(7));
        Assert.Equal((3, 11.0, 9.0, 3.5, 2.75, 45.0), (restored.OutletCount, restored.Width, restored.Length, restored.OutletWidth, restored.OutletLength, restored.Rotation));
    }

    [Fact]
    public void MovesAndRotatesRigidly_ButRejectsDeformingAnOutlet()
    {
        var intersection = new Intersection(Vector3.Zero, 4);
        intersection.Translate(new Vector3(5, 0, 0));
        Assert.Equal(new Vector3(5, 0, 0), intersection.Center);
        intersection.RotateAround(intersection.Center, Quaternion.AngleAxis(90f, Vector3.UnitY));
        Assert.Equal(90, Math.Abs(intersection.Rotation), 4);

        var before = intersection.GetNode(0).Position;
        intersection.GetNode(0).Update(before + new Vector3(0, 0, 3)); // dragging one outlet alone
        Assert.True(intersection.GetNode(0).Position.DistanceTo(before) < 1e-6);
    }
}
