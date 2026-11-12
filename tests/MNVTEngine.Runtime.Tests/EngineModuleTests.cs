using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Modules;
using MNVTEngine.Scenes;

namespace MNVTEngine.Runtime.Tests;

/// <summary>The catalog's component lists, the runtime module implementations and project resolution agree.</summary>
public sealed class EngineModuleTests : IDisposable
{
    // Until the world modules get their own assemblies, their components are registered with core's.
    private static readonly string[] HostedInCore = ["core", "world.2d", "world.3d"];

    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-modules-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static HashSet<string> Ids(ComponentRegistry registry) => [.. registry.Types.Keys];

    [Fact]
    public void EachModuleImplementation_RegistersExactlyItsCatalogComponents()
    {
        foreach (var module in ModuleCatalog.Builtin.Modules)
        {
            if (EngineModules.Create(module.Id) is not { } implementation)
            {
                Assert.True(HostedInCore.Contains(module.Id) || module.Components.Count == 0, $"'{module.Id}' declares components but has no implementation.");
                continue;
            }

            Assert.Equal(module.Id, implementation.Id);
            var registry = new ComponentRegistry();
            implementation.Register(registry);
            Assert.Equal(module.Components.Order(StringComparer.Ordinal), Ids(registry).Order(StringComparer.Ordinal));
        }

        var core = HostedInCore.SelectMany(id => ModuleCatalog.Builtin[id].Components).Order(StringComparer.Ordinal);
        Assert.Equal(core, Ids(ComponentRegistry.CreateDefault()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryBuiltInComponent_BelongsToExactlyOneModule()
    {
        var all = Ids(EngineComponents.CreateRegistry());
        Assert.Equal(ModuleCatalog.Builtin.Modules.SelectMany(m => m.Components).Order(StringComparer.Ordinal), all.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ProjectModules_FollowTheProfileAndTheProjectsSwitches()
    {
        WriteProject("2d", """{ "audio": false, "scripting": false }""", "MNVTEngine.SpriteRenderer", "MNVTEngine.AudioSource");
        var (resolution, diagnostics) = ProjectModules.Validate(GameProject.Load(_root));
        Assert.True(resolution.Succeeded);
        Assert.DoesNotContain("audio", resolution.Modules.Select(m => m.Id));
        Assert.Equal(ModuleDiagnostic.ComponentOfDisabledModule, Assert.Single(diagnostics, d => d.Severity == ModuleSeverity.Error).Code);

        // The game host registers only the enabled modules' components.
        var registry = ComponentRegistry.CreateDefault();
        var host = new ModuleHost(EngineModules.For(resolution));
        host.Start(registry);
        Assert.False(registry.TryGetType("MNVTEngine.AudioSource", out _));
        Assert.True(registry.TryGetType("MNVTEngine.Rigidbody2D", out _));
        Assert.False(registry.TryGetType("MNVTEngine.Rigidbody", out _));
        host.Shutdown();
    }

    [Fact]
    public void TheGame_DoesNotStart_WhenItsModulesDoNotResolve()
    {
        WriteProject("2d", """{ "physics.3d": true }""", "MNVTEngine.Camera");
        var exit = GameApplication.Run([_root], (_, _) => throw new InvalidOperationException("no window expected"), NullLoggerFactory.Instance);
        Assert.Equal(2, exit);
    }

    [Fact]
    public void TheGame_DoesNotStart_WhenItsScenesUseADisabledModule()
    {
        // Loading would keep the component as a MissingComponent and drop it silently.
        WriteProject("3d", """{ "terrain": false }""", "MNVTEngine.Camera", "MNVTEngine.WorldTerrain");
        var exit = GameApplication.Run([_root], (_, _) => throw new InvalidOperationException("no window expected"), NullLoggerFactory.Instance);
        Assert.Equal(2, exit);
    }

    private void WriteProject(string profile, string modules, params string[] components)
    {
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "Scenes"));
        File.WriteAllText(Path.Combine(_root, "Game.mnvtproj"),
            $$"""{ "schemaVersion": 2, "name": "Game", "profile": "{{profile}}", "modules": {{modules}}, "startupScene": "Scenes/Main.mnvt" }""");
        File.WriteAllText(Path.Combine(_root, "Assets", "Scenes", "Main.mnvt"),
            "{ \"schemaVersion\": 3, \"name\": \"Main\", \"gameObjects\": [ { \"id\": \"" + Guid.NewGuid() + "\", \"name\": \"O\", \"components\": ["
            + string.Join(", ", components.Select(c => $"{{ \"type\": \"{c}\", \"data\": {{}} }}")) + "] } ] }");
    }
}
