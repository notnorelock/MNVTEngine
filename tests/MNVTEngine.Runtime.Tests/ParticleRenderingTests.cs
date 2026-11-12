using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Assets;
using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Vulkan;
using MNVTEngine.Graphics.Vulkan.ShaderCompilation;
using MNVTEngine.Mathematics;
using MNVTEngine.Particles;
using MNVTEngine.Scenes;

namespace MNVTEngine.Runtime.Tests;

/// <summary>The 2D and 3D particle renderers on a real Vulkan device, through the scene renderer and the modules' passes.</summary>
[Collection(nameof(RuntimeStatics))]
public sealed class ParticleRenderingTests : IDisposable
{
    private const int Size = 64;
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-particles-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void ThreeD_Billboards_AreDepthTested_SortedBackToFront_AndAdditive()
    {
        WithDevice((device, assets) =>
        {
            using var resources = new ResourceCache(device, assets, NullLogger.Instance);
            using var renderer2D = new Renderer2D(device);
            using var scenes = new SceneRenderer(renderer2D, resources, NullLogger.Instance,
                ModuleIntegration.CreatePasses([new World3D.Rendering.World3DModule(), new Particles3D.Particles3DModule()], renderer2D, resources, NullLoggerFactory.Instance));
            using var target = device.CreateRenderTarget(Size, Size, SamplerState.LinearClamp, "particles 3D", depth: true);
            var (scene, camera) = Scene3D();
            var system = Particles(scene, ParticleBlendMode.Alpha);
            Put(system, Vector3.Zero, Color.Red);

            Assert.Equal(Color.Red, Center(Render(scenes, scene, camera, target, device)));
            Assert.Equal(1, scenes.Statistics[Particles3D.ParticleScenePass3D.ParticlesDrawn]);

            // An opaque cube in front hides the particle (depth from the 3D pass).
            var wall = scene.CreateGameObject("Wall");
            wall.Transform.Position = new Vector3(0f, 0f, 2f);
            wall.AddComponent<MeshRenderer>().Primitive = PrimitiveMesh.Cube;
            Assert.NotEqual(Color.Red, Center(Render(scenes, scene, camera, target, device)));
            wall.Destroy();

            // Two overlapping particles, the nearer one (blue) first in the buffer: sorted by distance it ends on top;
            // unsorted, the later (farther, red) one covers it because particles do not write depth.
            system.Clear();
            Put(system, new Vector3(0f, 0f, 1f), Color.Blue);
            Put(system, Vector3.Zero, Color.Red);
            system.Settings.Renderer.SortMode = ParticleSortMode.ByDistance;
            Assert.Equal(Color.Blue, Center(Render(scenes, scene, camera, target, device)));
            system.Settings.Renderer.SortMode = ParticleSortMode.None;
            Assert.Equal(Color.Red, Center(Render(scenes, scene, camera, target, device)));

            // Additive: two half-green particles add up to full green over black.
            system.Clear();
            system.Settings.Renderer.Blend = ParticleBlendMode.Additive;
            Put(system, Vector3.Zero, new Color(0f, 0.5f, 0f));
            Assert.InRange(Center(Render(scenes, scene, camera, target, device)).G, 0.45f, 0.55f);
            Put(system, Vector3.Zero, new Color(0f, 0.5f, 0f));
            Assert.InRange(Center(Render(scenes, scene, camera, target, device)).G, 0.95f, 1f);
        });
    }

    [Fact]
    public void TwoD_Particles_SampleSpriteSheetFrames_BatchAndBlend()
    {
        // A 2x1 sprite sheet: red frame 0, blue frame 1.
        var assetsDirectory = Path.Combine(_root, "Assets");
        Directory.CreateDirectory(assetsDirectory);
        using (var file = File.Create(Path.Combine(assetsDirectory, "sheet.png")))
        {
            new StbImageWriteSharp.ImageWriter().WritePng(new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 }, 2, 1, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, file);
        }

        WithDevice((device, assets) =>
        {
            using var resources = new ResourceCache(device, assets, NullLogger.Instance);
            using var renderer2D = new Renderer2D(device);
            using var scenes = new SceneRenderer(renderer2D, resources, NullLogger.Instance,
                ModuleIntegration.CreatePasses([new World2D.Rendering.World2DModule(), new Particles2D.Particles2DModule()], renderer2D, resources, NullLoggerFactory.Instance));
            using var target = device.CreateRenderTarget(Size, Size, SamplerState.PointClamp, "particles 2D");
            var scene = new Scene();
            var camera = scene.CreateGameObject("Camera").AddComponent<Camera>();
            var system = Particles(scene, ParticleBlendMode.Alpha);
            system.Settings.Renderer.Texture = new AssetRef(assets.Assets.Single(a => a.Path == "sheet.png").Guid);
            system.Settings.TextureSheetAnimation.Enabled = true;
            system.Settings.TextureSheetAnimation.Columns = 2;
            for (var i = 0; i < 100; i++)
            {
                Put(system, Vector3.Zero, Color.White);
            }

            var particles = system.Simulation.Particles;
            for (var i = 0; i < particles.Count; i++)
            {
                particles.Frame[i] = 1;
            }

            var view = SceneRenderer.CreateView(camera, Size, Size);
            var blue = Center(Render2D(scenes, scene, view, target, device));
            Assert.True(blue.B > 0.9f && blue.R < 0.1f, $"Frame 1 should be blue, got {blue}"); // linear filtering may blend a few percent of the neighbor texel
            Assert.Equal(100, scenes.Statistics[Particles2D.ParticleScenePass2D.ParticlesDrawn]);
            Assert.True(scenes.BatchesDrawn <= 2, $"{scenes.BatchesDrawn} draw calls for 100 particles of one texture"); // batched, not one per particle

            particles.Frame[particles.Count - 1] = 0;
            var red = Center(Render2D(scenes, scene, view, target, device));
            Assert.True(red.R > 0.9f && red.B < 0.1f, $"The last particle (frame 0, red) draws on top, got {red}");

            // Alpha blending over the background.
            system.Clear();
            system.Settings.Renderer.Texture = AssetRef.None;
            Put(system, Vector3.Zero, new Color(1f, 1f, 1f, 0.5f));
            Assert.InRange(Center(Render2D(scenes, scene, view, target, device)).R, 0.45f, 0.55f);
        });
    }

    private void WithDevice(Action<VulkanGraphicsDevice, AssetDatabase> body)
    {
        VulkanGraphicsDevice device;
        try
        {
            device = VulkanGraphicsDevice.CreateHeadless(Size, Size, NullLogger.Instance, new VulkanDeviceOptions { Validation = true }.WithShaderc());
        }
        catch (GraphicsInitializationException ex)
        {
            Assert.Skip($"No Vulkan 1.4 device available: {ex.Message}");
            throw;
        }

        try
        {
            Directory.CreateDirectory(Path.Combine(_root, "Assets"));
            body(device, AssetDatabase.OpenForEditing(Path.Combine(_root, "Assets"), Path.Combine(_root, "Library")));
        }
        finally
        {
            device.Dispose();
        }

        Assert.True(device.Validation.Errors == 0, $"{device.Validation.Errors} validation error(s): {device.Validation.LastError}");
    }

    private static (Scene Scene, Camera Camera) Scene3D()
    {
        var scene = new Scene();
        var camera = scene.CreateGameObject("Camera").AddComponent<Camera>();
        camera.Projection = CameraProjection.Perspective;
        camera.ViewportSize = new Vector2(Size, Size);
        camera.Transform.Position = new Vector3(0f, 0f, 5f); // looking down -Z at the origin
        return (scene, camera);
    }

    /// <summary>A world-space system that only emits by hand, with motionless, view-aligned particles 2 units across.</summary>
    private static ParticleSystem Particles(Scene scene, ParticleBlendMode blend)
    {
        var system = scene.CreateGameObject("Particles").AddComponent<ParticleSystem>();
        system.Main.SimulationSpace = ParticleSimulationSpace.World;
        system.Main.StartSpeed = 0f;
        system.Main.StartSize = 2f;
        system.Emission.RateOverTime = 0f;
        system.Shape.Kind = ParticleShapeKind.Point;
        system.Settings.Renderer.Blend = blend;
        system.Settings.Renderer.RenderMode = ParticleRenderMode.ViewAligned;
        return system;
    }

    private static void Put(ParticleSystem system, Vector3 position, Color color)
    {
        Assert.Equal(1, system.Emit(1));
        var particles = system.Simulation.Particles;
        var i = particles.Count - 1;
        particles.Position[i] = position;
        particles.Color[i] = particles.StartColor[i] = color;
    }

    private static byte[] Render(SceneRenderer scenes, Scene scene, Camera camera, RenderTarget2D target, IGraphicsDevice device)
    {
        scenes.RenderView(scene, SceneRenderer.CreateView(camera, Size, Size), Color.Black, target, camera);
        return Read(target, device);
    }

    private static byte[] Render2D(SceneRenderer scenes, Scene scene, Camera2D view, RenderTarget2D target, IGraphicsDevice device)
    {
        scenes.RenderView(scene, view, Color.Black, target);
        return Read(target, device);
    }

    private static byte[] Read(RenderTarget2D target, IGraphicsDevice device)
    {
        device.SetRenderTarget(target);
        var pixels = new byte[Size * Size * 4];
        device.ReadPixels(pixels);
        device.SetRenderTarget(null);
        return pixels;
    }

    private static Color Center(byte[] pixels)
    {
        var i = ((Size / 2 * Size) + (Size / 2)) * 4;
        return Color.FromBytes(pixels[i], pixels[i + 1], pixels[i + 2], 255);
    }
}
