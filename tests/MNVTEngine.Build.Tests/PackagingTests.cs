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
        FirstManifest = File.ReadAllText(Path.Combine(First.OutputDirectory!, "PackTest_Data", GamePackage.ManifestFile));
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
    public async Task Build_ProducesTheGameLayout_ThatRunsWithoutTheSdk()
    {
        Assert.Equal(Path.Combine(game.ProjectDirectory, "Builds", "release"), Output);
        Assert.Equal(
            ["Dependencies", "PackTest.dll", "PackTest.exe", "PackTest.runtimeconfig.json", "PackTest_Data"],
            Directory.EnumerateFileSystemEntries(Output).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(["Managed", "Native", "Runtime"], Directory.EnumerateDirectories(Path.Combine(Output, "Dependencies")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.True(File.Exists(Path.Combine(Output, "Dependencies", "Managed", "MNVTEngine.Runtime.dll")));
        Assert.True(File.Exists(Path.Combine(Output, "Dependencies", "Managed", "Newtonsoft.Json.dll")));
        Assert.Equal(["glfw3.dll", "soft_oal.dll"], Directory.EnumerateFiles(Path.Combine(Output, "Dependencies", "Native")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Single(Directory.GetFiles(Path.Combine(Output, "Dependencies", "Runtime"), "coreclr.dll", SearchOption.AllDirectories)); // private .NET runtime
        Assert.Equal(["content.mnvtpak", "package.json"], Directory.EnumerateFiles(Path.Combine(Output, "PackTest_Data")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Empty(Directory.GetFiles(Output, "*.pdb", SearchOption.AllDirectories)); // release builds ship no symbols
        Assert.Empty(BuildValidator.Validate(Output));

        // Assets, bytecode and C# game code are only in the encrypted archive.
        var key = Convert.FromHexString(File.ReadAllText(Path.Combine(game.ProjectDirectory, ContentKeys.RelativePath)).Trim());
        Assert.Equal(key, ContentKeys.ReadFromHost(Path.Combine(Output, "PackTest.dll")));
        using (var archive = Assets.ContentArchive.Open(Path.Combine(Output, "PackTest_Data", "content.mnvtpak"), key))
        {
            Assert.Contains("Scripts/PackTest.Scripts.dll", archive.Entries);
            Assert.DoesNotContain("Scripts/MNVTEngine.Scenes.dll", archive.Entries); // the host's copy is used
            Assert.Single(archive.Entries, e => e.StartsWith("Bytecode/", StringComparison.Ordinal));
        }

        var raw = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(Output, "PackTest_Data", "content.mnvtpak")));
        Assert.DoesNotContain("reach_goal", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("PlayerController", raw, StringComparison.Ordinal);

        var run = await BuildValidator.RunAsync(Output, 20, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        Assert.Empty(run);
        var log = File.ReadAllText(Path.Combine(Output, "Logs", "game.log"));
        Assert.Contains("[script] Move to the goal with WASD", log, StringComparison.Ordinal); // C# component, scene and bytecode all loaded from the archive
    }

    [Fact]
    public async Task Rebuild_IsByteForByteIdentical()
    {
        var again = await game.Build();
        Assert.True(again.Succeeded, string.Join("\n", again.Diagnostics));
        Assert.Equal(game.FirstManifest, File.ReadAllText(Path.Combine(Output, "PackTest_Data", GamePackage.ManifestFile)));
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

        var pak = Path.Combine(copy, "PackTest_Data", "content.mnvtpak");
        var bytes = File.ReadAllBytes(pak);
        bytes[100] ^= 1;
        File.WriteAllBytes(pak, bytes);
        File.Delete(Path.Combine(copy, "Dependencies", "Native", "glfw3.dll"));
        File.Delete(Path.Combine(copy, "Dependencies", "Managed", "Newtonsoft.Json.dll"));
        Directory.Delete(Path.Combine(copy, "Dependencies", "Runtime", "host"), recursive: true);
        File.WriteAllText(Path.Combine(copy, "Dependencies", "Managed", "MNVTEngine.Editor.dll"), "");

        var codes = BuildValidator.Validate(copy).Where(d => d.Severity == BuildSeverity.Error).Select(d => d.Code).ToHashSet();
        Assert.Superset(new HashSet<string> { "MNVB504", "MNVB505", "MNVB506", "MNVB507", "MNVB508", "MNVB515" }, codes);

        Assert.Contains(BuildValidator.Validate(Output, Assets.ContentArchive.CreateKey()), d => d.Code == "MNVB514"); // wrong key
    }
}
