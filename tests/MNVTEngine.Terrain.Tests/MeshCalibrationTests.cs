using MNVTEngine.Mathematics;
using MNVTEngine.Terrain.Elements;
using MNVTEngine.Terrain.Meshes;
using MNVTEngine.Terrain.Meshing;

namespace MNVTEngine.Terrain.Tests;

/// <summary>Ported from TerrainHeaven's MeshCalibration.test.ts and ObjWriter.test.ts (auto-smooth normals).</summary>
public sealed class MeshCalibrationTests
{
    [Fact]
    public void MeasuresTheRawBoundingBox()
    {
        var bounds = MeshCalibration.MeasureBounds([new Vector3(-1, 0, -2), new Vector3(1, 4, 2)]);
        Assert.Equal((new Vector3(-1, 0, -2), new Vector3(1, 4, 2), 2.0, 4.0, 4.0), (bounds.Min, bounds.Max, bounds.Width, bounds.Height, bounds.Depth));
        Assert.Equal(default, MeshCalibration.MeasureBounds([]));
    }

    [Fact]
    public void CentersOnXzAndRestsOnTheGround()
    {
        var bounds = MeshCalibration.MeasureBounds([new Vector3(-1, 2, -3), new Vector3(3, 6, 5)]);
        Assert.Equal(new Vector3(-1, -2, -1), MeshCalibration.ComputeGroundedCenterOffset(bounds, 1));
        Assert.Equal(new Vector3(-2, -4, -2), MeshCalibration.ComputeGroundedCenterOffset(bounds, 2));
    }

    private static GeometryGroup Quad(string name, Vector3 a, Vector3 b, Vector3 c, Vector3 d) => new(name, [new Triangle(a, b, c), new Triangle(a, c, d)]);

    [Fact]
    public void SmoothNormals_GiveAFlatSurfaceOneNormal()
    {
        var normals = SmoothNormals.Compute([Quad("floor", new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 0, 0))])[0];
        Assert.Equal(6, normals.Length);
        Assert.All(normals, n => Assert.True(Vector3.Approximately(n, Vector3.UnitY)));
    }

    [Fact]
    public void SmoothNormals_KeepASharpCornerCrisp_AndSmoothAShallowFold()
    {
        var floor = Quad("floor", new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 0, 0));
        var wall = Quad("wall", new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(1, 1, 1), new Vector3(1, 1, 0));
        var normals = SmoothNormals.Compute([floor, wall]);
        Assert.All(normals[0], n => Assert.True(n.Dot(Vector3.UnitY) > 0.99f));
        Assert.All(normals[1], n => Assert.True(Math.Abs(n.Dot(Vector3.UnitX)) > 0.99f));

        // A 10° fold sits inside the 30° threshold: the shared edge averages both faces.
        var tilted = Quad("tilted", new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(2, (float)Math.Tan(Math.PI / 18), 1), new Vector3(2, (float)Math.Tan(Math.PI / 18), 0));
        var fold = SmoothNormals.Compute([floor, tilted]);
        Assert.True(fold[0][2].Dot(Vector3.UnitY) < 0.9999f); // the floor corner on the fold leans toward the slope
    }
}
