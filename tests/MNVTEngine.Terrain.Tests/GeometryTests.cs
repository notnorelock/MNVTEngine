using MNVTEngine.Terrain.Geometry;

namespace MNVTEngine.Terrain.Tests;

public sealed class GeometryTests
{
    private static double TriangleArea(Vec2 a, Vec2 b, Vec2 c) => Math.Abs((b - a).Cross(c - a)) / 2;

    [Fact]
    public void Earcut_TriangulatesASquareWithAHole_CoveringExactlyItsArea()
    {
        List<Vec2> contour = [new(0, 0), new(10, 0), new(10, 10), new(0, 10)];
        List<List<Vec2>> holes = [[new(4, 4), new(4, 6), new(6, 6), new(6, 4)]];
        var points = contour.Concat(holes[0]).ToList();
        var faces = ShapeUtils.TriangulateShape(contour, holes);
        Assert.Equal(8, faces.Count);
        Assert.Equal(96, faces.Sum(f => TriangleArea(points[f[0]], points[f[1]], points[f[2]])), 6);
    }

    [Fact]
    public void Poly2Tri_HonoursHolesAndSteinerPoints()
    {
        var context = new P2TSweepContext([new(0, 0), new(10, 0), new(10, 10), new(0, 10)]);
        context.AddHole([new(4, 4), new(6, 4), new(6, 6), new(4, 6)]);
        context.AddPoints([new(2, 2), new(8, 8)]);
        context.Triangulate();

        var triangles = context.Triangles;
        Assert.Equal(96, triangles.Sum(t => TriangleArea(new(t.Points[0].X, t.Points[0].Y), new(t.Points[1].X, t.Points[1].Y), new(t.Points[2].X, t.Points[2].Y))), 6);
        Assert.Contains(triangles, t => t.Points.Any(p => p.X == 2 && p.Y == 2)); // Steiner points are vertices
        Assert.DoesNotContain(triangles, t => t.Points.Average(p => p.X) is > 4 and < 6 && t.Points.Average(p => p.Y) is > 4 and < 6);
        Assert.Throws<Poly2TriException>(() => new P2TSweepContext([new(0, 0), new(0, 0), new(1, 1)]));
    }

    [Fact]
    public void PolygonClipping_UnionsAndCutsHoles()
    {
        var a = PolygonClipping.Single([new(0, 0), new(10, 0), new(10, 10), new(0, 10)]);
        var b = PolygonClipping.Single([new(10, 0), new(20, 0), new(20, 10), new(10, 10)]);
        var union = PolygonClipping.Union(a, b);
        Assert.Single(union);
        Assert.Single(union[0]); // the shared edge dissolved: one ring
        Assert.Equal(5, union[0][0].Count); // a closed rectangle

        var hole = PolygonClipping.Single([new(4, 4), new(6, 4), new(6, 6), new(4, 6)]);
        var cut = PolygonClipping.Difference(union, hole);
        Assert.Equal(2, cut[0].Count); // outer + hole
        Assert.True(ShapeUtils.Area(cut[0][0][..^1]) > 0); // outer counter-clockwise
        Assert.True(ShapeUtils.Area(cut[0][1][..^1]) < 0); // hole clockwise
        Assert.Empty(PolygonClipping.Difference(a, a));
    }
}
