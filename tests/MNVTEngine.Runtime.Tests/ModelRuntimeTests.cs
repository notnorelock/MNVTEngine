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
        Assert.All(renderers, r => Assert.NotNull(model.FindMesh(r.Mesh.SubAsset![ResourceCache.MeshSubAssetPrefix.Length..])));

        var serializer = new SceneSerializer(EngineComponents.CreateRegistry());
        var copy = serializer.Deserialize(serializer.Serialize(scene)).Scene;
        Assert.Equal(renderers.Select(r => r.Mesh.ToString()).Order().ToList(), copy.FindObjectsOfType<MeshRenderer>().Select(r => r.Mesh.ToString()).Order().ToList());
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
        File.WriteAllText(Path.Combine(_root, "Assets", "Scenes", "First.mnvt"), new SceneSerializer(EngineComponents.CreateRegistry()).Serialize(scene));
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

    private static GameRuntime RunHidden(GameProject project, ILoggerFactory factory, string screenshot)
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

        var runtime = new GameRuntime(project, EngineComponents.CreateRegistry(),
            (w, l) => VulkanGraphicsDevice.Create(w.VulkanSurface, w.FramebufferWidth, w.FramebufferHeight, l, new VulkanDeviceOptions { VSync = false }),
            factory, new GameRuntimeOptions { ExitAfterFrames = 3, ScreenshotPath = screenshot });
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
