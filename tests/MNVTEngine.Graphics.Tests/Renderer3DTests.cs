using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Graphics.Rendering3D;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics.Tests;

public sealed class Renderer3DTests : IDisposable
{
    private readonly FakeGraphicsDevice _device = new();

    public void Dispose() => _device.Dispose();

    private static View3D CameraAt(Vector3 position, Vector3 target) =>
        new(Matrix4x4.LookAt(position, target, Vector3.Up), Matrix4x4.PerspectiveZeroToOne(60f, 1f, 0.1f, 100f), position);

    private (GpuMesh Mesh, Material Material, Texture2D Texture) Resources()
    {
        var mesh = GpuMesh.Upload(_device, MeshPrimitives.Cube());
        return (mesh, new Material(_device.CreateShader(BuiltinShaders.Unlit3D), BlendMode.Opaque), _device.CreateTexture(1, 1, [255, 255, 255, 255], SamplerState.PointClamp));
    }

    [Fact]
    public void CullsOutsideTheFrustum_AndOrdersOpaqueFrontToBack_TransparentBackToFront()
    {
        var (mesh, material, texture) = Resources();
        var view = CameraAt(new Vector3(0f, 0f, 10f), Vector3.Zero);
        MeshDrawItem At(float z, bool transparent = false, CullMode cull = CullMode.Back) =>
            new(mesh, 0, material, texture, Matrix4x4.Translate(new Vector3(0f, 0f, z)), transparent, cull);

        var stats = new Renderer3D(_device).Render(view, [
            At(-5f), At(5f), At(0f),                        // opaque: drawn nearest first
            At(-4f, transparent: true), At(4f, transparent: true, cull: CullMode.None), // transparent: farthest first
            At(20f),                                         // behind the camera
            new(mesh, 0, material, texture, Matrix4x4.Translate(new Vector3(500f, 0f, 0f))), // far to the side
        ]);

        Assert.Equal((5, 2, 5), (stats.Visible, stats.Culled, stats.DrawCalls));
        Assert.Equal(5 * 12, stats.Triangles);
        var z = _device.Draws.Select(d => d.World!.Value.Translation.Z).ToArray();
        Assert.Equal([5f, 0f, -5f, -4f, 4f], z);
        Assert.All(_device.Draws.Take(3), d => Assert.Equal(RenderState.Opaque3D, d.State));
        Assert.Equal(RenderState.Transparent3D, _device.Draws[3].State);
        Assert.Equal(RenderState.Transparent3D with { Cull = CullMode.None }, _device.Draws[4].State);
        Assert.All(_device.Draws, d => Assert.Equal(view.ViewProjection, d.ViewProjection));
    }

    [Fact]
    public void Submeshes_DrawTheirIndexRange_AndInvalidOnesThrow()
    {
        var cpu = MeshPrimitives.Cube();
        cpu.SetIndices([.. cpu.IndexSpan], new SubMesh(0, 12), new SubMesh(12, 24));
        var mesh = GpuMesh.Upload(_device, cpu);
        var (_, material, texture) = Resources();
        var renderer = new Renderer3D(_device);
        var view = CameraAt(new Vector3(0f, 0f, 5f), Vector3.Zero);
        renderer.Render(view, [new MeshDrawItem(mesh, 1, material, texture, Matrix4x4.Identity)]);
        Assert.Equal((12, 24), (_device.Draws[0].IndexOffset, _device.Draws[0].IndexCount));
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.Render(view, [new MeshDrawItem(mesh, 2, material, texture, Matrix4x4.Identity)]));
    }

    [Fact]
    public void TransformBounds_EnclosesTheRotatedScaledBox()
    {
        var local = new Bounds(Vector3.Zero, new Vector3(2f, 2f, 2f));
        var world = Matrix4x4.TRS(new Vector3(10f, 0f, 0f), Quaternion.Euler(0f, 45f, 0f), new Vector3(1f, 3f, 1f));
        var bounds = Renderer3D.TransformBounds(local, world);
        Assert.True(Vector3.Distance(new Vector3(10f, 0f, 0f), bounds.Center) < 1e-4f);
        Assert.Equal(2f * MathF.Sqrt(2f), bounds.Size.X, 4); // the diagonal after a 45° turn
        Assert.Equal(6f, bounds.Size.Y, 4);
    }
}
