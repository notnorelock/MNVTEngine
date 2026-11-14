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

    /// <summary>Reproducible engine version (fixed build number and time), so rebuilds can be compared byte for byte.</summary>
    public static readonly IReadOnlyDictionary<string, string> Reproducible = new Dictionary<string, string>
    {
        ["MNVT_REPRODUCIBLE"] = "true",
        ["MNVT_BUILD_NUMBER"] = "7",
        ["MNVT_BUILD_TIMESTAMP"] = "2026-11-14T12:00:00Z",
    };

    public Task<BuildResult> Build(BuildOptions? options = null) =>
        new GameBuilder(NullLogger.Instance).BuildAsync(ProjectDirectory, (options ?? new BuildOptions()) with { VersionVariables = options?.VersionVariables ?? Reproducible }, TestContext.Current.CancellationToken);

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

[Collection(GameExports.Name)]
public sealed class PackagingTests(PackagedGameFixture game) : IClassFixture<PackagedGameFixture>
{
    private string Output => game.First.OutputDirectory!;

    [Fact]
    public async Task Build_ProducesTheGameLayout_ThatRunsWithoutTheSdk()
    {
        Assert.Equal(Path.Combine(game.ProjectDirectory, "Builds", "release"), Output);
        Assert.Equal(
            ["Dependencies", "MNVTCrashHandler.dll", "MNVTCrashHandler.exe", "MNVTCrashHandler.runtimeconfig.json", "PackTest.dll", "PackTest.exe", "PackTest.runtimeconfig.json", "PackTest_Data"],
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

        // Framework pruning: unreachable runtime-pack assemblies are gone (also those only a facade such as netstandard
        // forwards to), deps.json lists exactly what remains (MNVB504 in the validation below), CoreLib stays.
        var framework = GamePackager.FrameworkDirectory(Output)!;
        Assert.True(File.Exists(Path.Combine(framework, "System.Private.CoreLib.dll")));
        Assert.True(File.Exists(Path.Combine(framework, "createdump.exe"))); // crash dumps
        Assert.False(File.Exists(Path.Combine(framework, "Microsoft.VisualBasic.Core.dll")));
        Assert.False(File.Exists(Path.Combine(framework, "System.Net.HttpListener.dll")));
        Assert.False(File.Exists(Path.Combine(framework, "clrgc.dll")));

        // build-report.json next to the package (not inside it), with modules, reasons and sizes.
        var report = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(BuildReport.PathFor(Output)));
        Assert.Equal(("2d", "jit", false), ((string?)report["profile"], (string?)report["scriptingBackend"]!["id"], (bool)report["devTools"]!));
        Assert.Contains(report["modules"]!["enabled"]!, m => (string?)m["id"] == "world.2d" && (string?)m["inclusion"] is "automatic" or "dependency");
        Assert.Contains(report["modules"]!["excluded"]!, m => (string?)m["id"] == "world.3d" && (string?)m["reason"] == "not available in 2D");
        Assert.Contains(report["assemblies"]!["excluded"]!, a => (string?)a["name"] == "MNVTEngine.World3D.Rendering");
        Assert.Equal(Directory.EnumerateFiles(Output, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length), (long)report["sizes"]!["total"]!);
        Assert.True((long)report["framework"]!["removedBytes"]! > 5_000_000);
        Assert.Equal(
            ["defaultresources.mnvtpack", "globals.mnvtpack", "level0.mnvtpack", "package.json", "package.sig"],
            Directory.EnumerateFiles(data).Select(Path.GetFileName).Where(f => f is not "resources.mnvtpack" and not "sharedassets.mnvtpack").Order(StringComparer.Ordinal));
        Assert.Equal(package.PackageFiles.Append("package.json").Append("package.sig").Order(StringComparer.Ordinal), Directory.EnumerateFiles(data).Select(Path.GetFileName).Order(StringComparer.Ordinal)); // nothing unlisted
        Assert.Equal(("globals.mnvtpack", "level"), (package.Packages[0].File, package.Packages.Single(a => a.File == "level0.mnvtpack").Kind));
        Assert.DoesNotContain("Scenes/", File.ReadAllText(Path.Combine(data, GamePackage.ManifestFile)), StringComparison.Ordinal); // no project structure in plain text
        Assert.Empty(Directory.GetFiles(Output, "*.pdb", SearchOption.AllDirectories)); // release builds ship no symbols
        Assert.Empty(BuildValidator.Validate(Output));

        // No development project: no project file, source asset folders, metas, keys or editor/importer assemblies.
        Assert.Empty(Directory.GetFiles(Output, "*" + GameProject.Extension, SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Output, "*.meta", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Output, "*.key", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Output, "*.pem", SearchOption.AllDirectories));
        Assert.DoesNotContain(Directory.EnumerateDirectories(Output, "*", SearchOption.AllDirectories), d => Path.GetFileName(d) is "Assets" or "Library" or "Settings" or "Scripts");
        Assert.DoesNotContain(Directory.EnumerateFiles(Output, "*.dll", SearchOption.AllDirectories), f => Path.GetFileName(f).StartsWith("MNVTEngine.Editor", StringComparison.Ordinal)
            || Path.GetFileName(f).StartsWith("MNVTEngine.Assets.Models", StringComparison.Ordinal) || Path.GetFileName(f).StartsWith("MNVTEngine.Build", StringComparison.Ordinal));

        // ...and the build cannot be opened as one, by folder or by any file in it.
        Assert.Equal(ProjectFolderKind.ExportedGame, GameProject.Classify(Output));
        Assert.Equal(ProjectFolderKind.GamePackages, GameProject.Classify(data));
        Assert.Equal(ProjectFolderKind.ExportedGame, GameProject.Classify(Path.Combine(Output, "PackTest.exe")));
        Assert.Equal(ProjectFolderKind.ExportedGame, Assert.Throws<NotAProjectException>(() => GameProject.Load(Output)).Kind);
        Assert.Throws<NotAProjectException>(() => GameProject.Load(data));

        // Assets, bytecode and C# game code are only in the signed, encrypted packages: code in globals, the scene in
        // level0, engine-generated SPIR-V in the default resources.
        var key = IO.Packages.PackageEncryptionKey.FromHex(File.ReadAllText(Path.Combine(game.ProjectDirectory, ContentKeys.RelativePath)));
        Assert.Equal(key.Id, ContentKeys.ReadFromHost(Path.Combine(Output, "PackTest.dll"))!.Id);
        Assert.DoesNotContain(key.ToHex(), File.ReadAllText(Path.Combine(Output, "PackTest.dll"), System.Text.Encoding.Latin1), StringComparison.OrdinalIgnoreCase); // masked, not a plain string
        var signer = ContentKeys.ReadVerificationKeyFromHost(Path.Combine(Output, "PackTest.dll"))!;
        Assert.Equal(package.SigningKeyId, signer.Id);
        using (var content = package.OpenContent(data, new IO.Packages.PackageOpenOptions(new IO.Packages.PackageKeyRing(key), [signer], RequireSignature: true)))
        {
            Assert.All(content.Packages, p => Assert.Equal((IO.Packages.PackageSignatureStatus.Trusted, true, package.BuildId), (p.Signature, p.Info.IsEncrypted, p.Info.BuildId)));
            Assert.Equal("globals.mnvtpack", content.PackageOf("Scripts/PackTest.Scripts.dll"));
            Assert.Equal("globals.mnvtpack", content.PackageOf(GameProject.RuntimeConfigEntry));
            Assert.DoesNotContain("Scripts/MNVTEngine.Scenes.dll", content.Entries); // the host's copy is used
            Assert.Equal("level0.mnvtpack", content.PackageOf("Assets/Scenes/Main.mnvt"));
            Assert.Single(content.Entries, e => e.StartsWith("Bytecode/", StringComparison.Ordinal));
            Assert.All(content.Entries.Where(e => e.StartsWith("Shaders/", StringComparison.Ordinal)), e => Assert.Equal("defaultresources.mnvtpack", content.PackageOf(e)));
            Assert.True(content.Entries.Count(e => e.StartsWith("Shaders/", StringComparison.Ordinal)) >= 2);
            Assert.DoesNotContain(content.Entries, e => e.EndsWith(".meta", StringComparison.Ordinal) || e.EndsWith(GameProject.Extension, StringComparison.Ordinal));
        }

        foreach (var packageFile in package.PackageFiles)
        {
            var raw = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(data, packageFile)));
            Assert.DoesNotContain("reach_goal", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("PlayerController", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("Scenes/Main", raw, StringComparison.Ordinal); // file names are in the encrypted index
        }

        var run = await BuildValidator.RunAsync(Output, 20, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        Assert.Empty(run);
        var log = File.ReadAllText(Path.Combine(Output, "Logs", "game.log"));
        Assert.Contains("[script] Move to the goal with WASD", log, StringComparison.Ordinal); // C# component, scene and bytecode all loaded from the packages
        Assert.Contains(", 0 compiled at run time", log, StringComparison.Ordinal); // every shader came precompiled from the default resources
    }

    [Fact]
    public void Export_EmbedsItsEngineBuild_InTheExecutableManifestPackagesAndReport()
    {
        var manifest = GamePackage.Load(Path.Combine(Output, "PackTest_Data"));
        var engine = manifest.Engine!;
        Assert.Matches(@"^1\.\d{4}\.\d+\.7-dev$", engine.Version); // MAJOR.DDMM.PATCH.BUILD, the fixed build number
        Assert.Equal("1411", engine.Version.Split('.')[1]);
        Assert.Equal(engine.Version, manifest.EngineVersion);
        Assert.Equal(new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc), engine.TimestampUtc);
        Assert.Equal(EngineVersion.CommitHash, engine.Commit ?? "unknown"); // the engine tree the tests run from

        // Every engine binary of the export: the game's executable, the engine assemblies, the crash handler.
        foreach (var binary in new[] { "PackTest.exe", "PackTest.dll", "MNVTCrashHandler.exe", Path.Combine("Dependencies", "Managed", "MNVTEngine.Runtime.dll"), Path.Combine("Dependencies", "Managed", "MNVTEngine.Core.dll") })
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(Output, binary));
            Assert.Equal((engine.FileVersion, binary), (info.FileVersion, binary));
            Assert.StartsWith(engine.Version + "+", info.ProductVersion, StringComparison.Ordinal);
        }

        var report = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(BuildReport.PathFor(Output)));
        Assert.Equal((engine.Version, engine.BuildId.ToString("D"), "game:PackTest", "2d", "jit"),
            ((string?)report["engineVersion"], (string?)report["engine"]!["buildId"], (string?)report["engine"]!["kind"], (string?)report["engine"]!["context"]!["profile"], (string?)report["engine"]!["context"]!["scriptingBackend"]));
        Assert.Contains("world.2d", report["engine"]!["context"]!["modules"]!.Values<string>());

        foreach (var file in Directory.GetFiles(Path.Combine(Output, "PackTest_Data"), "*.mnvtpack"))
        {
            using var package = IO.Packages.MnvtPackage.Open(file, PackageTools.OptionsFor(file));
            Assert.Equal((engine.Version, engine.BuildId.ToString("D")), (package.Info.Properties["engine"], package.Info.Properties["engineBuild"]));
        }
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

        var pak = Path.Combine(copy, "PackTest_Data", "level0.mnvtpack");
        var bytes = File.ReadAllBytes(pak);
        bytes[400] ^= 1; // chunk data
        File.WriteAllBytes(pak, bytes);
        File.Delete(Path.Combine(copy, "Dependencies", "Native", "glfw3.dll"));
        File.Delete(Path.Combine(copy, "Dependencies", "Managed", "Newtonsoft.Json.dll"));
        Directory.Delete(Path.Combine(copy, "Dependencies", "Runtime", "host"), recursive: true);
        File.WriteAllText(Path.Combine(copy, "Dependencies", "Managed", "MNVTEngine.Editor.dll"), "");
        File.WriteAllText(Path.Combine(copy, "Dependencies", "Managed", "MNVTEngine.World3D.dll"), ""); // a module the game does not have

        var codes = BuildValidator.Validate(copy).Where(d => d.Severity == BuildSeverity.Error).Select(d => d.Code).ToHashSet();
        Assert.Superset(new HashSet<string> { "MNVB504", "MNVB505", "MNVB506", "MNVB507", "MNVB508", "MNVB515", "MNVB517" }, codes);

        Assert.Contains(BuildValidator.Validate(Output, new IO.Packages.PackageKeyRing(IO.Packages.PackageEncryptionKey.Create())), d => d.Code == "MNVB514"); // wrong key
    }

    [Fact]
    public async Task TamperedManifest_OrSwappedPackages_StopTheGame()
    {
        var copy = Path.Combine(game.Root, "Tampered");
        foreach (var file in Directory.EnumerateFiles(Output, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(copy, Path.GetRelativePath(Output, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        // An edited manifest no longer matches its signature.
        var manifest = Path.Combine(copy, "PackTest_Data", GamePackage.ManifestFile);
        var original = File.ReadAllText(manifest);
        File.WriteAllText(manifest, original.Replace("\"name\": \"PackTest\"", "\"name\": \"PackTest2\"", StringComparison.Ordinal));
        Assert.NotEqual(original, File.ReadAllText(manifest));
        Assert.Contains(BuildValidator.Validate(copy), d => d.Code == "MNVB519");
        var run = await BuildValidator.RunAsync(copy, 5, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        Assert.Contains("missing, damaged or cannot be decrypted", Assert.Single(run).Message, StringComparison.Ordinal);

        // A validly signed package from another build in place of this one's is refused by identity.
        File.WriteAllText(manifest, original);
        var other = await game.Build(new BuildOptions { Profile = "debug", OutputDirectory = Path.Combine(game.Root, "OtherBuild") });
        Assert.True(other.Succeeded, string.Join("\n", other.Diagnostics));
        File.Copy(Path.Combine(other.OutputDirectory!, "PackTest_Data", "level0.mnvtpack"), Path.Combine(copy, "PackTest_Data", "level0.mnvtpack"), overwrite: true);
        Assert.Contains(BuildValidator.Validate(copy), d => d.Code == "MNVB514" && d.Message.Contains("not the package this build shipped", StringComparison.Ordinal));
    }

    [Fact]
    public void PackageTools_InspectVerifyAndExtract_WithTheGamesKeys()
    {
        var level = Path.Combine(Output, "PackTest_Data", "level0.mnvtpack");
        var options = PackageTools.OptionsFor(level);
        var inspection = PackageTools.Inspect(level, options, listEntries: true);
        Assert.Contains("Signature    Trusted", inspection, StringComparison.Ordinal);
        Assert.Contains("Assets/Scenes/Main.mnvt", inspection, StringComparison.Ordinal);
        Assert.Contains("Contents     not shown", PackageTools.Inspect(level, new IO.Packages.PackageOpenOptions()), StringComparison.Ordinal); // no key: public header only
        Assert.Empty(PackageTools.Verify(level, options, requireTrustedSignature: true));

        var extracted = Path.Combine(game.Root, "Extracted");
        Assert.True(PackageTools.Extract(Path.Combine(Output, "PackTest_Data", "globals.mnvtpack"), extracted, options) > 0);
        Assert.True(File.Exists(Path.Combine(extracted, GameProject.RuntimeConfigEntry)));
        Assert.Equal(ProjectFolderKind.ExportedGame, Assert.Throws<NotAProjectException>(() => GameProject.Load(extracted)).Kind);

        // Renaming the runtime configuration to a project file does not make a project either.
        File.Move(Path.Combine(extracted, GameProject.RuntimeConfigEntry), Path.Combine(extracted, "PackTest" + GameProject.Extension));
        Assert.Throws<NotAProjectException>(() => GameProject.Load(Path.Combine(extracted, "PackTest" + GameProject.Extension)));
    }
}
