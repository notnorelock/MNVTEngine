using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.CLI;
using MNVTEngine.Runtime;
using MNVTEngine.Versioning;

namespace MNVTEngine.Build.Tests;

/// <summary>Engine versions of real exports: a 3D game with an allocated build number, and the CLI's version output.</summary>
[Collection(GameExports.Name)]
public sealed class EngineVersionExportTests(TestBuildState state) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-version-export-").FullName;

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

    [Fact]
    public async Task Export3D_GetsTheNextBuildNumber_AndTheGameReportsItsEngineBuild()
    {
        Gpu.SkipWithoutVulkan14();
        var engine = EngineLocation.FindRoot();
        var template = ProjectTemplates.List(EngineLocation.TemplatesDirectory(engine)).Single(t => t.Id == "empty-3d");
        var directory = Path.Combine(_root, "Deep");
        ProjectTemplates.Create(template, "Deep", directory, engine);
        var counter = new BuildCounter(state.Directory);
        var before = counter.Attempts().Count;

        var result = await new GameBuilder(NullLogger.Instance).BuildAsync(directory, new BuildOptions { TestRunFrames = 10 }, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));

        // One allocation for the whole export (host, engine assemblies, crash handler, packages), recorded as succeeded.
        var attempts = counter.Attempts();
        Assert.Equal(before + 1, attempts.Count);
        var attempt = attempts[^1];
        Assert.Equal(("succeeded", "game:Deep"), (attempt.Result, attempt.Kind));

        var manifest = GamePackage.Load(Path.Combine(result.OutputDirectory!, "Deep_Data"));
        var stamp = manifest.Engine!;
        Assert.Equal((attempt.Version, attempt.BuildId), (stamp.Version, stamp.BuildId));
        Assert.Equal(attempt.Number, int.Parse(stamp.FileVersion.Split('.')[3], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(stamp.FileVersion, FileVersionInfo.GetVersionInfo(result.Executable!).FileVersion);
        Assert.Equal(stamp.FileVersion, FileVersionInfo.GetVersionInfo(Path.Combine(result.OutputDirectory!, GamePackage.ManagedFolder, "MNVTEngine.World3D.Rendering.dll")).FileVersion);

        // The exported game reports the engine build it was made with, without the repository or Git.
        var log = File.ReadAllText(Path.Combine(result.OutputDirectory!, "Logs", "game.log"));
        Assert.Contains($"MNVTEngine {stamp.Version}+", log, StringComparison.Ordinal);
        Assert.Contains($"build {stamp.BuildId:D}", log, StringComparison.Ordinal);
        Assert.Contains($"Deep (release), built with MNVTEngine {stamp.Version}, jit, win-x64", log, StringComparison.Ordinal);

        var report = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(BuildReport.PathFor(result.OutputDirectory!)));
        Assert.Equal(("3d", "vulkan", "Release"), ((string?)report["engine"]!["context"]!["profile"], (string?)report["engine"]!["context"]!["graphicsBackend"], (string?)report["engine"]!["context"]!["configuration"]));
    }

    [Fact]
    public async Task FailedBuild_IsRecordedAsFailed()
    {
        var engine = EngineLocation.FindRoot();
        var template = ProjectTemplates.List(EngineLocation.TemplatesDirectory(engine)).Single(t => t.Id == "empty-2d");
        var directory = Path.Combine(_root, "Broken");
        ProjectTemplates.Create(template, "Broken", directory, engine);
        File.WriteAllText(Path.Combine(directory, "Assets", "Scenes", "Main.mnvt"), "{ not a scene");

        var result = await new GameBuilder(NullLogger.Instance).BuildAsync(directory, new BuildOptions(), TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded);
        Assert.Equal(("failed", "game:Broken"), (new BuildCounter(state.Directory).Attempts()[^1].Result, new BuildCounter(state.Directory).Attempts()[^1].Kind));
    }

    [Fact]
    public async Task Cli_PrintsTheEngineBuild_ReadableAndAsJson()
    {
        var output = new StringWriter();
        Assert.Equal(Cli.Ok, await Cli.RunAsync(["--version"], output, NullLoggerFactory.Instance, TestContext.Current.CancellationToken));
        var text = output.ToString();
        Assert.StartsWith($"MNVTEngine {EngineVersion.Version}", text, StringComparison.Ordinal);
        Assert.Contains($"Build ID:      {EngineVersion.BuildId}", text, StringComparison.Ordinal);
        Assert.Contains($"Commit:        {EngineVersion.ShortCommit}", text, StringComparison.Ordinal);
        Assert.Contains("Built:         ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("1.0.0", text, StringComparison.Ordinal);

        output = new StringWriter();
        Assert.Equal(Cli.Ok, await Cli.RunAsync(["version", "--json"], output, NullLoggerFactory.Instance, TestContext.Current.CancellationToken));
        var json = Newtonsoft.Json.Linq.JObject.Parse(output.ToString());
        Assert.Equal((EngineVersion.Version, EngineVersion.BuildId, EngineVersion.IsDirtyBuild), ((string?)json["version"], (string?)json["buildId"], (bool?)json["source"]!["dirty"]));

        output = new StringWriter();
        Assert.Equal(Cli.Ok, await Cli.RunAsync(["version", "--verbose"], output, NullLoggerFactory.Instance, TestContext.Current.CancellationToken));
        Assert.Contains("Next build of ", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("eng/version.json: major ", output.ToString(), StringComparison.Ordinal);
    }
}
