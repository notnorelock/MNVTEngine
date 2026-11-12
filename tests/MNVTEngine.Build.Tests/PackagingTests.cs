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
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(Output, "Dependencies", "Managed")), f => Path.GetFileName(f).Contains("Shaderc", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Contains("ShaderCompiler", StringComparison.Ordinal)); // games run on precompiled SPIR-V
        Assert.Single(Directory.GetFiles(Path.Combine(Output, "Dependencies", "Runtime"), "coreclr.dll", SearchOption.AllDirectories)); // private .NET runtime
        var data = Path.Combine(Output, "PackTest_Data");
        var package = GamePackage.Load(data);

        // Only the game's modules are built in (generated bootstrap): 2D, no 3D world, physics or terrain; natives of its modules only.
        Assert.Contains("world.2d", package.Modules!);
        Assert.DoesNotContain(package.Modules!, id => id is "world.3d" or "physics.3d" or "terrain");
        var managed = Directory.EnumerateFiles(Path.Combine(Output, "Dependencies", "Managed")).Select(Path.GetFileNameWithoutExtension).ToList();
        Assert.DoesNotContain(managed, f => f!.StartsWith("MNVTEngine.World3D", StringComparison.Ordinal) || f.StartsWith("MNVTEngine.Terrain", StringComparison.Ordinal) || f.StartsWith("Bepu", StringComparison.Ordinal));
        Assert.Equal(BuildValidator.RequiredNatives(package), Directory.EnumerateFiles(Path.Combine(Output, "Dependencies", "Native")).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Contains("glfw3.dll", BuildValidator.RequiredNatives(package));
        Assert.Equal(package.Modules!.Contains("audio"), File.Exists(Path.Combine(Output, "Dependencies", "Native", "soft_oal.dll")));
        Assert.Equal(
            ["defaultresources.mnvtpak", "globals.mnvtpak", "level0.mnvtpak", "package.json"],
            Directory.EnumerateFiles(data).Select(Path.GetFileName).Where(f => f is not "resources.mnvtpak" and not "sharedassets.mnvtpak").Order(StringComparer.Ordinal));
        Assert.Equal(package.ArchiveFiles.Append("package.json").Order(StringComparer.Ordinal), Directory.EnumerateFiles(data).Select(Path.GetFileName).Order(StringComparer.Ordinal)); // nothing unlisted
        Assert.Equal(("globals.mnvtpak", "Scenes/Main.mnvt"), (package.Archives[0].File, package.Archives.Single(a => a.File == "level0.mnvtpak").Scene));
        Assert.Empty(Directory.GetFiles(Output, "*.pdb", SearchOption.AllDirectories)); // release builds ship no symbols
        Assert.Empty(BuildValidator.Validate(Output));

        // Assets, bytecode and C# game code are only in the encrypted archives: code in globals, the scene in level0,
        // engine-generated SPIR-V in the default resources.
        var key = Convert.FromHexString(File.ReadAllText(Path.Combine(game.ProjectDirectory, ContentKeys.RelativePath)).Trim());
        Assert.Equal(key, ContentKeys.ReadFromHost(Path.Combine(Output, "PackTest.dll")));
        using (var content = package.OpenContent(data, key))
        {
            Assert.Equal("globals.mnvtpak", content.ArchiveOf("Scripts/PackTest.Scripts.dll"));
            Assert.DoesNotContain("Scripts/MNVTEngine.Scenes.dll", content.Entries); // the host's copy is used
            Assert.Equal("level0.mnvtpak", content.ArchiveOf("Assets/Scenes/Main.mnvt"));
            Assert.Single(content.Entries, e => e.StartsWith("Bytecode/", StringComparison.Ordinal));
            Assert.All(content.Entries.Where(e => e.StartsWith("Shaders/", StringComparison.Ordinal)), e => Assert.Equal("defaultresources.mnvtpak", content.ArchiveOf(e)));
            Assert.True(content.Entries.Count(e => e.StartsWith("Shaders/", StringComparison.Ordinal)) >= 2);
        }

        foreach (var archiveFile in package.ArchiveFiles)
        {
            var raw = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(data, archiveFile)));
            Assert.DoesNotContain("reach_goal", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("PlayerController", raw, StringComparison.Ordinal);
        }

        var run = await BuildValidator.RunAsync(Output, 20, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        Assert.Empty(run);
        var log = File.ReadAllText(Path.Combine(Output, "Logs", "game.log"));
        Assert.Contains("[script] Move to the goal with WASD", log, StringComparison.Ordinal); // C# component, scene and bytecode all loaded from the archives
        Assert.Contains(", 0 compiled at run time", log, StringComparison.Ordinal); // every shader came precompiled from the default resources
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

        var pak = Path.Combine(copy, "PackTest_Data", "level0.mnvtpak");
        var bytes = File.ReadAllBytes(pak);
        bytes[100] ^= 1;
        File.WriteAllBytes(pak, bytes);
        File.Delete(Path.Combine(copy, "Dependencies", "Native", "glfw3.dll"));
        File.Delete(Path.Combine(copy, "Dependencies", "Managed", "Newtonsoft.Json.dll"));
        Directory.Delete(Path.Combine(copy, "Dependencies", "Runtime", "host"), recursive: true);
        File.WriteAllText(Path.Combine(copy, "Dependencies", "Managed", "MNVTEngine.Editor.dll"), "");
        File.WriteAllText(Path.Combine(copy, "Dependencies", "Managed", "MNVTEngine.World3D.dll"), ""); // a module the game does not have

        var codes = BuildValidator.Validate(copy).Where(d => d.Severity == BuildSeverity.Error).Select(d => d.Code).ToHashSet();
        Assert.Superset(new HashSet<string> { "MNVB504", "MNVB505", "MNVB506", "MNVB507", "MNVB508", "MNVB515", "MNVB517" }, codes);

        Assert.Contains(BuildValidator.Validate(Output, Assets.ContentArchive.CreateKey()), d => d.Code == "MNVB514"); // wrong key
    }
}
