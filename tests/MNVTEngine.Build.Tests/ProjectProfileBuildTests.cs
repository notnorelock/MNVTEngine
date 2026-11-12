using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.CLI;
using MNVTEngine.Modules;
using MNVTEngine.Runtime;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Build.Tests;

/// <summary>Project profiles in templates, `mnvt new`, `mnvt migrate`, validation and packaging.</summary>
public sealed class ProjectProfileBuildTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-profiles-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static IReadOnlyList<ProjectTemplate> Templates => ProjectTemplates.List(EngineLocation.TemplatesDirectory(EngineLocation.FindRoot()));

    [Fact]
    public void EveryTemplate_HasAProfile_AndEachProfileHasAnEmptyTemplate()
    {
        Assert.All(ProjectProfiles.All, p => Assert.Contains(Templates, t => t.Id == ProjectTemplates.DefaultTemplateId(p) && t.Profile == p));
        Assert.Equal("Empty 3D Project", Templates.Single(t => t.Id == "empty-3d").Name);
    }

    [Theory]
    [InlineData("empty-2d", ProjectProfile.TwoD)]
    [InlineData("basic-2d", ProjectProfile.TwoD)]
    [InlineData("empty-3d", ProjectProfile.ThreeD)]
    public void ProjectsFromTemplates_DeclareTheTemplatesProfile_AndContentOfOnlyThatProfile(string id, ProjectProfile profile)
    {
        var project = GameProject.Load(ProjectTemplates.Create(Templates.Single(t => t.Id == id), "Game", Path.Combine(_root, id), EngineLocation.FindRoot()));
        Assert.Equal(profile, project.Profile);
        Assert.Equal(2, JObject.Parse(File.ReadAllText(project.ProjectFile)).Value<int>("schemaVersion"));
        var content = ProjectProfileMigration.Infer(project);
        Assert.Empty(profile == ProjectProfile.TwoD ? content.ThreeDEvidence : content.TwoDEvidence);
    }

    [Fact]
    public async Task New_AsksForAProfile_AndCreatesTwoDOrThreeDProjects()
    {
        var output = new StringWriter();
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(Cli.UsageError, await Cli.RunAsync(["new", "NoProfile", "--path", _root], output, NullLoggerFactory.Instance, ct));
        Assert.Contains("--profile 2d or --profile 3d", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(Cli.UsageError, await Cli.RunAsync(["new", "Hybrid", "--profile", "hybrid", "--path", _root], output, NullLoggerFactory.Instance, ct));
        Assert.Equal(Cli.UsageError, await Cli.RunAsync(["new", "Mismatch", "--profile", "3d", "--template", "basic-2d", "--path", _root], output, NullLoggerFactory.Instance, ct));
        Assert.False(Directory.Exists(Path.Combine(_root, "Mismatch")));

        Assert.Equal(Cli.Ok, await Cli.RunAsync(["new", "Flat", "--profile", "2d", "--path", _root], output, NullLoggerFactory.Instance, ct));
        Assert.Equal(Cli.Ok, await Cli.RunAsync(["new", "Deep", "--profile", "3d", "--path", _root], output, NullLoggerFactory.Instance, ct));
        Assert.Equal(Cli.Ok, await Cli.RunAsync(["new", "Game", "--template", "basic-2d", "--path", _root], output, NullLoggerFactory.Instance, ct)); // the template implies the profile
        Assert.Equal(ProjectProfile.TwoD, GameProject.Load(Path.Combine(_root, "Flat")).Profile);
        Assert.Equal(ProjectProfile.ThreeD, GameProject.Load(Path.Combine(_root, "Deep")).Profile);
        Assert.Equal(Cli.Ok, await Cli.RunAsync(["validate", Path.Combine(_root, "Deep")], output, NullLoggerFactory.Instance, ct));
    }

    [Fact]
    public async Task LegacyProjects_ValidateWithTheirInferredProfile_AndMixedOnesFailUntilMigrated()
    {
        var output = new StringWriter();
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(Cli.Ok, await Cli.RunAsync(["new", "Old", "--profile", "3d", "--path", _root], output, NullLoggerFactory.Instance, ct));
        var root = Path.Combine(_root, "Old");
        var file = Path.Combine(root, "Old.mnvtproj");
        var legacy = JObject.Parse(File.ReadAllText(file));
        legacy["schemaVersion"] = 1;
        legacy.Remove("profile");
        File.WriteAllText(file, legacy.ToString());

        var builder = new GameBuilder(NullLogger.Instance);
        var inferred = await builder.ValidateProjectAsync(root, ct);
        Assert.Contains(inferred, d => d is { Code: "MNVB110", Severity: BuildSeverity.Warning } && d.Message.Contains("3D", StringComparison.Ordinal));
        Assert.DoesNotContain(inferred, d => d.Severity == BuildSeverity.Error);

        // Add 2D content: the project mixes both and must be decided.
        var scene = Path.Combine(root, "Assets", "Scenes", "Main.mnvt");
        var document = JObject.Parse(File.ReadAllText(scene));
        ((JArray)document["gameObjects"]![0]!["components"]!).Add(JObject.Parse("""{ "type": "MNVTEngine.SpriteRenderer", "id": "c3000000-0000-0000-0000-000000000009", "enabled": true, "data": {} }"""));
        File.WriteAllText(scene, document.ToString());
        Assert.Contains(await builder.ValidateProjectAsync(root, ct), d => d is { Code: "MNVB109", Severity: BuildSeverity.Error });
        Assert.Equal(Cli.Failed, await Cli.RunAsync(["migrate", root], output, NullLoggerFactory.Instance, ct));
        Assert.Equal(1, JObject.Parse(File.ReadAllText(file)).Value<int>("schemaVersion")); // untouched

        Assert.Equal(Cli.Ok, await Cli.RunAsync(["migrate", root, "--profile", "3d"], output, NullLoggerFactory.Instance, ct));
        Assert.Equal(ProjectProfile.ThreeD, GameProject.Load(root).Profile);
        Assert.True(File.Exists(file + ".v1.bak"));
        Assert.DoesNotContain(await builder.ValidateProjectAsync(root, ct), d => d.Code is "MNVB109" or "MNVB110");
        Assert.Equal(Cli.Ok, await Cli.RunAsync(["migrate", root], output, NullLoggerFactory.Instance, ct)); // nothing left to do
        Assert.Contains("nothing to migrate", output.ToString(), StringComparison.Ordinal);
    }
}
