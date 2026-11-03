using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Build;
using MNVTEngine.Editor.Services;
using MNVTEngine.Runtime;

namespace MNVTEngine.Editor.Tests;

public sealed class ProjectToolsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-tools-").FullName;

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
    public void RecentProjects_KeepMostRecentFirst_WithoutDuplicatesOrMissingFolders()
    {
        var file = Path.Combine(_root, "recent.json");
        var recent = new RecentProjects(file);
        Assert.Empty(recent.Load());

        string Project(string name)
        {
            var path = Path.Combine(_root, name, name + ".mnvtproj");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{}");
            return path;
        }

        var a = Project("A");
        var b = Project("B");
        recent.Add(a);
        recent.Add(b);
        recent.Add(a);
        Assert.Equal([a, b], recent.Load());

        File.Delete(b);
        Assert.Equal([a], recent.Load());
        recent.Remove(a);
        Assert.Empty(recent.Load());

        File.WriteAllText(file, "not json");
        Assert.Empty(recent.Load());
    }

    [Fact]
    public async Task BuildController_BuildsAndValidatesInTheBackground()
    {
        var engine = EngineLocation.FindRoot();
        var template = ProjectTemplates.List(EngineLocation.TemplatesDirectory(engine)).Single(t => t.Id == "empty-2d");
        var project = GameProject.Load(ProjectTemplates.Create(template, "ToolGame", Path.Combine(_root, "ToolGame"), engine));
        var build = new BuildController(project, NullLoggerFactory.Instance);
        Assert.Equal("release", build.Profile);
        Assert.False(build.RunLastBuild()); // nothing built yet

        build.StartValidation();
        Assert.True(build.IsBusy);
        Assert.Throws<InvalidOperationException>(() => build.StartBuild(runWhenDone: false));
        await WaitAsync(build);
        Assert.DoesNotContain(build.LastValidation!, d => d.Severity == BuildSeverity.Error);

        build.Profile = "debug";
        build.StartBuild(runWhenDone: false);
        Assert.StartsWith("Building", build.Status, StringComparison.Ordinal);
        await WaitAsync(build);
        Assert.True(build.LastBuild!.Succeeded, string.Join("\n", build.LastBuild.Diagnostics));
        Assert.Equal(Path.Combine(project.RootDirectory, "Builds", "debug"), build.OutputDirectory);
        Assert.True(File.Exists(Path.Combine(build.OutputDirectory, "ToolGame.exe")));
        Assert.Null(build.Status);
    }

    private static async Task WaitAsync(BuildController build)
    {
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (!build.Update())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.False(build.IsBusy);
    }
}
