using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.CLI;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Build.Tests;

/// <summary>`mnvt modules list | validate | graph` and module errors in project validation.</summary>
public sealed class ModuleCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-modules-cli-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static async Task<(int Exit, string Output)> Run(params string[] args)
    {
        var output = new StringWriter();
        var exit = await Cli.RunAsync(args, output, NullLoggerFactory.Instance, TestContext.Current.CancellationToken);
        return (exit, output.ToString());
    }

    private async Task<string> NewProject(string name, string profile)
    {
        Assert.Equal(Cli.Ok, (await Run("new", name, "--profile", profile, "--path", _root)).Exit);
        return Path.Combine(_root, name);
    }

    [Fact]
    public async Task List_ShowsTheCatalog_AndAProjectsStatus()
    {
        var (exit, catalog) = await Run("modules", "list");
        Assert.Equal(Cli.Ok, exit);
        Assert.Contains("Engine modules (14)", catalog, StringComparison.Ordinal);
        Assert.Matches(@"physics\.2d\s+2D\s+default\s+world\.2d", catalog);

        var (_, flat) = await Run("modules", "list", await NewProject("Flat", "2d"));
        Assert.Matches(@"world\.3d .*off: not available in 2D", flat);
        Assert.Matches(@"world\.2d .*on: default for 2D", flat);
    }

    [Fact]
    public async Task Graph_ListsStartupOrder_AndExcludedModules()
    {
        var (exit, graph) = await Run("modules", "graph", await NewProject("Deep", "3d"));
        Assert.Equal(Cli.Ok, exit);
        Assert.Contains("Deep (3D): 11 modules, in startup order", graph, StringComparison.Ordinal);
        Assert.True(graph.IndexOf("world.3d", StringComparison.Ordinal) < graph.IndexOf("  terrain", StringComparison.Ordinal));
        Assert.Matches(@"physics\.2d\s+not available in 3D", graph);
        Assert.Contains("Dependency tree", graph, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_ReportsModuleProblems_AndFailsTheBuildValidation()
    {
        var project = await NewProject("Bad", "2d");
        Assert.Equal((Cli.Ok, true), await Run("modules", "validate", project) is var ok ? (ok.Exit, ok.Output.Contains("Modules OK.", StringComparison.Ordinal)) : default);

        var file = Path.Combine(project, "Bad.mnvtproj");
        var document = JObject.Parse(File.ReadAllText(file));
        document["modules"] = JObject.Parse("""{ "physics.3d": true, "nope": true, "graphics": false }""");
        File.WriteAllText(file, document.ToString());
        var (exit, report) = await Run("modules", "validate", project);
        Assert.Equal(Cli.Failed, exit);
        Assert.Contains("error MNVB605", report, StringComparison.Ordinal);
        Assert.Contains("error MNVB606", report, StringComparison.Ordinal);
        Assert.Contains("error MNVB607", report, StringComparison.Ordinal);
        Assert.Contains("Modules: 3 error(s).", report, StringComparison.Ordinal);

        var validation = await new GameBuilder(NullLogger.Instance).ValidateProjectAsync(project, TestContext.Current.CancellationToken);
        Assert.Equal(["MNVB605", "MNVB606", "MNVB607"], validation.Where(d => d.Severity == BuildSeverity.Error).Select(d => d.Code).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ContentOfAnotherProfile_IsReportedByValidate()
    {
        var project = await NewProject("Mixed", "3d");
        var scene = Path.Combine(project, "Assets", "Scenes", "Main.mnvt");
        var document = JObject.Parse(File.ReadAllText(scene));
        ((JArray)document["gameObjects"]![0]!["components"]!).Add(JObject.Parse("""{ "type": "MNVTEngine.Tilemap", "id": "c3000000-0000-0000-0000-000000000009", "enabled": true, "data": {} }"""));
        File.WriteAllText(scene, document.ToString());
        var (exit, report) = await Run("modules", "validate", project);
        Assert.Equal(Cli.Failed, exit);
        Assert.Contains("error MNVB608: MNVTEngine.Tilemap (module 'world.2d', not available in 3D) is used in Scenes/Main.mnvt.", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreadableProjects_AreReported_NotThrown()
    {
        File.WriteAllText(Path.Combine(_root, "Broken.mnvtproj"), "{ \"schemaVersion\": 2 }");
        Assert.Equal(Cli.Failed, (await Run("modules", "validate", _root)).Exit);
        Assert.Equal(Cli.Failed, (await Run("migrate", _root)).Exit);
    }

    [Theory]
    [InlineData("modules")]
    [InlineData("modules", "frob")]
    [InlineData("modules", "graph")]
    [InlineData("modules", "list", "a", "b")]
    public async Task BadModuleCommands_AreUsageErrors(params string[] args) => Assert.Equal(Cli.UsageError, (await Run(args)).Exit);
}
