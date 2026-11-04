using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using MNVTEngine.Assets;
using MNVTEngine.Core.Logging;
using MNVTEngine.Graphics.Vulkan;
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
            (w, l) => VulkanGraphicsDevice.Create(w.VulkanSurface, w.FramebufferWidth, w.FramebufferHeight, l, new VulkanDeviceOptions { VSync = false }),
            factory,
            new GameRuntimeOptions { ExitAfterFrames = 10 });
        using (window)
        {
            try
            {
                window.Run(runtime);
            }
            catch (MNVTEngine.Graphics.GraphicsInitializationException ex)
            {
                Assert.Skip($"No Vulkan 1.4 device available: {ex.Message}");
            }
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

    [Fact]
    public void Lighting_AndMaterials_RenderAndReleaseTheirResources()
    {
        string Meta(Guid guid) => $$"""{ "schemaVersion": 1, "guid": "{{guid}}", "settings": { "filter": "point" } }""";
        var white = Guid.NewGuid();
        var red = Guid.NewGuid();
        var broken = Guid.NewGuid();
        var redShader = Guid.NewGuid();
        var brokenShader = Guid.NewGuid();
        using (var png = File.Create(Path.Combine(_root, "Assets", "white.png")))
        {
            new StbImageWriteSharp.ImageWriter().WritePng(Enumerable.Repeat((byte)255, 4 * 4 * 4).ToArray(), 4, 4, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, png);
        }

        File.WriteAllText(Path.Combine(_root, "Assets", "white.png.meta"), Meta(white));
        File.WriteAllText(Path.Combine(_root, "Assets", "red.frag"), """
            #version 450 core
            in vec2 vTexCoord;
            in vec4 vColor;
            layout(binding = 0) uniform sampler2D uTexture;
            uniform vec4 uColor;
            uniform float uTime;
            out vec4 fragColor;
            void main() { fragColor = uColor * texture(uTexture, vTexCoord).a + vec4(0.0 * uTime); }
            """);
        File.WriteAllText(Path.Combine(_root, "Assets", "red.frag.meta"), Meta(redShader));
        File.WriteAllText(Path.Combine(_root, "Assets", "broken.frag"), "#version 450 core\nvoid main() { this is not glsl }");
        File.WriteAllText(Path.Combine(_root, "Assets", "broken.frag.meta"), Meta(brokenShader));
        File.WriteAllText(Path.Combine(_root, "Assets", "Red.mnvtmat"), $$"""{ "schemaVersion": 1, "shader": "asset:{{redShader}}", "uniforms": { "uColor": [1, 0, 0, 1] } }""");
        File.WriteAllText(Path.Combine(_root, "Assets", "Red.mnvtmat.meta"), Meta(red));
        File.WriteAllText(Path.Combine(_root, "Assets", "Broken.mnvtmat"), $$"""{ "schemaVersion": 1, "shader": "asset:{{brokenShader}}" }""");
        File.WriteAllText(Path.Combine(_root, "Assets", "Broken.mnvtmat.meta"), Meta(broken));

        string Sprite(float x, float y, float w, float h, Guid? material = null) =>
            $$"""{ "type": "MNVTEngine.SpriteRenderer", "data": { "texture": "asset:{{white}}", "size": { "x": {{w}}, "y": {{h}} }{{(material is { } m ? $", \"material\": \"asset:{m}\"" : "")}} } }""";
        string Object(string name, float x, float y, string component) =>
            $$"""{ "id": "{{Guid.NewGuid()}}", "name": "{{name}}", "transform": { "localPosition": { "x": {{x}}, "y": {{y}} } }, "components": [ {{component}} ] }""";
        File.WriteAllText(Path.Combine(_root, "Assets", "Scenes", "First.mnvt"), $$"""
            { "schemaVersion": 2, "name": "Lit", "gameObjects": [
              {{Object("Camera", 0, 0, """{ "type": "MNVTEngine.Camera", "data": { "orthographicSize": 5.0 } }""")}},
              {{Object("Floor", 0, 0, Sprite(0, 0, 20, 20))}},
              {{Object("Red", 0, 4, Sprite(0, 4, 2, 1, red).Replace("\"size\"", "\"sortingOrder\": 5, \"size\""))}},
              {{Object("Broken", 0, -4, Sprite(0, -4, 2, 1, broken).Replace("\"size\"", "\"sortingOrder\": 5, \"size\""))}},
              {{Object("Ambient", 0, 0, """{ "type": "MNVTEngine.GlobalLight2D", "data": { "intensity": 0.2 } }""")}},
              {{Object("Lamp", -3, 0, """{ "type": "MNVTEngine.Light2D", "data": { "radius": 3.0 } }""")}} ] }
            """);
        var screenshot = Path.Combine(_root, "lit.png");

        var project = GameProject.Load(WriteProject());
        using var logs = new InMemoryLoggerProvider(minimumLevel: LogLevel.Debug);
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var runtime = RunHidden(project, EngineComponents.CreateRegistry(), factory, frames: 3, screenshot);

        Assert.Equal(0, runtime.ExitCode); // no GPU resources leaked
        Assert.Single(logs.Snapshot(), e => e.Level >= LogLevel.Error && e.Message.Contains("Broken.mnvtmat", StringComparison.Ordinal));
        var image = Graphics.Image.Load(screenshot);
        var unit = image.Height / 10f; // orthographic size 5: the view is 10 units tall (the window may be wider than requested)
        (int X, int Y) At(float x, float y) => ((int)((image.Width / 2f) + (x * unit)), (int)((image.Height / 2f) - (y * unit)));
        byte Channel((int X, int Y) p, int c) => image.Pixels[(((p.Y * image.Width) + p.X) * 4) + c];

        Assert.InRange(Channel(At(-3, 0), 0), 230, 255);   // under the lamp
        Assert.InRange(Channel(At(4.5f, 0), 0), 40, 62);   // ambient 0.2
        Assert.InRange(Channel(At(0, 4), 1), 0, 3);        // red material: no green...
        Assert.InRange(Channel(At(0, 4), 0), 40, 62);      // ...lit by ambient only
        Assert.InRange(Channel(At(0, -4), 1), 40, 62);     // broken material falls back to the white default
    }

    [Fact]
    public void Profiler_RecordsRealFrames_WithRendererCounters()
    {
        WriteScene("First", """{ "type": "MNVTEngine.SpriteRenderer", "data": {} }""");
        var profiler = new Core.Diagnostics.FrameProfiler();
        Core.Diagnostics.Profiler.Current = profiler;
        try
        {
            RunHidden(GameProject.Load(WriteProject()), EngineComponents.CreateRegistry(), NullLoggerFactory.Instance, frames: 5, profiler: profiler);
        }
        finally
        {
            Core.Diagnostics.Profiler.Current = null;
        }

        Assert.InRange(profiler.Frames.Count, 4, 6);
        var paths = profiler.Summarize().Samples.Select(s => s.Path).ToList();
        Assert.Contains("Render/Render.Sprites", paths);
        Assert.Contains("Update/Scripts.Update", paths);
        Assert.Contains("Draw calls", profiler.LastFrame!.Counters.Keys);
    }

    private static GameRuntime RunHidden(GameProject project, ComponentRegistry registry, ILoggerFactory factory, int frames, string? screenshot = null, Core.Diagnostics.FrameProfiler? profiler = null)
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
            (w, l) => VulkanGraphicsDevice.Create(w.VulkanSurface, w.FramebufferWidth, w.FramebufferHeight, l, new VulkanDeviceOptions { VSync = false }),
            factory, new GameRuntimeOptions { ExitAfterFrames = frames, ScreenshotPath = screenshot, Profiler = profiler });
        using (window)
        {
            try
            {
                window.Run(runtime);
            }
            catch (MNVTEngine.Graphics.GraphicsInitializationException ex)
            {
                Assert.Skip($"No Vulkan 1.4 device available: {ex.Message}");
            }
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
