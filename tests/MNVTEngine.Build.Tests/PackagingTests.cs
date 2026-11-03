using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Runtime;

namespace MNVTEngine.Build.Tests;

/// <summary>Builds the basic-2d template once with the real pipeline (dotnet publish) and checks the exported game.</summary>
public sealed class PackagedGameFixture : IAsyncLifetime
{
    public string Root { get; } = Directory.CreateTempSubdirectory("mnvt-package-").FullName;

    public string ProjectDirectory => Path.Combine(Root, "PackTest");

    public BuildResult First { get; private set; } = null!;

    public string FirstManifest { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        var engine = EngineLocation.FindRoot();
        var template = ProjectTemplates.List(EngineLocation.TemplatesDirectory(engine)).Single(t => t.Id == "basic-2d");
        ProjectTemplates.Create(template, "PackTest", ProjectDirectory, engine);
        First = await Build();
        Assert.True(First.Succeeded, string.Join("\n", First.Diagnostics));
        FirstManifest = File.ReadAllText(Path.Combine(First.OutputDirectory!, GamePackage.ContentFolder, GamePackage.ManifestFile));
    }

    public Task<BuildResult> Build(BuildOptions? options = null) =>
        new GameBuilder(NullLogger.Instance).BuildAsync(ProjectDirectory, options ?? new BuildOptions(), TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A lingering build server may still hold files; the temp folder is cleaned by the OS eventually.
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class PackagingTests(PackagedGameFixture game) : IClassFixture<PackagedGameFixture>
{
    private string Output => game.First.OutputDirectory!;

    [Fact]
    public async Task Build_ProducesASelfContainedGame_ThatRunsWithoutTheSdk()
    {
        Assert.Equal(Path.Combine(game.ProjectDirectory, "Builds", "release"), Output);
        Assert.True(File.Exists(Path.Combine(Output, "PackTest.exe")));
        Assert.True(File.Exists(Path.Combine(Output, "glfw3.dll")));
        Assert.True(File.Exists(Path.Combine(Output, "coreclr.dll"))); // the .NET runtime ships with the game
        Assert.True(File.Exists(Path.Combine(Output, "Content", "Scripts", "PackTest.Scripts.dll")));
        Assert.False(File.Exists(Path.Combine(Output, "Content", "Scripts", "MNVTEngine.Scenes.dll"))); // host copy is used
        Assert.Single(Directory.GetFiles(Path.Combine(Output, "Content", "Bytecode")));
        Assert.Empty(Directory.GetFiles(Output, "*.pdb")); // release builds ship no symbols
        Assert.Empty(BuildValidator.Validate(Output));

        var run = await BuildValidator.RunAsync(Output, 20, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        Assert.Empty(run);
        var log = File.ReadAllText(Path.Combine(Output, "Logs", "game.log"));
        Assert.Contains("[script] Move to the goal with WASD", log, StringComparison.Ordinal); // C# component, scene and bytecode all loaded
    }

    [Fact]
    public async Task Rebuild_IsByteForByteIdentical()
    {
        var again = await game.Build();
        Assert.True(again.Succeeded, string.Join("\n", again.Diagnostics));
        Assert.Equal(game.FirstManifest, File.ReadAllText(Path.Combine(Output, GamePackage.ContentFolder, GamePackage.ManifestFile)));
    }

    [Fact]
    public async Task FailedBuild_KeepsThePreviousGame_AndReportsTheScriptLine()
    {
        var script = Path.Combine(game.ProjectDirectory, "Assets", "Missions", "ReachGoal.mnvts");
        var original = File.ReadAllText(script);
        var exeTime = File.GetLastWriteTimeUtc(Path.Combine(Output, "PackTest.exe"));
        try
        {
            File.WriteAllText(script, original.Replace("objective.complete()", "objective.complete(", StringComparison.Ordinal));
            var failed = await game.Build();
            Assert.False(failed.Succeeded);
            var error = failed.Diagnostics.First(d => d.Severity == BuildSeverity.Error);
            Assert.Equal(("Missions/ReachGoal.mnvts", 13, "MNV2000"), (error.File, error.Line, error.Code));
        }
        finally
        {
            File.WriteAllText(script, original);
        }

        Assert.Equal(exeTime, File.GetLastWriteTimeUtc(Path.Combine(Output, "PackTest.exe")));
        Assert.Empty(BuildValidator.Validate(Output));
    }

    [Fact]
    public async Task Build_RefusesToOverwriteAFolderThatIsNotABuild()
    {
        var foreign = Path.Combine(game.Root, "Documents");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "thesis.txt"), "important");

        var result = await game.Build(new BuildOptions { OutputDirectory = foreign });
        Assert.Contains(result.Diagnostics, d => d.Code == "MNVB104");
        Assert.Equal("important", File.ReadAllText(Path.Combine(foreign, "thesis.txt")));

        result = await game.Build(new BuildOptions { OutputDirectory = game.ProjectDirectory });
        Assert.Contains(result.Diagnostics, d => d.Code == "MNVB104");
    }

    [Fact]
    public void Validation_DetectsDamagedAndIncompletePackages()
    {
        var copy = Path.Combine(game.Root, "Damaged");
        foreach (var file in Directory.EnumerateFiles(Output, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(copy, Path.GetRelativePath(Output, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        File.WriteAllText(Directory.GetFiles(Path.Combine(copy, "Content", "Bytecode")).Single(), "garbage");
        File.Delete(Path.Combine(copy, "glfw3.dll"));
        File.WriteAllText(Path.Combine(copy, "MNVTEngine.Editor.dll"), "");

        var codes = BuildValidator.Validate(copy).Where(d => d.Severity == BuildSeverity.Error).Select(d => d.Code).ToHashSet();
        Assert.Superset(new HashSet<string> { "MNVB505", "MNVB506", "MNVB507", "MNVB508", "MNVB511" }, codes);
    }
}
