using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.CLI;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;

namespace MNVTEngine.Build.Tests;

/// <summary>Fast checks of profiles, templates, content compilation and the CLI; nothing here runs dotnet publish.</summary>
public sealed class BuildPipelineTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-pipeline-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ProjectTemplate Template(string id) =>
        ProjectTemplates.List(EngineLocation.TemplatesDirectory(EngineLocation.FindRoot())).Single(t => t.Id == id);

    private GameProject NewProject(string id = "empty-2d", string name = "Game")
    {
        var file = ProjectTemplates.Create(Template(id), name, Path.Combine(_root, name), EngineLocation.FindRoot());
        return GameProject.Load(file);
    }

    private static ComponentRegistry Registry => EngineComponents.CreateRegistry();

    [Fact]
    public void Templates_ReplaceEveryToken_AndProduceLoadableProjects()
    {
        var project = NewProject("basic-2d", "My-Game");
        Assert.Equal("My-Game", project.Name);
        Assert.Equal(Path.Combine(project.RootDirectory, "Scripts", "My_Game.Scripts.csproj"), Path.GetFullPath(project.ResolveProjectFile(project.Scripts!.Project!)));
        var leftovers = Directory.EnumerateFiles(project.RootDirectory, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".png", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("{{", StringComparison.Ordinal) || f.Contains("__", StringComparison.Ordinal));
        Assert.Empty(leftovers);
        Assert.False(File.Exists(Path.Combine(project.RootDirectory, ProjectTemplates.ManifestFile)));
        Assert.Equal(64, File.ReadAllText(ContentKeys.PathFor(project)).Trim().Length); // every project gets its own key
    }

    [Fact]
    public void Templates_RefuseInvalidNames_AndNonEmptyFolders()
    {
        Assert.Throws<ArgumentException>(() => ProjectTemplates.Create(Template("empty-2d"), "../evil", Path.Combine(_root, "x"), EngineLocation.FindRoot()));
        Directory.CreateDirectory(Path.Combine(_root, "Taken"));
        File.WriteAllText(Path.Combine(_root, "Taken", "keep.txt"), "mine");
        Assert.Throws<IOException>(() => ProjectTemplates.Create(Template("empty-2d"), "Taken", Path.Combine(_root, "Taken"), EngineLocation.FindRoot()));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_root, "Taken", "keep.txt")));
    }

    [Fact]
    public void ResolveProfile_PicksByName_ThenConfiguration_ThenRelease_AndAppliesOverrides()
    {
        var project = NewProject();
        var diagnostics = new List<BuildDiagnostic>();

        Assert.Equal(("release", "Release", "win-x64"), Describe(GameBuilder.ResolveProfile(project, new BuildOptions(), diagnostics)));
        Assert.Equal(("debug", "Debug", "win-x64"), Describe(GameBuilder.ResolveProfile(project, new BuildOptions { Configuration = "debug" }, diagnostics)));
        Assert.Equal(("debug", "Release", "win-x64"), Describe(GameBuilder.ResolveProfile(project, new BuildOptions { Profile = "DEBUG", Configuration = "Release" }, diagnostics)));
        Assert.Empty(diagnostics);
        Assert.True(GameBuilder.ResolveProfile(project, new BuildOptions(), diagnostics)!.ShowsConsole is false);

        Assert.Null(GameBuilder.ResolveProfile(project, new BuildOptions { Profile = "ship" }, diagnostics));
        Assert.Null(GameBuilder.ResolveProfile(project, new BuildOptions { Configuration = "fast" }, diagnostics));
        Assert.Null(GameBuilder.ResolveProfile(project, new BuildOptions { Target = "linux-arm" }, diagnostics));
        Assert.Equal(["MNVB101", "MNVB102", "MNVB103"], diagnostics.Select(d => d.Code));

        static (string, string, string) Describe(BuildProfile? p) => (p!.Name, p.Configuration, p.Target);
    }

    [Fact]
    public void ProjectValidation_RejectsBadProfiles()
    {
        var project = NewProject();
        File.WriteAllText(project.ProjectFile, """
            { "schemaVersion": 1, "name": "G", "startupScene": "Scenes/Main.mnvt",
              "buildProfiles": [ { "name": "a", "configuration": "Fast" }, { "name": "a", "target": "ps5" }, { "name": "b", "outputDirectory": "../../out" } ] }
            """);
        var ex = Assert.Throws<SerializationException>(() => GameProject.Load(project.ProjectFile));
        Assert.Contains("buildProfiles[0].configuration", ex.Diagnostics.Select(d => d.Path));
        Assert.Contains("buildProfiles[1].name", ex.Diagnostics.Select(d => d.Path));
        Assert.Contains("buildProfiles[1].target", ex.Diagnostics.Select(d => d.Path));
        Assert.Contains("buildProfiles[2].outputDirectory", ex.Diagnostics.Select(d => d.Path));
    }

    [Theory]
    [InlineData("Scene Demo", "SceneDemo")]
    [InlineData("3D Rally!", "DRally")]
    [InlineData("***", "Game")]
    public void ExecutableNames_AreDerivedSafely(string project, string expected) =>
        Assert.Equal(expected, GamePackage.ExecutableNameFor(project));

    [Fact]
    public void ContentCompiler_ReportsStrictScriptErrors_AndMissingReferences_WithoutTouchingTheProject()
    {
        var project = NewProject();
        var missions = Path.Combine(project.AssetsDirectory, "Missions");
        Directory.CreateDirectory(missions);
        File.WriteAllText(Path.Combine(missions, "Bad.mnvts"), "on start {\n    player.teleport(1)\n}\n");
        File.WriteAllText(Path.Combine(missions, "Bad.mnvts.meta"), """{ "schemaVersion": 1, "guid": "11111111-1111-4111-8111-111111111111", "settings": {} }""");
        var scene = Path.Combine(project.AssetsDirectory, "Scenes", "Main.mnvt");
        File.WriteAllText(scene, File.ReadAllText(scene).Replace(
            "\"components\": [",
            "\"components\": [ { \"type\": \"MNVTEngine.SpriteRenderer\", \"id\": \"b0000000-0000-0000-0000-0000000000ff\", \"enabled\": true, \"data\": { \"texture\": \"asset:22222222-2222-4222-8222-222222222222\" } },",
            StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(project.AssetsDirectory, "notes.txt"), "no meta");
        var before = Directory.GetFiles(project.RootDirectory, "*", SearchOption.AllDirectories).Order().ToList();

        var result = ContentCompiler.Compile(project, Registry, archive: null);

        var script = Assert.Single(result.Diagnostics, d => d.Code == "MNV3014"); // the editor only warns (MNV3015); a build must not link-fail at run time
        Assert.Equal((BuildSeverity.Error, "Missions/Bad.mnvts", 2), (script.Severity, script.File, script.Line));
        Assert.Contains(result.Diagnostics, d => d.Code == "MNVB206" && d.File == "Scenes/Main.mnvt" && d.Message.Contains("22222222", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, d => d.Severity == BuildSeverity.Warning && d.File == "notes.txt");
        Assert.Equal(before, Directory.GetFiles(project.RootDirectory, "*", SearchOption.AllDirectories).Order());
    }

    [Fact]
    public void ContentCompiler_WritesIndexAssetsBytecodeAndAProjectWithoutDevelopmentSettings_IntoTheArchive()
    {
        var project = NewProject();
        File.WriteAllText(Path.Combine(project.AssetsDirectory, "Hello.mnvts"), "on start { log(\"hi\") }");
        File.WriteAllText(Path.Combine(project.AssetsDirectory, "Hello.mnvts.meta"), """{ "schemaVersion": 1, "guid": "33333333-3333-4333-8333-333333333333", "settings": {} }""");
        var key = Assets.ContentArchive.CreateKey();
        var path = Path.Combine(_root, "content.mnvtpak");

        ContentResult result;
        using (var writer = new Assets.ContentArchive.Writer(path, key))
        {
            result = ContentCompiler.Compile(project, Registry, writer);
        }

        Assert.False(result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics));
        using var archive = Assets.ContentArchive.Open(path, key);
        Assert.DoesNotContain(archive.Entries, e => e.EndsWith(".meta", StringComparison.Ordinal)); // GUIDs travel in the index
        var assets = Assets.AssetDatabase.OpenPackaged(archive);
        Assert.Equal("Scenes/Main.mnvt", assets.Get(Guid.Parse("7e000000-0000-4000-8000-000000000001"))!.Path);
        var compiled = Scripting.Bytecode.BytecodeSerializer.Read(assets.ReadCompiled(Guid.Parse("33333333-3333-4333-8333-333333333333"))!);
        Assert.Equal("Hello.mnvts", compiled.SourceName); // runtime errors point at the asset path
        Assert.Contains("log", compiled.NativeNames);
        Assert.DoesNotContain("buildProfiles", System.Text.Encoding.UTF8.GetString(archive.Read("Game.mnvtproj")), StringComparison.Ordinal);
        Assert.Equal("Game", GameProject.LoadPackaged(archive, _root).Name);
    }

    [Fact]
    public void GamePackage_RefusesScriptsOutsideThePackage_AndNewerSchemas()
    {
        Directory.CreateDirectory(Path.Combine(_root, "C"));
        File.WriteAllText(Path.Combine(_root, "C", GamePackage.ManifestFile), """{ "schemaVersion": 1, "scriptsAssembly": "../../evil.dll" }""");
        Assert.Throws<SerializationException>(() => GamePackage.Load(Path.Combine(_root, "C")));
        File.WriteAllText(Path.Combine(_root, "C", GamePackage.ManifestFile), """{ "schemaVersion": 99 }""");
        Assert.Throws<SerializationException>(() => GamePackage.Load(Path.Combine(_root, "C")));
    }

    [Fact]
    public async Task Cli_ReportsUsageErrors_AndCreatesAndValidatesProjects()
    {
        var output = new StringWriter();
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(Cli.UsageError, await Cli.RunAsync(["frobnicate"], output, NullLoggerFactory.Instance, ct));
        Assert.Equal(Cli.UsageError, await Cli.RunAsync(["build"], output, NullLoggerFactory.Instance, ct));
        Assert.Equal(Cli.UsageError, await Cli.RunAsync(["build", "x", "--profile"], output, NullLoggerFactory.Instance, ct));
        Assert.Equal(Cli.UsageError, await Cli.RunAsync(["build", "x", "--fast"], output, NullLoggerFactory.Instance, ct));
        Assert.Equal(Cli.UsageError, await Cli.RunAsync(["new", "G", "--template", "nope", "--path", _root], output, NullLoggerFactory.Instance, ct));

        Assert.Equal(Cli.Ok, await Cli.RunAsync(["new", "Fresh", "--path", _root], output, NullLoggerFactory.Instance, ct));
        Assert.Equal(Cli.Ok, await Cli.RunAsync(["validate", Path.Combine(_root, "Fresh")], output, NullLoggerFactory.Instance, ct));
        Assert.Equal(Cli.Failed, await Cli.RunAsync(["new", "Fresh", "--path", _root], output, NullLoggerFactory.Instance, ct)); // folder not empty
        Assert.Equal(Cli.Failed, await Cli.RunAsync(["validate", Path.Combine(_root, "Missing")], output, NullLoggerFactory.Instance, ct));
        Assert.Contains("MNVB100", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GameCodeLoading_AndPackaging_AgreeOnTheManagedFolder() =>
        Assert.Equal(GamePackage.ManagedFolder, GameAssemblies.GameAssemblyLoadContext.ManagedDependenciesFolder);

    [Fact]
    public void CommandLine_ParsesOptionalValues()
    {
        var run = CommandLine.Parse(["build", "p", "--run", "--output", "o"], ["--output"], [], ["--run"]);
        Assert.Equal((true, (string?)null, "o", "p"), (run.Has("--run"), run.Value("--run"), run.Value("--output"), run.Single("project")));
        Assert.Equal(120, run.Int("--run", 120));
        Assert.Equal(30, CommandLine.Parse(["build", "p", "--run", "30"], [], [], ["--run"]).Int("--run", 120));
        Assert.Throws<FormatException>(() => CommandLine.Parse(["build", "--output", "a", "--output", "b"], ["--output"], []));
    }
}
