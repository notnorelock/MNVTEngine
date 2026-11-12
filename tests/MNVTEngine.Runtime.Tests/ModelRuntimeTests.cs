using Microsoft.Extensions.Logging;
using MNVTEngine.Assets;
using MNVTEngine.Assets.Models;
using MNVTEngine.Core.Logging;
using MNVTEngine.Graphics.Vulkan;
using MNVTEngine.Platform;
using MNVTEngine.Scenes;

namespace MNVTEngine.Runtime.Tests;

/// <summary>Imported models at runtime: instantiation, references that survive serialization, and drawing from the artifact.</summary>
public sealed class ModelRuntimeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-model-runtime-").FullName;

    public ModelRuntimeTests() => Directory.CreateDirectory(Path.Combine(_root, "Assets", "Scenes"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Fixture(string relative) => Path.Combine(AppContext.BaseDirectory, "Models", relative);

    private Guid ImportFixture(string fixtureFolder, string file, out ModelData model)
    {
        var source = Fixture(fixtureFolder);
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(_root, "Assets", "Models", Path.GetRelativePath(source, path));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(path, destination);
        }

        var db = AssetDatabase.OpenForEditing(Path.Combine(_root, "Assets"), Path.Combine(_root, "Library"), ModelImporters.WithDefaults());
        Assert.True(db.TryGetGuid("Models/" + file, out var guid));
        model = ModelArtifact.Read(db.ReadArtifact(guid, ModelArtifact.Name)!);
        return guid;
    }

    private Guid ImportExisting(string file, out ModelData model)
    {
        var db = AssetDatabase.OpenForEditing(Path.Combine(_root, "Assets"), Path.Combine(_root, "Library"), ModelImporters.WithDefaults());
        Assert.True(db.TryGetGuid("Models/" + file, out var guid));
        Assert.True(db.Get(guid)!.Status == AssetImportStatus.Imported, string.Join("\n", db.Get(guid)!.Messages));
        model = ModelArtifact.Read(db.ReadArtifact(guid, ModelArtifact.Name)!);
        return guid;
    }

    private static unsafe void ExportFbx(string source, string destination)
    {
        var api = Silk.NET.Assimp.Assimp.GetApi();
        var scene = api.ImportFile(source, 0);
        Assert.True(scene is not null, api.GetErrorStringS());
        try
        {
            Assert.Equal(Silk.NET.Assimp.Return.Success, api.ExportScene(scene, "fbx", destination, 0));
        }
        finally
        {
            api.ReleaseImport(scene);
        }
    }

    [Fact]
    public void Instantiate_BuildsHierarchy_WithMeshReferences_ThatSurviveSerialization()
    {
        var guid = ImportFixture("Khronos", "BoxAnimated.glb", out var model);
        var scene = new Scene();
        var root = ModelInstantiation.Instantiate(scene, guid, model, "Box");

        Assert.Equal("Box", root.Name);
        Assert.Equal(model.Nodes.Count, scene.GameObjects.Count);
        var renderers = scene.FindObjectsOfType<MeshRenderer>().ToList();
        Assert.Equal(model.Nodes.Count(n => n.Mesh >= 0), renderers.Count);
        Assert.All(renderers, r => Assert.Equal(PrimitiveMesh.None, r.Primitive));
        Assert.All(renderers, r => Assert.Equal(guid, r.Mesh.Guid));
        Assert.All(renderers, r => Assert.NotNull(model.FindMesh(r.Mesh.SubAsset![ModelCache.MeshSubAssetPrefix.Length..])));

        var serializer = new SceneSerializer(Build.EngineModuleSet.CreateRegistry());
        var copy = serializer.Deserialize(serializer.Serialize(scene)).Scene;
        Assert.Equal(renderers.Select(r => r.Mesh.ToString()).Order().ToList(), copy.FindObjectsOfType<MeshRenderer>().Select(r => r.Mesh.ToString()).Order().ToList());
    }

    [Fact]
    public void Instantiate_AddsTheModelsLights_AndDisabledCameras()
    {
        var lights = ImportFixture("Lights", "PunctualLights.gltf", out var lightModel);
        var scene = new Scene();
        ModelInstantiation.Instantiate(scene, lights, lightModel, "Lamps");
        var spot = scene.Find("SpotNode")!.GetComponent<Light>()!;
        Assert.Equal((LightType.Spot, 12f), (spot.Type, spot.Range));
        Assert.Equal(1f, spot.Intensity, 3);
        var bulb = scene.Find("BulbNode")!.GetComponent<Light>()!;
        Assert.Equal(MathF.Sqrt(10f), bulb.Range, 3); // no range in the file: where it has faded to 1%
        Assert.Equal(LightType.Directional, scene.Find("SunNode")!.GetComponent<Light>()!.Type);

        var cameras = ImportFixture("Khronos", "Cameras.gltf", out var cameraModel);
        var withCameras = new Scene();
        ModelInstantiation.Instantiate(withCameras, cameras, cameraModel, "Cameras");
        var all = withCameras.FindObjectsOfType<Camera>().ToList();
        Assert.Equal(2, all.Count);
        Assert.All(all, c => Assert.False(c.Enabled)); // they do not take over the scene's main camera
        Assert.Contains(all, c => c.Projection == CameraProjection.Orthographic && c.OrthographicSize == 1f);
        Assert.Null(withCameras.MainCamera);
    }

    [Fact]
    public void ModelMesh_RendersFromTheArtifact_WithItsTexturedMaterial()
    {
        // The scene the editor would save after dropping the model: its instantiated hierarchy, seen from the front.
        var guid = ImportFixture("WoodenCrate/gltf", "wooden_crate_01_1k.gltf", out var model);
        var scene = new Scene { Name = "Crate" };
        var camera = scene.CreateGameObject("Camera");
        camera.Transform.LocalPosition = new Mathematics.Vector3(0f, 0.3f, 1.5f);
        var view = camera.AddComponent<Camera>();
        (view.Projection, view.FieldOfView, view.BackgroundColor) = (CameraProjection.Perspective, 60f, new Mathematics.Color(0f, 0f, 0f, 1f));
        ModelInstantiation.Instantiate(scene, guid, model, "Crate");
        var meshCount = model.Nodes.Count(n => n.Mesh >= 0);
        File.WriteAllText(Path.Combine(_root, "Assets", "Scenes", "First.mnvt"), new SceneSerializer(Build.EngineModuleSet.CreateRegistry()).Serialize(scene));
        File.WriteAllText(Path.Combine(_root, "Test.mnvtproj"), """{ "schemaVersion": 1, "name": "Test", "startupScene": "Scenes/First.mnvt", "loadingScreen": { "enabled": false } }""");
        File.WriteAllText(Path.Combine(_root, "Assets", "Scenes", "First.mnvt.meta"), $$"""{ "schemaVersion": 1, "guid": "{{Guid.NewGuid()}}", "settings": {} }""");

        var screenshot = Path.Combine(_root, "crate.png");
        using var logs = new InMemoryLoggerProvider(minimumLevel: LogLevel.Debug);
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var runtime = RunHidden(GameProject.Load(_root), factory, screenshot);

        Assert.Equal(0, runtime.ExitCode); // model meshes and embedded textures are released
        var warnings = logs.Snapshot().Where(e => e.Level >= LogLevel.Warning && e.Category != "Graphics").Select(e => $"{e.Category}: {e.Message}").ToList(); // Vulkan loader messages depend on the machine
        Assert.True(warnings.Count == 0, string.Join("\n", warnings));
        Assert.Contains(logs.Snapshot(), e => e.Message.Contains($"{meshCount} meshes drawn", StringComparison.Ordinal));
        var image = Graphics.Image.Load(screenshot);
        var center = ((image.Height / 2 * image.Width) + (image.Width / 2)) * 4;
        Assert.True(image.Pixels[center] + image.Pixels[center + 1] + image.Pixels[center + 2] > 30, "the crate covers the center of the view");
    }

    [Fact]
    public void ModelAnimator_PlaysTheModelsClip_OnTheInstantiatedHierarchy()
    {
        var guid = ImportFixture("Khronos", "BoxAnimated.glb", out var model);
        var assets = AssetDatabase.OpenForEditing(Path.Combine(_root, "Assets"), Path.Combine(_root, "Library"), ModelImporters.WithDefaults());
        using var provider = new ModelAnimationProvider(assets, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var scene = new Scene { ModelAnimations = provider, RethrowScriptExceptions = true };
        var root = ModelInstantiation.Instantiate(scene, guid, model, "Box");
        var animator = root.GetComponent<ModelAnimator>()!;
        Assert.Equal(model.Animations[0].Name, Assert.Single(animator.ClipNames));

        var channel = model.Animations[0].Channels.First(c => c.Path == AnimationPath.Translation && c.Node != 0);
        var node = model.Nodes[channel.Node].Name;
        var animated = scene.GameObjects.First(g => g.Name == node).Transform;
        scene.Start();
        scene.Update(new Core.Timing.FrameTime(1.5f, 1.5f, 1.5, 1.5, 1));

        Assert.True(animator.IsPlaying);
        Assert.True(Mathematics.Vector3.Distance(AnimationSampling.SampleVector3(channel, 1.5f), animated.LocalPosition) < 1e-4f, "the node follows its channel");
    }

    [Fact]
    public void ModelAnimator_DrivesBlendShapeWeights_FromTheClip()
    {
        var guid = ImportFixture("Khronos", "AnimatedMorphCube.glb", out var model);
        var assets = AssetDatabase.OpenForEditing(Path.Combine(_root, "Assets"), Path.Combine(_root, "Library"), ModelImporters.WithDefaults());
        using var provider = new ModelAnimationProvider(assets, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var scene = new Scene { ModelAnimations = provider, RethrowScriptExceptions = true };
        ModelInstantiation.Instantiate(scene, guid, model, "Cube");
        var renderer = scene.FindObjectsOfType<MeshRenderer>().Single();
        Assert.Equal(model.Meshes[0].BlendShapes.Select(s => s.Name), renderer.BlendShapeNames);

        var channel = model.Animations[0].Channels.Single(c => c.Path == AnimationPath.Weights);
        var time = channel.Times[^1] * 0.37f;
        scene.Start();
        scene.Update(new Core.Timing.FrameTime(time, time, time, time, 1));

        var expected = new float[2];
        AnimationSampling.SampleWeights(channel, time, expected);
        Assert.Equal(expected[0], renderer.GetBlendShapeWeight(0), 4);
        Assert.Equal(expected[1], renderer.GetBlendShapeWeight(1), 4);
    }

    [Fact]
    public void BlendShapes_DeformTheRenderedMesh()
    {
        var guid = ImportFixture("Khronos", "AnimatedMorphCube.glb", out var model);
        string Render(float weight)
        {
            var scene = new Scene { Name = "Morph" };
            var camera = scene.CreateGameObject("Camera");
            camera.Transform.LocalPosition = new Mathematics.Vector3(0f, 0f, 4f);
            var view = camera.AddComponent<Camera>();
            (view.Projection, view.BackgroundColor) = (CameraProjection.Perspective, new Mathematics.Color(0f, 0f, 0f, 1f));
            var root = ModelInstantiation.Instantiate(scene, guid, model, "Cube");
            root.GetComponent<ModelAnimator>()!.PlayOnStart = false;
            var renderer = scene.FindObjectsOfType<MeshRenderer>().Single();
            renderer.SetBlendShapeWeight(0, weight);
            renderer.SetBlendShapeWeight(1, 0f);
            File.WriteAllText(Path.Combine(_root, "Assets", "Scenes", "First.mnvt"), new SceneSerializer(Build.EngineModuleSet.CreateRegistry()).Serialize(scene));
            File.WriteAllText(Path.Combine(_root, "Assets", "Scenes", "First.mnvt.meta"), """{ "schemaVersion": 1, "guid": "e9000000-0000-4000-8000-000000000001", "settings": {} }""");
            File.WriteAllText(Path.Combine(_root, "Test.mnvtproj"), """{ "schemaVersion": 1, "name": "Test", "startupScene": "Scenes/First.mnvt", "loadingScreen": { "enabled": false } }""");
            var screenshot = Path.Combine(_root, $"morph{weight}.png");
            using var factory = LoggerFactory.Create(_ => { });
            Assert.Equal(0, RunHidden(GameProject.Load(_root), factory, screenshot).ExitCode); // the deformed mesh is released
            return screenshot;
        }

        static int Lit(string png)
        {
            var image = Graphics.Image.Load(png);
            return Enumerable.Range(0, image.Width * image.Height).Count(i => image.Pixels[i * 4] + image.Pixels[(i * 4) + 1] + image.Pixels[(i * 4) + 2] > 30);
        }

        var rest = Lit(Render(0f));
        var morphed = Lit(Render(1f));
        Assert.True(rest > 0, "the cube is visible");
        Assert.NotEqual(rest, morphed); // the full shape covers a different area of the screen
    }

    [Theory]
    [InlineData("CesiumMan.glb")]
    [InlineData("CesiumMan.fbx")] // written by Assimp's FBX exporter from the glTF character
    public void SkinnedCharacter_RendersWithItsJoints_AndReleasesItsSkinnedMesh(string file)
    {
        if (file.EndsWith(".fbx", StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.Combine(_root, "Assets", "Models"));
            ExportFbx(Fixture("Khronos/CesiumMan.glb"), Path.Combine(_root, "Assets", "Models", file));
            File.WriteAllText(Path.Combine(_root, "Assets", "Models", file + ".meta"), """{ "schemaVersion": 1, "guid": "e8000000-0000-4000-8000-000000000002", "settings": { "scale": 100 } }"""); // the exporter writes centimeter units
        }

        var guid = file.EndsWith(".fbx", StringComparison.Ordinal) ? ImportExisting(file, out var model) : ImportFixture("Khronos", file, out model);
        var scene = new Scene { Name = "Walk" };
        var camera = scene.CreateGameObject("Camera");
        camera.Transform.LocalPosition = new Mathematics.Vector3(0f, 0.8f, 3f);
        var view = camera.AddComponent<Camera>();
        (view.Projection, view.BackgroundColor) = (CameraProjection.Perspective, new Mathematics.Color(0f, 0f, 0f, 1f));
        ModelInstantiation.Instantiate(scene, guid, model, "Cesium Man").GetComponent<ModelAnimator>()!.Speed = 0f; // the walk's first pose, the same in every run
        File.WriteAllText(Path.Combine(_root, "Assets", "Scenes", "First.mnvt"), new SceneSerializer(Build.EngineModuleSet.CreateRegistry()).Serialize(scene));
        File.WriteAllText(Path.Combine(_root, "Assets", "Scenes", "First.mnvt.meta"), $$"""{ "schemaVersion": 1, "guid": "{{Guid.NewGuid()}}", "settings": {} }""");
        File.WriteAllText(Path.Combine(_root, "Test.mnvtproj"), """{ "schemaVersion": 1, "name": "Test", "startupScene": "Scenes/First.mnvt", "loadingScreen": { "enabled": false } }""");

        using var logs = new InMemoryLoggerProvider(minimumLevel: LogLevel.Debug);
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var screenshot = Path.Combine(_root, "walk.png");
        var runtime = RunHidden(GameProject.Load(_root), factory, screenshot);
        
        Assert.Equal(0, runtime.ExitCode); // the skinned dynamic mesh is released
        var warnings = logs.Snapshot().Where(e => e.Level >= LogLevel.Warning && e.Category != "Graphics").Select(e => $"{e.Category}: {e.Message}").ToList();
        Assert.True(warnings.Count == 0, string.Join("\n", warnings));
        Assert.Contains(logs.Snapshot(), e => e.Message.Contains("1 meshes drawn", StringComparison.Ordinal) && e.Message.Contains("1 GPU-skinned", StringComparison.Ordinal));
        var image = Graphics.Image.Load(screenshot);
        Assert.Contains(Enumerable.Range(0, image.Width * image.Height), i => image.Pixels[i * 4] + image.Pixels[(i * 4) + 1] + image.Pixels[(i * 4) + 2] > 60); // the character is on screen

        // The CPU skins the same pose to the same picture.
        var cpuShot = Path.Combine(_root, "walk-cpu.png");
        Assert.Equal(0, RunHidden(GameProject.Load(_root), factory, cpuShot, gpuSkinning: false).ExitCode);
        var cpu = Graphics.Image.Load(cpuShot);
        var different = Enumerable.Range(0, image.Pixels.Length).Count(i => Math.Abs(image.Pixels[i] - cpu.Pixels[i]) > 8);
        Assert.True(different <= image.Pixels.Length / 200, $"{different} of {image.Pixels.Length} channels differ between GPU and CPU skinning");
    }

    [Fact]
    public void RemappedMaterial_ReplacesTheImportedOne_AndAnUnloadableRemapFallsBack()
    {
        var guid = ImportFixture("Khronos", "BoxAnimated.glb", out var model);
        var red = Guid.Parse("e6000000-0000-4000-8000-000000000001");
        var missing = Guid.Parse("e6000000-0000-4000-8000-000000000002");
        var materials = Path.Combine(_root, "Assets", "Materials");
        Directory.CreateDirectory(materials);
        File.WriteAllText(Path.Combine(materials, "Red.mnvtmat"), """{ "schemaVersion": 2, "shader": "builtin:standard", "blend": "Opaque", "properties": { "_BaseColor": [1, 0, 0, 1] } }""");
        File.WriteAllText(Path.Combine(materials, "Red.mnvtmat.meta"), $$"""{ "schemaVersion": 1, "guid": "{{red}}", "settings": {} }""");
        var meta = Path.Combine(_root, "Assets", "Models", "BoxAnimated.glb.meta");
        var json = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(meta));
        json["settings"]!["materials"] = new Newtonsoft.Json.Linq.JObject { [model.Materials[0].Name] = $"asset:{red}", [model.Materials[1].Name] = $"asset:{missing}" };
        File.WriteAllText(meta, json.ToString());

        VulkanGraphicsDevice device;
        try
        {
            device = VulkanGraphicsDevice.CreateHeadless(16, 16, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        }
        catch (Graphics.GraphicsInitializationException ex)
        {
            Assert.Skip($"No Vulkan 1.4 device available: {ex.Message}");
            throw;
        }

        using (device)
        {
            var assets = AssetDatabase.OpenForEditing(Path.Combine(_root, "Assets"), Path.Combine(_root, "Library"), ModelImporters.WithDefaults());
            using var resources = new ResourceCache(device, assets, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            new World3D.Rendering.World3DModule().RegisterResources(resources);
            Assert.Same(resources.GetMaterial(new AssetRef(red)), resources.GetModelMaterial(guid, 0));
            var fallback = resources.GetModelMaterial(guid, 1)!; // the remap target does not exist: the imported material draws
            Assert.Contains("#", fallback.Name, StringComparison.Ordinal);
        }
    }

    private static GameRuntime RunHidden(GameProject project, ILoggerFactory factory, string screenshot, bool gpuSkinning = true)
    {
        GameWindow window;
        try
        {
            window = new GameWindow(new GameWindowOptions { Hidden = true, Width = 64, Height = 64, VSync = false });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Assert.Skip($"No window system available: {ex.Message}");
            throw;
        }

        var runtime = new GameRuntime(project, Build.EngineModuleSet.CreateRegistry(),
            (w, l) => VulkanGraphicsDevice.Create(w.VulkanSurface, w.FramebufferWidth, w.FramebufferHeight, l, new VulkanDeviceOptions { VSync = false }),
            factory, new GameRuntimeOptions { ExitAfterFrames = 3, ScreenshotPath = screenshot, GpuSkinning = gpuSkinning, Modules = Build.EngineModuleSet.All() });
        using (window)
        {
            try
            {
                window.Run(runtime);
            }
            catch (Graphics.GraphicsInitializationException ex)
            {
                Assert.Skip($"No Vulkan 1.4 device available: {ex.Message}");
            }
        }

        return runtime;
    }
}
