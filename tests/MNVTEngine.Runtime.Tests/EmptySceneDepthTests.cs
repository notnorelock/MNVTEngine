using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Assets;
using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Materials;
using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Graphics.Rendering3D;
using MNVTEngine.Graphics.Vulkan;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Runtime.Tests;

/// <summary>
/// Regression: a 3D view of a scene with no meshes returned before clearing depth, so whatever was drawn after it in the
/// same target (the editor's ground grid) was depth-tested against the previous frame and disappeared.
/// </summary>
[Collection(nameof(RuntimeStatics))]
public sealed class EmptySceneDepthTests : IDisposable
{
    private const int Size = 64;
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-depth-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void AnEmptyThreeDScene_ClearsDepth_SoOverlaysDrawnAfterItAreVisible()
    {
        VulkanGraphicsDevice device;
        try
        {
            device = VulkanGraphicsDevice.CreateHeadless(Size, Size, NullLogger.Instance, new VulkanDeviceOptions { Validation = true });
        }
        catch (GraphicsInitializationException ex)
        {
            Assert.Skip($"No Vulkan 1.4 device available: {ex.Message}");
            throw;
        }

        try
        {
            Directory.CreateDirectory(Path.Combine(_root, "Assets"));
            var assets = AssetDatabase.OpenForEditing(Path.Combine(_root, "Assets"), Path.Combine(_root, "Library"));
            using var resources = new ResourceCache(device, assets, NullLogger.Instance);
            using var renderer2D = new Renderer2D(device);
            using var sceneRenderer = new SceneRenderer(renderer2D, resources, NullLogger.Instance);
            using var target = device.CreateRenderTarget(Size, Size, SamplerState.LinearClamp, "depth test", depth: true);

            // Frame 1: a cube right in front of the camera fills the depth buffer with near values.
            var (blocked, blockedCamera) = SceneWithCamera();
            var wall = blocked.CreateGameObject("Wall");
            wall.Transform.Position = new Vector3(0f, 0f, 4f);
            wall.Transform.LocalScale = new Vector3(4f, 4f, 0.2f);
            wall.AddComponent<MeshRenderer>().Primitive = PrimitiveMesh.Cube;
            sceneRenderer.RenderView(blocked, SceneRenderer.CreateView(blockedCamera, Size, Size), Color.Black, target, blockedCamera);

            // Frame 2: the same view of an empty scene, then an overlay farther away than the old wall, as the editor grid is.
            var (empty, camera) = SceneWithCamera();
            sceneRenderer.RenderView(empty, SceneRenderer.CreateView(camera, Size, Size), Color.Black, target, camera);
            DrawOverlayCube(device, resources, camera, target);

            device.SetRenderTarget(target);
            var pixels = new byte[Size * Size * 4];
            device.ReadPixels(pixels);
            device.SetRenderTarget(null);
            var center = ((Size / 2 * Size) + (Size / 2)) * 4;
            Assert.True(pixels[center + 1] > 200 && pixels[center] < 60, $"The overlay is hidden by the previous frame's depth: center pixel is ({pixels[center]}, {pixels[center + 1]}, {pixels[center + 2]}).");
        }
        finally
        {
            device.Dispose();
        }

        Assert.True(device.Validation.Errors == 0, $"{device.Validation.Errors} validation error(s): {device.Validation.LastError}");
    }

    private static (Scene Scene, Camera Camera) SceneWithCamera()
    {
        var scene = new Scene();
        var camera = scene.CreateGameObject("Camera").AddComponent<Camera>();
        camera.Projection = CameraProjection.Perspective;
        camera.ViewportSize = new Vector2(Size, Size);
        camera.Transform.Position = new Vector3(0f, 0f, 5f); // looking down -Z at the origin
        return (scene, camera);
    }

    /// <summary>A green unlit cube at the origin, drawn after the scene into the same target like the editor's grid.</summary>
    private static void DrawOverlayCube(VulkanGraphicsDevice device, ResourceCache resources, Camera camera, RenderTarget2D target)
    {
        var renderer = new Renderer3D(device);
        using var meshes = new GpuMeshCache(device);
        var material = new Material(resources.GetBuiltinShader(Serialization.MaterialDefinition.BuiltinUnlit), BlendMode.Opaque, "overlay");
        material.SetColor("_BaseColor", new Color(0f, 1f, 0f, 1f));
        MaterialShaders.ApplyDefaults(material, MaterialShaders.UnlitProperties, resources.GetDefaultTexture);
        var cube = MeshPrimitives.Cube();
        device.SetRenderTarget(target);
        renderer.Render(new View3D(camera.ViewMatrix, camera.ProjectionMatrix, camera.Transform.Position),
            [new MeshDrawItem(meshes.Get(cube), 0, material, resources.GetDefaultTexture(DefaultTexture.White), Matrix4x4.Identity)]);
        device.SetRenderTarget(null);
    }
}
