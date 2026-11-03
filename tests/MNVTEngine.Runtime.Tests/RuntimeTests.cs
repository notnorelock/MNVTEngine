using Microsoft.Extensions.Logging;
using MNVTEngine.Assets;
using MNVTEngine.Core.Logging;
using MNVTEngine.Graphics.OpenGL;
using MNVTEngine.Platform;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;

namespace MNVTEngine.Runtime.Tests;

public sealed class RuntimeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-runtime-").FullName;

    public RuntimeTests() => Directory.CreateDirectory(Path.Combine(_root, "Assets", "Scenes"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string WriteProject(string startupScene = "Scenes/First.mnvt", string extra = "")
    {
        var path = Path.Combine(_root, "Test.mnvtproj");
        File.WriteAllText(path, $$"""{ "schemaVersion": 1, "name": "Test", "startupScene": "{{startupScene}}" {{extra}} }""");
        return path;
    }

    private void WriteScene(string name, string componentsJson) =>
        File.WriteAllText(Path.Combine(_root, "Assets", "Scenes", name + ".mnvt"), $$"""
            { "schemaVersion": 1, "name": "{{name}}", "gameObjects": [
              { "id": "{{Guid.NewGuid()}}", "name": "Camera", "components": [ { "type": "MNVTEngine.Camera", "data": {} } ] },
              { "id": "{{Guid.NewGuid()}}", "name": "Logic", "components": [ {{componentsJson}} ] } ] }
            """);

    [Fact]
    public void Project_LoadsFromFileOrDirectory()
    {
        WriteScene("First", "");
        var file = WriteProject();
        Assert.Equal("Scenes/First.mnvt", GameProject.Load(file).StartupScene);
        Assert.Equal(_root, GameProject.Load(_root).RootDirectory);
    }

    [Theory]
    [InlineData("Scenes/Missing.mnvt", "", "not found")]
    [InlineData("../outside.mnvt", "", "leaves the Assets folder")]
    [InlineData("Scenes/First.mnvt", """, "fixedTimestep": 0""", "fixedTimestep")]
    [InlineData("Scenes/First.mnvt", """, "unknownSetting": 1""", "unknownSetting")]
    public void Project_ValidationFailures(string startup, string extra, string expected)
    {
        WriteScene("First", "");
        WriteProject(startup, extra);
        var ex = Assert.Throws<SerializationException>(() => GameProject.Load(_root));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("Textures/../../secret.txt")]
    [InlineData("")]
    public void AssetPaths_RejectEscapes(string path) =>
        Assert.Throws<ArgumentException>(() => AssetPaths.Resolve(Path.Combine(_root, "Assets"), path));

    [Fact]
    public void AssetPaths_RejectRooted() =>
        Assert.Throws<ArgumentException>(() => AssetPaths.Resolve(Path.Combine(_root, "Assets"), Path.Combine(_root, "x.png")));

    [Fact]
    public void AssetPaths_ResolveInside() =>
        Assert.Equal(Path.Combine(_root, "Assets", "Textures", "a.png"), AssetPaths.Resolve(Path.Combine(_root, "Assets"), "Textures/./a.png"));

    [Fact]
    public void Runtime_RunsStandaloneAndSwitchesScenes()
    {
        WriteScene("First", """{ "type": "Test.Switcher", "data": { "target": "Scenes/Second.mnvt" } }""");
        WriteScene("Second", """{ "type": "Test.Marker", "data": {} }, { "type": "MNVTEngine.SpriteRenderer", "data": { "texture": "Textures/missing.png" } }""");
        var project = GameProject.Load(WriteProject());
        var registry = ComponentRegistry.CreateDefault().Register<Switcher>().Register<Marker>();
        using var logs = new InMemoryLoggerProvider(minimumLevel: LogLevel.Debug);
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));

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

        var runtime = new GameRuntime(
            project,
            registry,
            (w, l) => GLGraphicsDevice.Create(w.GraphicsContext, w.FramebufferWidth, w.FramebufferHeight, l),
            factory,
            new GameRuntimeOptions { ExitAfterFrames = 10 });
        using (window)
        {
            window.Run(runtime);
        }

        Assert.Equal("Second", runtime.CurrentScene!.Name);
        Assert.Equal(0, runtime.ExitCode);
        var messages = logs.Snapshot();
        Assert.Contains(messages, e => e.Message.Contains("Scene 'Second' running", StringComparison.Ordinal));
        // Schema 1 scene: the unknown texture path is cleared during migration, with a warning.
        Assert.Contains(messages, e => e.Level == LogLevel.Warning && e.Message.Contains("Textures/missing.png", StringComparison.Ordinal));
        Assert.True(Marker.Updates > 0);
    }

    [Fact]
    public void Runtime_InstantiatesPrefabsFromReadOnlyAssets()
    {
        var prefabGuid = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_root, "Assets", "Bullet.mnvtprefab"), $$"""
            { "schemaVersion": 2, "name": "Bullet", "gameObjects": [ { "id": "{{Guid.NewGuid()}}", "name": "Bullet", "parent": null,
              "components": [ { "type": "Test.Marker", "data": {} } ] } ] }
            """);
        File.WriteAllText(Path.Combine(_root, "Assets", "Bullet.mnvtprefab.meta"), $$"""{ "schemaVersion": 1, "guid": "{{prefabGuid}}", "settings": {} }""");
        WriteScene("First", $$"""{ "type": "Test.Shooter", "data": { "bullet": "asset:{{prefabGuid}}" } }""");
        File.WriteAllText(Path.Combine(_root, "Assets", "Scenes", "First.mnvt.meta"), $$"""{ "schemaVersion": 1, "guid": "{{Guid.NewGuid()}}", "settings": {} }""");

        var project = GameProject.Load(WriteProject());
        var registry = ComponentRegistry.CreateDefault().Register<Shooter>().Register<Marker>();
        using var logs = new InMemoryLoggerProvider(minimumLevel: LogLevel.Debug);
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var runtime = RunHidden(project, registry, factory, frames: 6);

        Assert.Equal(3, Shooter.BulletsSeen); // counted while running; Unload destroys everything afterwards
        Assert.NotNull(runtime.CurrentScene);
        Assert.DoesNotContain(logs.Snapshot(), e => e.Level >= LogLevel.Error);
        Assert.False(File.Exists(Path.Combine(_root, "Assets", "Textures.meta")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Library")), "Read-only runtime must not write a Library folder.");
    }

    private static GameRuntime RunHidden(GameProject project, ComponentRegistry registry, ILoggerFactory factory, int frames)
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

        var runtime = new GameRuntime(project, registry,
            (w, l) => GLGraphicsDevice.Create(w.GraphicsContext, w.FramebufferWidth, w.FramebufferHeight, l),
            factory, new GameRuntimeOptions { ExitAfterFrames = frames });
        using (window)
        {
            window.Run(runtime);
        }

        return runtime;
    }

    [ComponentId("Test.Shooter")]
    public sealed class Shooter : Component
    {
        [AssetType(Serialization.AssetTypes.Prefab)]
        public AssetRef Bullet;
        private int _shots;

        public static int BulletsSeen { get; private set; }

        public override void Update()
        {
            if (_shots++ < 3)
            {
                Prefab.Instantiate(Bullet, new Mathematics.Vector2(_shots, 0f));
            }
        }

        public override void LateUpdate() =>
            BulletsSeen = GameObject.Scene.GameObjects.Count(g => g.Name == "Bullet" && g.GetComponent<Marker>() is not null);
    }

    [ComponentId("Test.Switcher")]
    public sealed class Switcher : Component
    {
        public string Target = "";
        private int _frames;

        public override void Update()
        {
            if (++_frames == 3)
            {
                SceneManager.LoadScene(Target);
            }
        }
    }

    [ComponentId("Test.Marker")]
    public sealed class Marker : Component
    {
        public static int Updates { get; private set; }

        public override void Update() => Updates++;
    }
}
