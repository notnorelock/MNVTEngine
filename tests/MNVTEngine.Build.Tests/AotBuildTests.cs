using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Runtime;

namespace MNVTEngine.Build.Tests;

/// <summary>The ahead-of-time scripting backend: a real NativeAOT export of the basic-2d template, run on the GPU.</summary>
[Collection(GameExports.Name)]
public sealed class AotBuildTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-aot-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A lingering build server may still hold files; the temp folder is cleaned by the OS eventually.
        }
    }

    private string NewProject()
    {
        var engine = EngineLocation.FindRoot();
        var template = ProjectTemplates.List(EngineLocation.TemplatesDirectory(engine)).Single(t => t.Id == "basic-2d");
        var directory = Path.Combine(_root, "AotTest");
        ProjectTemplates.Create(template, "AotTest", directory, engine);
        return directory;
    }

    [Fact]
    public async Task AotBuild_IsOneNativeExecutable_WithTheGamesCode_AndRuns()
    {
        var result = await new GameBuilder(NullLogger.Instance).BuildAsync(NewProject(), new BuildOptions { ScriptingBackend = "aot", TestRunFrames = 20 }, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        var output = result.OutputDirectory!;

        // No IL host, no .NET runtime, no managed assemblies: the engine and the game's C# are in the executable.
        Assert.True(File.Exists(Path.Combine(output, "AotTest.exe")));
        Assert.True(File.Exists(Path.Combine(output, "MNVTCrashHandler.exe"))); // compiled ahead of time too: no runtime to run on
        Assert.False(File.Exists(Path.Combine(output, "MNVTCrashHandler.dll")));
        Assert.False(File.Exists(Path.Combine(output, "AotTest.dll")));
        Assert.False(Directory.Exists(Path.Combine(output, "Dependencies", "Runtime")));
        Assert.Empty(Directory.Exists(Path.Combine(output, "Dependencies", "Managed")) ? Directory.GetFiles(Path.Combine(output, "Dependencies", "Managed")) : []);
        Assert.Empty(Directory.GetFiles(output, "*.pdb", SearchOption.AllDirectories)); // Release
        var package = GamePackage.Load(Path.Combine(output, "AotTest_Data"));
        Assert.Equal(("aot", (string?)null), (package.ScriptingBackend, package.ScriptsAssembly));

        // The C# component ran (registered by the generated GameComponents.g.cs, not loaded from the archive).
        var log = File.ReadAllText(Path.Combine(output, "Logs", "game.log"));
        Assert.Contains("[script] Move to the goal with WASD", log, StringComparison.Ordinal);
        var report = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(BuildReport.PathFor(output)));
        Assert.Equal("aot", (string?)report["scriptingBackend"]!["id"]);
        Assert.Contains("Not IL2CPP", (string?)report["scriptingBackend"]!["description"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Il2cpp_IsRefused_WithTheReason()
    {
        var result = await new GameBuilder(NullLogger.Instance).BuildAsync(NewProject(), new BuildOptions { ScriptingBackend = "il2cpp" }, TestContext.Current.CancellationToken);
        var error = Assert.Single(result.Diagnostics, d => d.Code == "MNVB111");
        Assert.Contains("no il2cpp backend", error.Message, StringComparison.Ordinal);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Generator_RegistersGameComponents_AndInstantiatesSerializedCollectionsOfStructs()
    {
        var source = AotBootstrap.Source([typeof(Tilemap)], []);
        Assert.Contains("registry.Register<global::MNVTEngine.Tilemap>();", source, StringComparison.Ordinal);
        Assert.Contains("new global::System.Collections.ObjectModel.ReadOnlyCollection<global::MNVTEngine.TileCell>(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Activator", source, StringComparison.Ordinal);
        Assert.Equal("global::System.Collections.Generic.Dictionary<global::System.String, global::System.Int32[]>", AotBootstrap.Name(typeof(Dictionary<string, int[]>)));
    }
}
