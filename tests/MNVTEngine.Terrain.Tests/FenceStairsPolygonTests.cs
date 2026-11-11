using MNVTEngine.Mathematics;
using MNVTEngine.Terrain.Elements;
using MNVTEngine.Terrain.Geometry;

namespace MNVTEngine.Terrain.Tests;

/// <summary>Ported from TerrainHeaven's Fence.test.ts and Stairs.test.ts, plus polygon terrain checks.</summary>
public sealed class FenceStairsPolygonTests
{
    private static List<Triangle> Group(IEnumerable<GeometryGroup> groups, string name) => groups.First(g => g.Name == name).Triangles;

    private static Vector3 Normal(Triangle t) => (t.B - t.A).Cross(t.C - t.A).Normalized;

    // ---------------------------------------------------------------- Fence

    [Fact]
    public void Fence_BuildsATwoSidedPanelAndEvenlySpacedPosts()
    {
        var fence = new Fence(Vector3.Zero, new Vector3(10, 0, 0)) { PostSpacing = 2 };
        fence.Update();
        var posts = fence.GetPostPositions();
        Assert.Equal(6, posts.Count);
        Assert.Equal(0, posts[0].X, 6);
        Assert.Equal(10, posts[^1].X, 6);
        Assert.True(fence.GetMaterialHint("fence")!.DoubleSided);
        Assert.Equal(2, Group(fence.Geometry, "fence").Count); // six vertices: one quad
    }

    [Fact]
    public void Fence_AddsPostsAsTheCurveTurns()
    {
        var fence = new Fence(Vector3.Zero, new Vector3(10, 0, 0));
        fence.Divisions = 24;
        fence.SetCurvePointA(new Vector3(0, 0, 8));
        fence.SetCurvePointB(new Vector3(10, 0, 8));
        fence.PostSpacing = 100;
        fence.MaxAngleStep = 20;
        Assert.True(fence.GetPostPositions().Count > 2);
    }

    [Fact]
    public void Fence_RoundTripsStyleCurveAndPostSettings()
    {
        var fence = new Fence(new Vector3(1, 2, 3), new Vector3(9, 2, 5)) { Style = "box" };
        fence.Divisions = 8;
        fence.SetCurvePointA(new Vector3(3, 2, 8));
        fence.PostShape = "circle";
        fence.PostSides = 18;
        fence.PostHeight = 2.4;
        var restored = Fence.Deserialize(fence.Serialize(4));
        Assert.Equal(("box", 8, "circle", 18), (restored.Style, restored.Divisions, restored.PostShape, restored.PostSides));
        Assert.Equal(2.4, restored.PostHeight, 6);
        Assert.Equal(new Vector3(3, 2, 8), restored.Serialize(4).CurvePointA!.ToVector3());
    }

    [Fact]
    public void Fence_WindsPostSidesAndCapsOutward()
    {
        var fence = new Fence(Vector3.Zero, new Vector3(4, 0, 0)) { PostSpacing = 10, PostShape = "circle", PostSides = 8 };
        var triangles = Group(fence.ComputeGeometry(), "posts");
        var side = triangles[0];
        var normal = Normal(side);
        var center = (side.A + side.B + side.C) / 3;
        Assert.True(new Vector2(normal.X, normal.Z).Dot(new Vector2(center.X, center.Z)) > 0);
        Assert.True(Normal(triangles[2]).Y > 0.99);
        Assert.True(Normal(triangles[3]).Y < -0.99);
    }

    [Theory]
    [InlineData("plane")]
    [InlineData("box")]
    public void Fence_FitsPanelHeightIntoV01(string style)
    {
        var fence = new Fence(Vector3.Zero, new Vector3(6, 0, 0)) { Style = style, Height = 3.75 };
        var v = Group(fence.ComputeGeometry(), "fence").SelectMany(t => new[] { t.UvA.Y, t.UvB.Y, t.UvC.Y }).ToList();
        Assert.Equal((0.0, 1.0), (v.Min(), v.Max()));
    }

    [Fact]
    public void Fence_TextureStretchPropertyAppliesToFenceUVs()
    {
        var fence = new Fence(Vector3.Zero, new Vector3(6, 0, 0));
        var property = fence.GetProperties().Sections.SelectMany(s => s.Properties).OfType<NumberProperty>().Single(p => p.Label == "Texture Stretch");
        property.Set(0.5);
        var u = Group(fence.ComputeGeometry(), "fence").SelectMany(t => new[] { t.UvA.X, t.UvB.X, t.UvC.X }).ToList();
        Assert.Equal(0, u.Min());
        Assert.Equal(3, u.Max(), 6);
    }

    [Fact]
    public void Fence_WindsEveryBoxFaceOutward()
    {
        var fence = new Fence(Vector3.Zero, new Vector3(4, 0, 0)) { Style = "box", Height = 2, Thickness = 0.4 };
        var triangles = Group(fence.ComputeGeometry(), "fence");
        var boxCenter = new Vector3(2, 1, 0);
        for (var index = 0; index < triangles.Count; index += 2)
        {
            var t = triangles[index];
            var faceCenter = (t.A + t.B + t.C + triangles[index + 1].C) * 0.25;
            Assert.True(Normal(t).Dot(faceCenter - boxCenter) > 0);
        }
    }

    [Fact]
    public void Fence_AddsReversePlaneTrianglesOnlyToExports()
    {
        var fence = new Fence(Vector3.Zero, new Vector3(4, 0, 0));
        Assert.Equal(2, Group(fence.ComputeGeometry(), "fence").Count);
        Assert.Equal(4, Group(fence.GetExportGeometry(0), "fence").Count);
    }

    [Fact]
    public void Fence_KeepsBothTrianglesAlignedWhenStretched()
    {
        var fence = new Fence(Vector3.Zero, new Vector3(8, 0, 0));
        fence.SetUVTransform("fence", new UVTransform { ScaleX = 1.7, ScaleY = 1 });
        var t = Group(fence.ComputeGeometry(), "fence");
        Assert.Equal(2, t.Count);
        Assert.Equal(t[0].UvA.X, t[1].UvA.X, 5);
        Assert.Equal(t[0].UvC.X, t[1].UvB.X, 5);
        Assert.Equal(t[0].UvC.Y, t[1].UvB.Y, 5);
        Assert.Equal(13.6, t[0].UvC.X, 5);
    }

    // ---------------------------------------------------------------- Stairs

    [Fact]
    public void Stairs_BuildsAClosedSteppedPrism()
    {
        var stairs = new Stairs(new Vector3(0, 1, 0), new Vector3(8, 5, 0)) { Width = 4, StepCount = 8, RailingSide = "none" };
        var steps = Group(stairs.GetExportGeometry(0), "steps");
        var vertices = steps.SelectMany(t => new[] { t.A, t.B, t.C }).ToList();
        Assert.Equal((8 * 8) + 4, steps.Count);
        Assert.Equal(5, vertices.Max(v => v.Y), 6);
        Assert.Equal((-2.0, 2.0), (Math.Round(vertices.Min(v => v.Z), 6), Math.Round(vertices.Max(v => v.Z), 6)));
    }

    [Fact]
    public void Stairs_AddsRailingsAndReducesGeometryAtDistantLods()
    {
        var stairs = new Stairs(Vector3.Zero, new Vector3(12, 4, 0)) { StepCount = 24, RailingSide = "both", RailingPostSpacing = 1, MidRail = true };
        var lod0 = stairs.GetExportGeometry(0);
        var lod3 = stairs.GetExportGeometry(3);
        Assert.NotEmpty(Group(lod0, "railings"));
        Assert.True(lod3.Sum(g => g.Triangles.Count) < lod0.Sum(g => g.Triangles.Count));
    }

    [Fact]
    public void Stairs_RoundTripsEverySetting()
    {
        var stairs = new Stairs(new Vector3(1, 2, 3), new Vector3(9, 7, 5))
        {
            Width = 5, StepCount = 17, FoundationDepth = 0.4, RailingSide = "left", RailingHeight = 1.3, RailingThickness = 0.12,
            RailingPostSpacing = 2.2, MidRail = false, CutTerrain = false,
        };
        stairs.SetUVTransform("steps", new UVTransform { OffsetX = 0.2, OffsetY = 0.3, ScaleX = 2, ScaleY = 3 });
        var r = Stairs.Deserialize(stairs.Serialize(6));
        Assert.Equal((5.0, 17, 0.4, "left", 1.3, 0.12, 2.2, false, false), (r.Width, r.StepCount, r.FoundationDepth, r.RailingSide, r.RailingHeight, r.RailingThickness, r.RailingPostSpacing, r.MidRail, r.CutTerrain));
        var uv = r.GetUVTransform("steps");
        Assert.Equal((0.2, 0.3, 2.0, 3.0), (uv.OffsetX, uv.OffsetY, uv.ScaleX, uv.ScaleY));
        Assert.Equal([new Vector3(1, 2, 3), new Vector3(9, 7, 5)], r.Serialize(6).Nodes.Select(n => n.ToVector3()));
    }

    [Fact]
    public void Stairs_CutTerrainWithOneRampFootprint()
    {
        var stairs = new Stairs(Vector3.Zero, new Vector3(6, 3, 0)) { Width = 2 };
        var occupied = stairs.GetOccupiedArea();
        Assert.Equal(2, occupied.Count);
        Assert.Equal([0f, 3f], occupied.SelectMany(t => new[] { t.A.Y, t.B.Y, t.C.Y }).Distinct().Order());
        Assert.All(occupied, t => Assert.Equal(70, t.BankSlopeDegrees));
    }

    // ---------------------------------------------------------------- PolygonTerrain

    [Fact]
    public void PolygonTerrain_FollowsVertexHeights_FacesUp_AndCutsTerrainTiles()
    {
        var polygon = new PolygonTerrain([new Vector3(0, 0, 0), new Vector3(4, 0, 0), new Vector3(4, 2, 4), new Vector3(0, 2, 4)]);
        var triangles = Group(polygon.ComputeGeometry(), "terrain");
        Assert.Equal(2, triangles.Count);
        Assert.All(triangles, t => Assert.True(Normal(t).Y > 0)); // faces up like the tiles around it
        Assert.Equal(2, triangles.SelectMany(t => new[] { t.A, t.B, t.C }).Max(v => v.Y));

        polygon.FlattenToAverageHeight();
        Assert.All(Enumerable.Range(0, polygon.PointCount), i => Assert.Equal(1, polygon.GetPoint(i).Y));
        var restored = PolygonTerrain.Deserialize(polygon.Serialize(2));
        Assert.Equal(4, restored.PointCount);

        var world = new TerrainWorld();
        var tile = new Elements.Terrain(Vector3.Zero, 30, 30);
        world.Add(tile);
        world.Add(polygon);
        world.Update();
        Assert.DoesNotContain(Group(tile.Geometry, "terrain"), t =>
        {
            var c = (t.A + t.B + t.C) / 3;
            return c.X is > 0.1f and < 3.9f && c.Z is > 0.1f and < 3.9f;
        });
    }
}
