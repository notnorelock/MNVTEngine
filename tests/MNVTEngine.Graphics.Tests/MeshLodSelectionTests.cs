using MNVTEngine.Assets.Models;
using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Mathematics;
using MNVTEngine.Runtime;

namespace MNVTEngine.Graphics.Tests;

public sealed class MeshLodSelectionTests
{
    private static ModelMesh Sphere()
    {
        var mesh = new ModelMesh("sphere", MeshPrimitives.Sphere(1f, 32, 16));
        mesh.Lods.Add(new ModelMeshLod(MeshSimplifier.Simplify(mesh.Mesh, 0.5f).Mesh, 0.5f));
        mesh.Lods.Add(new ModelMeshLod(MeshSimplifier.Simplify(mesh.Mesh, 0.2f).Mesh, 0.1f));
        return mesh;
    }

    [Theory]
    [InlineData(2f, 0)]   // fills the view
    [InlineData(8f, 1)]   // its bounds span about a third of the height
    [InlineData(40f, 2)]  // small in the distance
    public void Perspective_PicksTheLevelByScreenSize(float distance, int level)
    {
        var mesh = Sphere();
        var projection = Matrix4x4.PerspectiveZeroToOne(60f, 16f / 9f, 0.1f, 100f);
        Assert.Equal(level, MeshScenePass.LodLevel(mesh, mesh.Bounds, projection, new Vector3(0f, 0f, distance)));
    }

    [Fact]
    public void Orthographic_UsesTheViewHeight()
    {
        var mesh = Sphere(); // 2 units across, about 3.5 diagonally
        Assert.Equal(0, MeshScenePass.LodLevel(mesh, mesh.Bounds, Matrix4x4.OrthoZeroToOne(-3f, 3f, -3f, 3f, 0.1f, 100f), new Vector3(0f, 0f, 500f)));
        Assert.Equal(2, MeshScenePass.LodLevel(mesh, mesh.Bounds, Matrix4x4.OrthoZeroToOne(-30f, 30f, -30f, 30f, 0.1f, 100f), Vector3.Zero));
    }
}
