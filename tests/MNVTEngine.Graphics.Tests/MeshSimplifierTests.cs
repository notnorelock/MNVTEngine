using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics.Tests;

public sealed class MeshSimplifierTests
{
    [Fact]
    public void Sphere_LosesTriangles_KeepsItsShape_AndMapsVerticesBack()
    {
        var sphere = MeshPrimitives.Sphere(1f, 48, 24);
        var lod = MeshSimplifier.Simplify(sphere, 0.25f);
        Assert.InRange(lod.Mesh.TriangleCount, sphere.TriangleCount / 8, sphere.TriangleCount * 3 / 10);
        Assert.Empty(lod.Mesh.Validate());
        Assert.Equal(lod.Mesh.VertexCount, lod.SourceVertices.Length);
        for (var i = 0; i < lod.Mesh.VertexCount; i++)
        {
            Assert.Equal(sphere.Positions[lod.SourceVertices[i]], lod.Mesh.Positions[i]); // a subset: attributes carry over unchanged
            Assert.Equal(sphere.UV0![lod.SourceVertices[i]], lod.Mesh.UV0![i]);
        }

        Assert.All(lod.Mesh.Positions, p => Assert.InRange(p.Magnitude, 0.99f, 1.01f)); // still on the sphere
        Assert.InRange(lod.Mesh.Bounds.Size.X, 1.8f, 2.01f);
    }

    [Fact]
    public void FlatGrid_CollapsesInside_ButKeepsItsBorder()
    {
        var plane = MeshPrimitives.Plane(10f, 10f, 10);
        var lod = MeshSimplifier.Simplify(plane, 0.1f);
        Assert.True(lod.Mesh.TriangleCount < plane.TriangleCount / 2);
        Assert.Equal(plane.Bounds.Size, lod.Mesh.Bounds.Size);
        var area = 0f;
        var p = lod.Mesh.Positions;
        var i = lod.Mesh.IndexSpan;
        for (var t = 0; t < i.Length; t += 3)
        {
            var normal = Vector3.Cross(p[i[t + 1]] - p[i[t]], p[i[t + 2]] - p[i[t]]);
            Assert.True(normal.Y > 0f); // nothing turned over
            area += normal.Magnitude * 0.5f;
        }

        Assert.Equal(100f, area, 2); // no holes
    }

    [Fact]
    public void SeamsAndSubmeshes_AreKept()
    {
        var cube = MeshPrimitives.Cube(); // every face has its own vertices: all of them are seams
        Assert.Equal(cube.TriangleCount, MeshSimplifier.Simplify(cube, 0.5f).Mesh.TriangleCount);

        var plane = MeshPrimitives.Plane(2f, 2f, 8);
        var half = plane.IndexSpan.Length / 2 / 3 * 3;
        plane.SetIndices(plane.IndexSpan.ToArray(), new SubMesh(0, half), new SubMesh(half, plane.IndexSpan.Length - half));
        var lod = MeshSimplifier.Simplify(plane, 0.2f);
        Assert.Equal(2, lod.Mesh.SubMeshes.Count);
        Assert.All(lod.Mesh.SubMeshes, s => Assert.True(s.IndexCount > 0));
    }
}
