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

    private static ComponentRegistry Registry => Build.EngineModuleSet.CreateRegistry();

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

        var result = ContentCompiler.Compile(project, Registry, output: null);

        var script = Assert.Single(result.Diagnostics, d => d.Code == "MNV3014"); // the editor only warns (MNV3015); a build must not link-fail at run time
        Assert.Equal((BuildSeverity.Error, "Missions/Bad.mnvts", 2), (script.Severity, script.File, script.Line));
        Assert.Contains(result.Diagnostics, d => d.Code == "MNVB206" && d.File == "Scenes/Main.mnvt" && d.Message.Contains("22222222", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, d => d.Severity == BuildSeverity.Warning && d.File == "notes.txt");
        Assert.Equal(before, Directory.GetFiles(project.RootDirectory, "*", SearchOption.AllDirectories).Order());
    }

    [Fact]
    public void ContentCompiler_WritesIndexAssetsBytecodeAndAProjectWithoutDevelopmentSettings_IntoTheArchives()
    {
        var project = NewProject();
        File.WriteAllText(Path.Combine(project.AssetsDirectory, "Hello.mnvts"), "on start { log(\"hi\") }");
        File.WriteAllText(Path.Combine(project.AssetsDirectory, "Hello.mnvts.meta"), """{ "schemaVersion": 1, "guid": "33333333-3333-4333-8333-333333333333", "settings": {} }""");
        var (result, archives, key) = CompileInto(project);

        Assert.False(result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics));
        using var archive = Assets.ContentArchiveSet.Open(_root, archives.Select(a => a.File), key);
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
    public void MissingMetas_AreCreatedOnce_WithAWarningToCommitThem()
    {
        var project = NewProject();
        var notes = Path.Combine(project.AssetsDirectory, "Docs", "notes.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(notes)!);
        File.WriteAllText(notes, "added with the file explorer");

        var created = Assert.Single(ContentCompiler.CreateMissingMetas(project));
        Assert.Equal(("MNVB209", "Docs/notes.txt"), (created.Code, created.File));
        var meta = File.ReadAllText(notes + ".meta");
        Assert.Empty(ContentCompiler.CreateMissingMetas(project));
        Assert.Equal(meta, File.ReadAllText(notes + ".meta")); // the GUID stays
        Assert.DoesNotContain(ContentCompiler.Compile(project, Registry, output: null).Diagnostics, d => d.File == "Docs/notes.txt");
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

        Assert.Equal(Cli.Ok, await Cli.RunAsync(["new", "Fresh", "--profile", "2d", "--path", _root], output, NullLoggerFactory.Instance, ct));
        Assert.Equal(Cli.Ok, await Cli.RunAsync(["validate", Path.Combine(_root, "Fresh")], output, NullLoggerFactory.Instance, ct));
        Assert.Equal(Cli.Failed, await Cli.RunAsync(["new", "Fresh", "--profile", "2d", "--path", _root], output, NullLoggerFactory.Instance, ct)); // folder not empty
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

    [Fact]
    public void ContentLayout_SplitsLevelsSharedAssetsResources_AndPrecompilesShaders()
    {
        var project = NewProject();
        var assets = project.AssetsDirectory;
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        void Asset(string path, byte[] data, string guid)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(assets, path))!);
            File.WriteAllBytes(Path.Combine(assets, path), data);
            File.WriteAllText(Path.Combine(assets, path + ".meta"), $$"""{ "schemaVersion": 1, "guid": "{{guid}}", "settings": {} }""");
        }

        const string Shared = "a1000000-0000-4000-8000-000000000001", OnlySecond = "a1000000-0000-4000-8000-000000000002";
        const string Shader = "a1000000-0000-4000-8000-000000000003", Material = "a1000000-0000-4000-8000-000000000004";
        Asset("Textures/shared.png", png, Shared);
        Asset("Textures/second.png", png, OnlySecond);
        Asset("Shaders/Tint.frag", "#version 450 core\nin vec2 vTexCoord;\nin vec4 vColor;\nlayout(binding = 0) uniform sampler2D uTexture;\nuniform vec4 uTint;\nout vec4 fragColor;\nvoid main() { fragColor = texture(uTexture, vTexCoord) * vColor * uTint; }\n"u8.ToArray(), Shader);
        Asset("Materials/Tint.mnvtmat", System.Text.Encoding.UTF8.GetBytes($$"""{ "schemaVersion": 1, "shader": "asset:{{Shader}}", "blend": "Alpha", "uniforms": { "uTint": [1, 0, 0, 1] } }"""), Material);
        Asset("Missions/Unused.mnvts", "on start { log(\"spawned by code\") }"u8.ToArray(), "a1000000-0000-4000-8000-000000000005");

        var main = Path.Combine(assets, "Scenes", "Main.mnvt");
        var template = File.ReadAllText(main);
        string WithSprite(string texture, string? material) => template.Replace(
            "\"components\": [",
            $$"""
            "components": [ { "type": "MNVTEngine.SpriteRenderer", "id": "b0000000-0000-0000-0000-0000000000ff", "enabled": true, "data": { "texture": "asset:{{texture}}"{{(material is null ? "" : $", \"material\": \"asset:{material}\"")}} } },
            """,
            StringComparison.Ordinal);
        File.WriteAllText(main, WithSprite(Shared, Material));
        Asset("Scenes/Second.mnvt", System.Text.Encoding.UTF8.GetBytes(WithSprite(Shared, null).Replace(
            "\"gameObjects\": [",
            $"\"gameObjects\": [ {{ \"id\": \"c0000000-0000-4000-8000-000000000001\", \"name\": \"Prop\", \"parent\": null, \"components\": [ {{ \"type\": \"MNVTEngine.SpriteRenderer\", \"id\": \"b0000000-0000-0000-0000-0000000000fe\", \"enabled\": true, \"data\": {{ \"texture\": \"asset:{OnlySecond}\" }} }} ] }},",
            StringComparison.Ordinal)), "a1000000-0000-4000-8000-000000000006");

        var (result, archives, key) = CompileInto(project);
        Assert.False(result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics));
        Assert.Equal(
            [("globals.mnvtpak", "globals", null), ("defaultresources.mnvtpak", "defaultResources", null), ("level0.mnvtpak", "level", "Scenes/Main.mnvt"),
             ("level1.mnvtpak", "level", "Scenes/Second.mnvt"), ("sharedassets.mnvtpak", "sharedAssets", null), ("resources.mnvtpak", "resources", null)],
            archives.Select(a => (a.File, a.Kind, a.Scene)));

        using var content = Assets.ContentArchiveSet.Open(_root, archives.Select(a => a.File), key);
        Assert.Equal("level0.mnvtpak", content.ArchiveOf("Assets/Materials/Tint.mnvtmat"));   // only Main uses the material
        Assert.Equal("level0.mnvtpak", content.ArchiveOf("Assets/Shaders/Tint.frag"));        // ...and, through it, the shader
        Assert.Equal("level1.mnvtpak", content.ArchiveOf("Assets/Textures/second.png"));
        Assert.Equal("sharedassets.mnvtpak", content.ArchiveOf("Assets/Textures/shared.png"));
        Assert.Equal("resources.mnvtpak", content.ArchiveOf("Assets/Missions/Unused.mnvts"));
        Assert.Equal("resources.mnvtpak", content.ArchiveOf(Assets.AssetDatabase.CompiledEntry(Guid.Parse("a1000000-0000-4000-8000-000000000005")))); // bytecode follows its script
        Assert.Equal("globals.mnvtpak", content.ArchiveOf("Game.mnvtproj"));

        // A 2D project: sprite vertex + fragment and the custom fragment. world.3d's mesh, shadow and sky shaders are not
        // part of its modules, so they are neither compiled nor shipped.
        Assert.Equal(3, result.Shaders);
        Assert.Equal(3, content.Archives[1].Entries.Count(e => e.StartsWith("Shaders/", StringComparison.Ordinal)));
        Assert.Equal(archives.Sum(a => a.Entries), content.Entries.Count());

        var index = Newtonsoft.Json.Linq.JObject.Parse(System.Text.Encoding.UTF8.GetString(content.Read(Assets.AssetDatabase.IndexEntry)));
        Assert.Equal("level1.mnvtpak", index["assets"]!.Single(a => (string?)a["guid"] == OnlySecond)["archive"]!.ToString());
    }

    [Fact]
    public void ContentCompiler_FailsTheBuild_OnAMaterialShaderTheDeviceWouldReject()
    {
        var project = NewProject();
        File.WriteAllText(Path.Combine(project.AssetsDirectory, "Broken.frag"), "#version 450 core\nout vec4 c;\nvoid main() { c = undefined; }\n");
        File.WriteAllText(Path.Combine(project.AssetsDirectory, "Broken.frag.meta"), """{ "schemaVersion": 1, "guid": "a2000000-0000-4000-8000-000000000001", "settings": {} }""");
        File.WriteAllText(Path.Combine(project.AssetsDirectory, "Broken.mnvtmat"), """{ "schemaVersion": 1, "shader": "asset:a2000000-0000-4000-8000-000000000001", "blend": "Alpha", "uniforms": {} }""");
        File.WriteAllText(Path.Combine(project.AssetsDirectory, "Broken.mnvtmat.meta"), """{ "schemaVersion": 1, "guid": "a2000000-0000-4000-8000-000000000002", "settings": {} }""");

        var error = Assert.Single(ContentCompiler.Compile(project, Registry, output: null).Diagnostics, d => d.Code == "MNVB210");
        Assert.Equal(("Broken.mnvtmat", BuildSeverity.Error), (error.File, error.Severity));
        Assert.Contains("undefined", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Models_ShipAsTheirArtifact_AndFailTheBuildWhenNeverImported()
    {
        var project = NewProject();
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Models", "Khronos", "BoxAnimated.glb"), Path.Combine(project.AssetsDirectory, "Box.glb"));
        File.WriteAllText(Path.Combine(project.AssetsDirectory, "Box.glb.meta"), """{ "schemaVersion": 1, "guid": "44444444-4444-4444-8444-444444444444", "settings": {} }""");
        var guid = Guid.Parse("44444444-4444-4444-8444-444444444444");

        Assert.Single(ContentCompiler.Compile(project, Registry, output: null).Diagnostics, d => d.Code == "MNVB222");

        Assert.False(ContentCompiler.ImportModels(project).HasErrors());
        var (result, archives, key) = CompileInto(project);
        Assert.False(result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics));
        using var archive = Assets.ContentArchiveSet.Open(_root, archives.Select(a => a.File), key);
        Assert.DoesNotContain(archive.Entries, e => e.EndsWith("Box.glb", StringComparison.Ordinal)); // the source file is not shipped
        var model = Assets.AssetDatabase.OpenPackaged(archive).ReadArtifact(guid, Assets.Models.ModelArtifact.Name);
        Assert.NotNull(model);
        Assert.NotEmpty(Assets.Models.ModelArtifact.Read(model).Meshes);
    }

    [Fact]
    public void Shaders_ArePrecompiledForTheProjectsModulesOnly()
    {
        // 3D: sprite (2 stages), the shared mesh vertex stage with the Standard, Unlit and Sky fragments, the shadow
        // caster (2), the skinned and instanced vertex stages (3); terrain adds its splat fragment.
        Assert.Equal(12, ContentCompiler.Compile(NewProject("empty-3d", "Deep"), Registry, output: null).Shaders);
        Assert.Equal(2, ContentCompiler.Compile(NewProject("empty-2d", "Flat"), Registry, output: null).Shaders); // the sprite shader only
    }

    [Fact]
    public void PackagedProjectFile_AlwaysDeclaresTheProfile_EvenForAProjectFromBeforeProfiles()
    {
        var project = NewProject("empty-3d");
        var legacy = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(project.ProjectFile));
        legacy["schemaVersion"] = 1;
        legacy.Remove("profile");
        File.WriteAllText(project.ProjectFile, legacy.ToString());
        project = GameProject.Load(project.ProjectFile);
        Assert.True(project.NeedsProfileMigration);

        var (_, archives, key) = CompileInto(project);
        using var archive = Assets.ContentArchiveSet.Open(_root, archives.Select(a => a.File), key);
        var packaged = GameProject.LoadPackaged(archive, _root);
        Assert.Equal(Modules.ProjectProfile.ThreeD, packaged.Profile);
        Assert.Equal(GameProject.CurrentSchemaVersion, packaged.SchemaVersion);
    }

    private (ContentResult Result, List<PackagedArchive> Archives, byte[] Key) CompileInto(GameProject project)
    {
        var key = Assets.ContentArchive.CreateKey();
        var writer = new ContentArchiveWriter(_root, key);
        ContentResult result;
        using (writer)
        {
            result = ContentCompiler.Compile(project, Registry, writer);
        }

        return (result, writer.Archives, key);
    }
}