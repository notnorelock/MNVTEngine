using System.Runtime.CompilerServices;
using MNVTEngine.GameAssemblies;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;

namespace MNVTEngine.Build.Tests;

/// <summary>Builds a small game in a temp project that references the engine assemblies from this test's output folder.</summary>
public sealed class GameCodeTests : IDisposable
{
    private const string V1 = """
        using MNVTEngine;
        using MNVTEngine.Mathematics;

        namespace TestGame;

        [ComponentId("TestGame.Mover")]
        public sealed class Mover : Component
        {
            public float Speed = 1f;
            public GameObject? Target;

            public override void Update() => Transform.Position += new Vector2(Speed * Time.DeltaTime, 0f);
        }
        """;

    private const string V2 = """
        using MNVTEngine;
        using MNVTEngine.Mathematics;

        namespace TestGame;

        [ComponentId("TestGame.Mover")]
        public sealed class Mover : Component
        {
            public float Speed = 1f;
            public GameObject? Target;
            public int Version = 2;

            public override void Update() => Transform.Position += new Vector2(Speed * Time.DeltaTime * 2f, 0f);
        }

        public sealed class Helper : Component;
        """;

    private static readonly string[] EngineAssemblies = ["Core", "Mathematics", "ECS", "Input", "Serialization", "Scenes"];

    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-gamecode-").FullName;

    public GameCodeTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "Scenes"));
        Directory.CreateDirectory(Path.Combine(_root, "Scripts"));
        File.WriteAllText(Path.Combine(_root, "Assets", "Scenes", "Main.mnvt"), """{ "schemaVersion": 2, "gameObjects": [] }""");
        File.WriteAllText(Path.Combine(_root, "Game.mnvtproj"), """{ "schemaVersion": 1, "name": "T", "startupScene": "Scenes/Main.mnvt", "scripts": { "project": "Scripts/TestGame.csproj" } }""");

        var engine = AppContext.BaseDirectory;
        var references = string.Join("\n", EngineAssemblies
            .Select(n => $"""    <Reference Include="{Path.Combine(engine, $"MNVTEngine.{n}.dll")}" Private="false" />"""));
        File.WriteAllText(Path.Combine(_root, "Scripts", "TestGame.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net11.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
            {references}
              </ItemGroup>
            </Project>
            """);
    }

    private ScriptProject Project => ScriptProject.For(GameProject.Load(_root))!;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A lingering build server may still hold obj/ files; the temp folder is cleaned by the OS eventually.
        }
    }

    private void WriteSource(string code)
    {
        File.WriteAllText(Path.Combine(_root, "Scripts", "Game.cs"), code);
        File.SetLastWriteTimeUtc(Path.Combine(_root, "Scripts", "Game.cs"), DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 1000)));
    }

    [Fact]
    public async Task Compile_Load_Reload_KeepsSceneData_AndUnloadsOldCode()
    {
        WriteSource(V1);
        var project = Project;
        Assert.False(project.IsBuildUpToDate());
        var build = await ScriptCompiler.BuildAsync(project, TestContext.Current.CancellationToken);
        Assert.True(build.Succeeded, string.Join("\n", build.Diagnostics));
        Assert.True(project.IsBuildUpToDate());

        var host = new GameAssemblyHost();
        var registry = ComponentRegistry.CreateDefault();
        var serializer = new SceneSerializer(registry);
        var (json, generation) = LoadAndAuthorScene(host, registry, serializer, build.AssemblyPath!);
        GameCode? firstGeneration = generation;
        generation = null!; // async locals live in the state machine; drop every strong reference before checking unload

        WriteSource(V2);
        Assert.False(project.IsBuildUpToDate());
        var rebuild = await ScriptCompiler.BuildAsync(project, TestContext.Current.CancellationToken);
        Assert.True(rebuild.Succeeded, string.Join("\n", rebuild.Diagnostics));

        var (unloaded, playerX) = ReloadAndCheck(host, registry, serializer, json, rebuild.AssemblyPath!, firstGeneration);
        firstGeneration = null;
        Assert.Equal(2f * 3f * 0.5f, playerX); // V2 behaviour: Speed (3, kept from the scene) * dt * 2
        Assert.True(GameAssemblyHost.IsCollected(unloaded), "The previous game code generation was not collected; something still references its types.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (string Json, GameCode Generation) LoadAndAuthorScene(GameAssemblyHost host, ComponentRegistry registry, SceneSerializer serializer, string assemblyPath)
    {
        var loaded = host.Load(assemblyPath);
        Assert.True(loaded.Succeeded, string.Join("\n", loaded.Diagnostics));
        Assert.Empty(GameAssemblyHost.Register(registry, null, loaded.Code));
        Assert.True(registry.TryGetType("TestGame.Mover", out var moverType));
        Assert.NotSame(typeof(Component).Assembly, moverType.Assembly);
        Assert.Same(typeof(Component), moverType.BaseType); // engine types are shared with the host

        var scene = new Scene("Main");
        var target = scene.CreateGameObject("Target");
        var player = scene.CreateGameObject("Player");
        var mover = player.AddComponent(moverType);
        moverType.GetField("Speed")!.SetValue(mover, 3f);
        moverType.GetField("Target")!.SetValue(mover, target);
        return (serializer.Serialize(scene), loaded.Code!);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Unloaded, float PlayerX) ReloadAndCheck(GameAssemblyHost host, ComponentRegistry registry, SceneSerializer serializer, string json, string assemblyPath, GameCode previous)
    {
        var loaded = host.Load(assemblyPath);
        Assert.True(loaded.Succeeded, string.Join("\n", loaded.Diagnostics));
        Assert.Empty(GameAssemblyHost.Register(registry, previous, loaded.Code));
        Assert.True(registry.TryGetType("TestGame.Helper", out _));

        var scene = serializer.Deserialize(json).Scene;
        var player = scene.Find("Player")!;
        var mover = player.Components.Single(c => c.GetType().Name == "Mover");
        Assert.Same(loaded.Code!.MainAssembly, mover.GetType().Assembly);
        Assert.Equal(3f, (float)mover.GetType().GetField("Speed")!.GetValue(mover)!);
        Assert.Same(scene.Find("Target"), mover.GetType().GetField("Target")!.GetValue(mover));
        Assert.Equal(2, (int)mover.GetType().GetField("Version")!.GetValue(mover)!);

        scene.RethrowScriptExceptions = true;
        scene.Start();
        scene.Update(new Core.Timing.FrameTime(0.5f, 0.5f, 0.5, 0.5, 1));
        return (GameAssemblyHost.Unload(previous), player.Transform.Position.X);
    }

    [Fact]
    public async Task CompileErrors_AreReportedWithLocation()
    {
        WriteSource("""
            namespace TestGame;
            public sealed class Broken : MNVTEngine.Component
            {
                public int Value = "text";
            }
            """);
        var build = await ScriptCompiler.BuildAsync(Project, TestContext.Current.CancellationToken);

        Assert.False(build.Succeeded);
        var error = Assert.Single(build.Errors);
        Assert.Equal("CS0029", error.Code);
        Assert.EndsWith("Game.cs", error.File, StringComparison.Ordinal);
        Assert.Equal(4, error.Line);
        Assert.False(Project.IsBuildUpToDate());
    }

    [Fact]
    public void LoadFailures_AreReported()
    {
        var bogus = Path.Combine(_root, "Bogus.dll");
        File.WriteAllText(bogus, "not an assembly");
        var result = new GameAssemblyHost().Load(bogus);
        Assert.False(result.Succeeded);
        Assert.Equal("LOAD001", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void ParseDiagnostics_HandlesMsBuildFormats()
    {
        var output = """
            C:\g\A.cs(12,5): error CS1002: ; expected [C:\g\G.csproj]
            C:\g\A.cs(12,5): error CS1002: ; expected [C:\g\G.csproj]
            C:\g\B.cs(3,1): warning CS0168: The variable 'x' is declared but never used [C:\g\G.csproj]
            C:\g\G.csproj : error MSB4025: The project file could not be loaded.
            Build succeeded.
            """;
        var diagnostics = ScriptCompiler.ParseDiagnostics(output);
        Assert.Equal(3, diagnostics.Count);
        Assert.Equal(new ScriptDiagnostic(ScriptDiagnosticSeverity.Error, "CS1002", "; expected", @"C:\g\A.cs", 12, 5), diagnostics[0]);
        Assert.Equal(ScriptDiagnosticSeverity.Warning, diagnostics[1].Severity);
        Assert.Equal("MSB4025", diagnostics[2].Code);
        Assert.Equal(0, diagnostics[2].Line);
    }

    [Fact]
    public void CacheCleaner_FindsNewtonsoftStores() =>
        Assert.True(ReflectionCacheCleaner.KnownStoreCount >= 10, "Newtonsoft.Json internals changed; unloading game code will leak. Update ReflectionCacheCleaner.");

    [Fact]
    public void SharedAssemblies_AlwaysComeFromTheHost()
    {
        Assert.True(GameAssemblyLoadContext.IsSharedWithHost(typeof(Component).Assembly.GetName()));
        Assert.True(GameAssemblyLoadContext.IsSharedWithHost(new System.Reflection.AssemblyName("System.Runtime")));
        Assert.True(GameAssemblyLoadContext.IsSharedWithHost(new System.Reflection.AssemblyName("Newtonsoft.Json")));
        Assert.False(GameAssemblyLoadContext.IsSharedWithHost(new System.Reflection.AssemblyName("TestGame")));
    }

    [Fact]
    public void ScriptsProjectPath_MustStayInsideTheProject()
    {
        File.WriteAllText(Path.Combine(_root, "Game.mnvtproj"), """{ "schemaVersion": 1, "name": "T", "startupScene": "Scenes/Main.mnvt", "scripts": { "project": "../evil.csproj" } }""");
        Assert.Throws<Serialization.SerializationException>(() => GameProject.Load(_root));
    }
}
