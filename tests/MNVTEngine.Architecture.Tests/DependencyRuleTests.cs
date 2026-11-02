namespace MNVTEngine.Architecture.Tests;

/// <summary>
/// Enforces docs/architecture/overview.md. Rules are written against project names that may not exist yet,
/// so they start protecting a module the moment it is added.
/// </summary>
public class DependencyRuleTests
{
    private static readonly ProjectGraph Graph = ProjectGraph.Load();

    private static readonly string[] GraphicsBackendPackages = ["Silk.NET"];
    private static readonly string[] EditorUiPackages = ["ImGui.NET", "Hexa.NET.ImGui"];

    [Fact]
    public void Graph_IsNotEmpty() =>
        Assert.True(Graph.Contains("MNVTEngine.Core") && Graph.Contains("MNVTEngine.Mathematics"), "Project discovery found no engine projects.");

    [Fact]
    public void NoCircularProjectReferences()
    {
        var cycle = Graph.FindCycle();
        Assert.True(cycle is null, $"Cycle: {string.Join(" -> ", cycle ?? [])}");
    }

    [Fact]
    public void AllProjectReferencesResolveToSourceProjects()
    {
        var dangling = Graph.Projects
            .SelectMany(p => p.ProjectReferences.Where(r => !Graph.Contains(r)).Select(r => $"{p.Name} -> {r}"))
            .ToList();
        Assert.Empty(dangling);
    }

    [Theory]
    [InlineData("MNVTEngine.Core")]
    [InlineData("MNVTEngine.Mathematics")]
    public void FoundationProjects_HaveNoEngineDependencies(string project)
    {
        Assert.Empty(Graph[project].ProjectReferences);
        Assert.Empty(Graph[project].Packages);
    }

    [Fact]
    public void EngineProjects_NeverDependOnEditorOrTooling()
    {
        var violations = Graph.Projects
            .Where(p => p.Area == "Engine")
            .SelectMany(p => Graph.TransitiveReferences(p.Name)
                .Where(r => Graph.Contains(r) && Graph[r].Area is "Editor" or "Tooling")
                .Select(r => $"{p.Name} -> {r}"))
            .ToList();
        Assert.Empty(violations);
    }

    [Fact]
    public void ToolingProjects_NeverDependOnEditor()
    {
        var violations = Graph.Projects
            .Where(p => p.Area == "Tooling")
            .SelectMany(p => Graph.TransitiveReferences(p.Name)
                .Where(r => Graph.Contains(r) && Graph[r].Area == "Editor")
                .Select(r => $"{p.Name} -> {r}"))
            .ToList();
        Assert.Empty(violations);
    }

    [Fact]
    public void OnlyEditorProjects_UseImGui()
    {
        var violations = Graph.Projects
            .Where(p => p.Area != "Editor")
            .Where(p => Graph.TransitivePackages(p.Name).Any(pkg => EditorUiPackages.Any(ui => pkg.StartsWith(ui, StringComparison.OrdinalIgnoreCase))))
            .Select(p => p.Name)
            .ToList();
        Assert.Empty(violations);
    }

    [Fact]
    public void OnlyPlatformBackends_ReferenceSilkNetDirectly()
    {
        string[] allowed = ["MNVTEngine.Graphics.OpenGL", "MNVTEngine.Platform", "MNVTEngine.Windowing", "MNVTEngine.Input", "MNVTEngine.Audio"];
        var violations = Graph.Projects
            .Where(p => p.Area == "Engine" && !allowed.Contains(p.Name))
            .Where(p => p.Packages.Any(pkg => GraphicsBackendPackages.Any(b => pkg.StartsWith(b, StringComparison.OrdinalIgnoreCase))))
            .Select(p => p.Name)
            .ToList();
        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("MNVTEngine.Graphics", "MNVTEngine.Graphics.OpenGL")]
    [InlineData("MNVTEngine.Mathematics", "MNVTEngine.Graphics")]
    [InlineData("MNVTEngine.Runtime", "MNVTEngine.Graphics.OpenGL")]
    [InlineData("MNVTEngine.Scripting", "MNVTEngine.Graphics")]
    [InlineData("MNVTEngine.Scripting", "MNVTEngine.Graphics.OpenGL")]
    [InlineData("MNVTEngine.Scripting.VM", "MNVTEngine.Graphics")]
    [InlineData("MNVTEngine.Scripting.VM", "MNVTEngine.Graphics.OpenGL")]
    [InlineData("MNVTEngine.Scripting.Compiler", "MNVTEngine.Graphics")]
    [InlineData("MNVTEngine.Scripting.Missions", "MNVTEngine.Graphics")]
    [InlineData("MNVTEngine.Physics2D", "MNVTEngine.Graphics")]
    public void ForbiddenDependency(string project, string forbidden)
    {
        if (!Graph.Contains(project))
        {
            return; // Module not created yet; the rule activates when it is.
        }

        Assert.DoesNotContain(forbidden, Graph.TransitiveReferences(project));
    }

    [Fact]
    public void ScriptingProjects_DoNotUseBackendPackages()
    {
        var violations = Graph.Projects
            .Where(p => p.Name.StartsWith("MNVTEngine.Scripting", StringComparison.Ordinal))
            .Where(p => Graph.TransitivePackages(p.Name).Any(pkg =>
                GraphicsBackendPackages.Concat(EditorUiPackages).Any(b => pkg.StartsWith(b, StringComparison.OrdinalIgnoreCase))))
            .Select(p => p.Name)
            .ToList();
        Assert.Empty(violations);
    }
}
