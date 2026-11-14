using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Runtime;

namespace MNVTEngine.Build.Tests;

/// <summary>
/// C# code in DLC, end to end on both scripting backends: a game exported without the DLC, then a DLC package with a
/// component that uses the game's own types, copied into the export, whose scene the game loads and whose code runs.
/// </summary>
[Collection(GameExports.Name)]
public sealed class DlcCodeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-dlc-code-").FullName;

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
        var directory = Path.Combine(_root, "DlcTest");
        var projectFile = ProjectTemplates.Create(template, "DlcTest", directory, engine);

        // The game opens the DLC's scene when it starts (it fails quietly without the DLC).
        var player = Path.Combine(directory, "Scripts", "PlayerController.cs");
        File.WriteAllText(player, File.ReadAllText(player).Replace(
            "    public override void Update()",
            "    public override void Start() => SceneManager.LoadScene(\"DLC/Forest/Forest.mnvt\");\n\n    public override void Update()",
            StringComparison.Ordinal));

        // The DLC: a scene in Assets/DLC/Forest, and its code in a class library that builds on the game's.
        var assets = Path.Combine(directory, "Assets", "DLC", "Forest");
        Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(assets, "Forest.mnvt"), """
            { "schemaVersion": 2, "name": "Forest", "gameObjects": [
              { "id": "f0000000-0000-0000-0000-000000000001", "name": "Greeter", "active": true, "parent": null,
                "transform": { "localPosition": { "x": 0.0, "y": 0.0 }, "localRotation": 0.0, "localScale": { "x": 1.0, "y": 1.0 } },
                "components": [ { "type": "DlcTest.Forest.ForestGreeter", "id": "f1000000-0000-0000-0000-000000000001", "enabled": true, "data": {} } ] } ] }
            """);
        var code = Path.Combine(directory, "DLC", "Forest");
        Directory.CreateDirectory(code);
        File.WriteAllText(Path.Combine(code, "Forest.Scripts.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net11.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{engine}/src/Engine/MNVTEngine.Scenes/MNVTEngine.Scenes.csproj" Private="false" />
                <ProjectReference Include="../../Scripts/DlcTest.Scripts.csproj" Private="false" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(code, "ForestGreeter.cs"), """
            namespace DlcTest.Forest;

            public sealed class ForestGreeter : MNVTEngine.Component
            {
                public override void Start() => Log.Info($"forest code runs next to {typeof(DlcTest.PlayerController).Name}");
            }
            """);
        var json = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(projectFile));
        json["content"] = new Newtonsoft.Json.Linq.JObject { ["dlcCode"] = new Newtonsoft.Json.Linq.JObject { ["Forest"] = "DLC/Forest/Forest.Scripts.csproj" } };
        File.WriteAllText(projectFile, json.ToString());
        return directory;
    }

    [Theory]
    [InlineData("jit")]
    [InlineData("aot")]
    public async Task DlcCode_LoadsIntoTheGame_AndItsComponentsRun(string backend)
    {
        Gpu.SkipWithoutVulkan14();
        var directory = NewProject();
        var options = new BuildOptions { ScriptingBackend = backend };
        var result = await new GameBuilder(NullLogger.Instance).BuildAsync(directory, options, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        var output = result.OutputDirectory!;

        var project = GameProject.Load(Directory.GetFiles(directory, "*" + GameProject.Extension).Single());
        var diagnostics = new List<BuildDiagnostic>();
        var profile = GameBuilder.ResolveProfile(project, options, diagnostics)!;
        var dlc = await DlcBuilder.BuildAsync(project, profile, "Assets/DLC/Forest", "Forest", Path.Combine(_root, "dlc"), null, separateKey: false, TestContext.Current.CancellationToken);
        Assert.False(dlc.Diagnostics.HasErrors(), string.Join("\n", dlc.Diagnostics));
        using (var package = IO.Packages.MnvtPackage.Open(dlc.PackageFile!, new IO.Packages.PackageOpenOptions(new IO.Packages.PackageKeyRing(ContentKeys.LoadOrCreate(project, [])!))))
        {
            Assert.Equal(("Forest", "Forest.Scripts.dll"), (package.Info.Properties[GameContent.DlcProperty], package.Info.Properties[GameContent.ScriptsProperty]));
            Assert.Equal(backend == "jit", package.Contains("Scripts/Forest.Scripts.dll")); // ahead-of-time games have it built in
        }

        var dlcFolder = Path.Combine(output, "DlcTest_Data", GameContent.DlcFolder);
        Directory.CreateDirectory(dlcFolder);
        File.Copy(dlc.PackageFile!, Path.Combine(dlcFolder, "Forest.mnvtpack"));

        using var game = Process.Start(new ProcessStartInfo(Path.Combine(output, "DlcTest.exe"), "--frames 30") { WorkingDirectory = output, UseShellExecute = false })!;
        Assert.True(game.WaitForExit(TimeSpan.FromMinutes(2)), "the game did not exit");
        var log = File.ReadAllText(Path.Combine(output, "Logs", "game.log"));
        Assert.True(game.ExitCode == 0, log);
        Assert.Contains("Code of DLC/Forest.mnvtpack loaded", log, StringComparison.Ordinal);
        Assert.Contains("forest code runs next to PlayerController", log, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_RegistersDlcComponents_OnlyForTheirDlc()
    {
        var source = AotBootstrap.Source([], [], new Dictionary<string, IReadOnlyList<Type>> { ["Forest"] = [typeof(Tilemap)] });
        Assert.Contains("case \"Forest\":\n                registry.Register<global::MNVTEngine.Tilemap>();\n                return true;", source, StringComparison.Ordinal);
        Assert.Contains("default:\n                return false;", source, StringComparison.Ordinal);
    }
}
