using MNVTEngine.Graphics.Materials;
using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Graphics.Rendering3D;
using MNVTEngine.Mathematics;
using static MNVTEngine.Graphics.Vulkan.Tests.VulkanHarness;

namespace MNVTEngine.Graphics.Vulkan.Tests;

/// <summary>GPU instancing: copies of a mesh with one material become one draw that looks like the separate draws.</summary>
[Collection(nameof(VulkanGroup))]
public class VulkanInstancingTests
{
    [Fact]
    public void SharedMeshAndMaterial_DrawOnce_AndLookTheSame() => Run(device =>
    {
        using var standard = device.CreateShader(MaterialShaders.Standard);
        using var instancedShader = device.CreateShader(MaterialShaders.StandardInstanced);
        using var white = device.CreateTexture(1, 1, [255, 255, 255, 255], SamplerState.PointClamp);
        using var flat = device.CreateTexture(1, 1, [128, 128, 255, 255], SamplerState.PointClamp);
        using var meshes = new GpuMeshCache(device);
        var material = new Material(standard, BlendMode.Opaque, "Shared");
        MaterialProperties.ApplyDefaults(material, MaterialShaders.StandardProperties, kind => kind == DefaultTexture.FlatNormal ? flat : white);
        material.SetColor("_BaseColor", new Color(0.2f, 0.6f, 1f, 1f));
        var twin = new Material(instancedShader, BlendMode.Opaque, "Shared (instanced)");
        var renderer = new Renderer3D(device);

        // Three small floors in a row under a camera looking down; the gaps between them stay black.
        var plane = meshes.Get(MeshPrimitives.Plane());
        MeshDrawItem Floor(float x) => new(plane, 0, material, white, Matrix4x4.TRS(new Vector3(x, 0f, 0f), Quaternion.Identity, new Vector3(0.1f, 1f, 0.1f)));
        MeshDrawItem[] items = [Floor(-1.2f), Floor(0f), Floor(1.2f)];
        var view = new View3D(Matrix4x4.LookAt(new Vector3(0f, 3f, 0f), Vector3.Zero, Vector3.Forward), Matrix4x4.PerspectiveZeroToOne(60f, 1f, 0.1f, 50f), new Vector3(0f, 3f, 0f));
        (byte[] Pixels, Render3DStatistics Stats) Render()
        {
            device.Clear(Color.Black);
            device.ClearDepth();
            var stats = renderer.Render(view, items);
            var pixels = new byte[Size * Size * 4];
            device.ReadPixels(pixels);
            return (pixels, stats);
        }

        var (separate, separateStats) = Render();
        Assert.Equal((3, 0), (separateStats.DrawCalls, separateStats.InstancedItems));

        renderer.InstancedVariant = source =>
        {
            twin.CopyFrom(source);
            return twin;
        };
        var (instanced, instancedStats) = Render();
        Assert.Equal((1, 3, 3), (instancedStats.DrawCalls, instancedStats.InstancedItems, instancedStats.Visible));
        Assert.Equal(separateStats.Triangles, instancedStats.Triangles);
        for (var x = 0; x < Size; x += 4)
        {
            AssertColor(Pixel(separate, Size, x, Size / 2), Pixel(instanced, Size, x, Size / 2), 3f / 255f);
        }

        Assert.NotEqual(Color.Black, Pixel(instanced, Size, Size / 2, Size / 2));
    });
}
