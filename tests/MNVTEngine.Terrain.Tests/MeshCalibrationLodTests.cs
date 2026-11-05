using MNVTEngine.Terrain.Geometry;
using MNVTEngine.Terrain.Meshes;

namespace MNVTEngine.Terrain.Tests;

/// <summary>Ported from TerrainHeaven's MeshCalibration.test.ts, plus prop LOD decimation checks.</summary>
public sealed class MeshCalibrationLodTests
{
    private static MeshGroup Group(double[] positions) => new("g", positions, new double[positions.Length / 3 * 2], Vec3.One, null);

    /// <summary>An n×n grid of quads on y = 0 as a triangle soup, with planar uvs.</summary>
    private static MeshGroup Grid(int n)
    {
        var positions = new List<double>();
        var uvs = new List<double>();
        void Add(int x, int z)
        {
            positions.AddRange([x, 0, z]);
            uvs.AddRange([x / (double)n, z / (double)n]);
        }

        for (var x = 0; x < n; x++)
        {
            for (var z = 0; z < n; z++)
            {
                Add(x, z);
                Add(x, z + 1);
                Add(x + 1, z);
                Add(x + 1, z);
                Add(x, z + 1);
                Add(x + 1, z + 1);
            }
        }

        return new MeshGroup("grid", [.. positions], [.. uvs], Vec3.One, "grid.png");
    }

    [Fact]
    public void MeasuresTheRawBoundingBox()
    {
        var bounds = MeshCalibration.MeasureBounds([Group([-1, 0, -2, 1, 4, 2])]);
        Assert.Equal((new Vec3(-1, 0, -2), new Vec3(1, 4, 2), 2.0, 4.0, 4.0), (bounds.Min, bounds.Max, bounds.Width, bounds.Height, bounds.Depth));
        Assert.Equal(default, MeshCalibration.MeasureBounds([]));
    }

    [Fact]
    public void CentersOnXzAndRestsOnTheGround()
    {
        var bounds = MeshCalibration.MeasureBounds([Group([-1, 2, -3, 3, 6, 5])]);
        Assert.Equal(new Vec3(-1, -2, -1), MeshCalibration.ComputeGroundedCenterOffset(bounds, 1));
        Assert.Equal(new Vec3(-2, -4, -2), MeshCalibration.ComputeGroundedCenterOffset(bounds, 2));
    }

    [Fact]
    public void ScalesFirstThenOffsets_AndIgnoresNonPositiveScales()
    {
        Assert.Equal([0, 1, 0, 1, 3, 0], MeshCalibration.Calibrate([Group([0, 0, 0, 100, 200, 0])], 0.01, new Vec3(0, 1, 0))[0].Positions);
        Assert.Equal([2, 3, 4], MeshCalibration.Calibrate([Group([2, 3, 4])], -5, Vec3.Zero)[0].Positions);
    }

    [Fact]
    public void DecimatesCoarserLodsAndKeepsTheShape()
    {
        var grid = Grid(10);
        var generator = new MeshLodGenerator();
        var lod1 = generator.GetLodGroups("grid.obj", 1, [grid])[0];
        var lod3 = generator.GetLodGroups("grid.obj", 3, [grid])[0];
        Assert.True(lod1.VertexCount < grid.VertexCount);
        Assert.True(lod3.VertexCount < lod1.VertexCount);
        Assert.Equal(lod1.VertexCount / 3 * 2, lod1.Uvs.Length / 3);
        Assert.Equal(("grid", "grid.png"), (lod1.Name, lod1.TexturePath));
        var bounds = MeshCalibration.MeasureBounds([lod1]);
        Assert.Equal((0.0, 0.0), (bounds.Height, bounds.Min.Y)); // a flat plane stays flat
        Assert.Same(lod1, generator.GetLodGroups("grid.obj", 1, [grid])[0]);
        Assert.Same(grid, generator.GetLodGroups("grid.obj", 0, [grid])[0]);
        generator.Invalidate("grid.obj");
        Assert.NotSame(lod1, generator.GetLodGroups("grid.obj", 1, [grid])[0]);
    }

    [Fact]
    public void LeavesTinyGroupsAlone()
    {
        var tiny = Grid(1);
        Assert.Same(tiny, MeshLodGenerator.SimplifyGroup(tiny, 0.05));
    }
}
