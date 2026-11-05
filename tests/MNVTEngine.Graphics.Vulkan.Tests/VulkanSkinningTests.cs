using MNVTEngine.Graphics.Materials;
using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Graphics.Rendering3D;
using MNVTEngine.Mathematics;
using static MNVTEngine.Graphics.Vulkan.Tests.VulkanHarness;

namespace MNVTEngine.Graphics.Vulkan.Tests;

/// <summary>GPU skinning: the skinned shaders move vertices by the draw's joint matrices.</summary>
[Collection(nameof(VulkanGroup))]
public class VulkanSkinningTests
{
    [Fact]
    public void SkinnedShaders_MoveVerticesByTheirJoints() => Run(device =>
    {
        using var standard = device.CreateShader(MaterialShaders.StandardSkinned);
        using var unlit = device.CreateShader(MaterialShaders.UnlitSkinned);
        using var caster = device.CreateShader(MaterialShaders.ShadowCasterSkinned);
        using var white = device.CreateTexture(1, 1, [255, 255, 255, 255], SamplerState.PointClamp);
        using var flat = device.CreateTexture(1, 1, [128, 128, 255, 255], SamplerState.PointClamp);
        var renderer = new Renderer3D(device);
        var material = new Material(standard, BlendMode.Opaque, "Skinned") { Cull = CullMode.None };
        MaterialShaders.ApplyDefaults(material, MaterialShaders.StandardProperties, kind => kind == DefaultTexture.FlatNormal ? flat : white);

        // A 10 x 10 floor: the left half on joint 0, the right half on joint 1.
        var plane = MeshPrimitives.Plane();
        var joints = new ushort[plane.VertexCount * 4];
        var weights = new float[plane.VertexCount * 4];
        for (var v = 0; v < plane.VertexCount; v++)
        {
            joints[v * 4] = plane.Positions[v].X > 0f ? (ushort)1 : (ushort)0;
            weights[v * 4] = 1f;
        }

        using var mesh = GpuMesh.UploadSkinned(device, plane, joints, weights);
        Assert.Equal(SkinnedMeshVertex.Layout, mesh.Vertices.Layout);
        var view = new View3D(Matrix4x4.LookAt(new Vector3(0f, 2f, 0f), Vector3.Zero, Vector3.Forward), Matrix4x4.PerspectiveZeroToOne(60f, 1f, 0.1f, 50f), new Vector3(0f, 2f, 0f));
        byte[] Render(params Matrix4x4[] pose)
        {
            device.Clear(Color.Black);
            device.ClearDepth();
            var bounds = new Bounds(Vector3.Zero, new Vector3(40f, 1f, 40f));
            renderer.Render(view, [new MeshDrawItem(mesh, 0, material, white, Matrix4x4.Identity, Joints: pose, WorldBounds: bounds)]);
            var pixels = new byte[Size * Size * 4];
            device.ReadPixels(pixels);
            return pixels;
        }

        var rest = Render(Matrix4x4.Identity, Matrix4x4.Identity);
        AssertColor(Color.White, Pixel(rest, Size, Size / 4, Size / 2));
        AssertColor(Color.White, Pixel(rest, Size, Size * 3 / 4, Size / 2));

        // Joint 1 drops the right half far below the camera's view: only the left half is drawn.
        var dropped = Render(Matrix4x4.Identity, Matrix4x4.Translate(new Vector3(0f, -100f, 0f)));
        AssertColor(Color.White, Pixel(dropped, Size, Size / 4, Size / 2));
        AssertColor(Color.Black, Pixel(dropped, Size, Size * 3 / 4, Size / 2));
    });
}
