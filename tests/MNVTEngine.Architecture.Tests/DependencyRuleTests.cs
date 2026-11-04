namespace MNVTEngine.Architecture.Tests;

/// <summary>
/// Enforces docs/architecture/overview.md. Rules are written against project names that may not exist yet,
/// so they start protecting a module the moment it is added.
/// </summary>
public class DependencyRuleTests
{
    private static readonly ProjectGraph Graph = ProjectGraph.Load();

    private static readonly string[] GraphicsBackendPackages = ["Silk.NET", "Vortice"];
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

    [Fact]
    public void ScriptingLanguage_IsSelfContained()
    {
        // The compiler and VM give scripts no file, process, network or engine access by construction: they reference nothing.
        Assert.Empty(Graph["MNVTEngine.Scripting"].ProjectReferences);
        Assert.Empty(Graph["MNVTEngine.Scripting"].Packages);
    }

    [Fact]
    public void Missions_DoNotDependOnScenesOrRendering()
    {
        string[] forbidden = ["MNVTEngine.Scenes", "MNVTEngine.Graphics", "MNVTEngine.Platform", "MNVTEngine.Runtime"];
        Assert.DoesNotContain(Graph.TransitiveReferences("MNVTEngine.Scripting.Missions"), forbidden.Contains);
    }

    [Fact]
    public void Input_DependsOnlyOnMathematics() => Assert.Equal(["MNVTEngine.Mathematics"], Graph["MNVTEngine.Input"].ProjectReferences);

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

    /// <summary>The in-game debug UI and the development host that includes it unconditionally.</summary>
    private static readonly string[] DebugUiProjects = ["MNVTEngine.DebugUI", "MNVTEngine.Player"];

    [Fact]
    public void OnlyEditorAndDebugUi_UseImGui()
    {
        var violations = Graph.Projects
            .Where(p => p.Area != "Editor" && !DebugUiProjects.Contains(p.Name))
            .Where(p => Graph.TransitivePackages(p.Name).Any(pkg => EditorUiPackages.Any(ui => pkg.StartsWith(ui, StringComparison.OrdinalIgnoreCase))))
            .Select(p => p.Name)
            .ToList();
        Assert.Empty(violations);
    }

    [Fact]
    public void OnlyPlatformBackends_ReferenceSilkNetDirectly()
    {
        string[] allowed = ["MNVTEngine.Graphics.Vulkan", "MNVTEngine.Platform", "MNVTEngine.Windowing", "MNVTEngine.Audio.OpenAL"];
        var violations = Graph.Projects
            .Where(p => p.Area == "Engine" && !allowed.Contains(p.Name))
            .Where(p => p.Packages.Any(pkg => GraphicsBackendPackages.Any(b => pkg.StartsWith(b, StringComparison.OrdinalIgnoreCase))))
            .Select(p => p.Name)
            .ToList();
        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("MNVTEngine.Graphics", "MNVTEngine.Platform")]
    [InlineData("MNVTEngine.Graphics", "MNVTEngine.Graphics.Vulkan")]
    [InlineData("MNVTEngine.Graphics.Vulkan", "MNVTEngine.Platform")]
    [InlineData("MNVTEngine.Runtime", "MNVTEngine.Graphics.Vulkan")]
    [InlineData("MNVTEngine.Assets", "MNVTEngine.Graphics.Vulkan")]
    [InlineData("MNVTEngine.Scripting", "MNVTEngine.Graphics.Vulkan")]
    [InlineData("MNVTEngine.Scripting.VM", "MNVTEngine.Graphics.Vulkan")]
    [InlineData("MNVTEngine.Physics2D", "MNVTEngine.Graphics.Vulkan")]
    [InlineData("MNVTEngine.Scenes", "MNVTEngine.Graphics.Vulkan")]
    [InlineData("MNVTEngine.Platform", "MNVTEngine.Graphics")]
    [InlineData("MNVTEngine.Scenes", "MNVTEngine.Graphics")]
    [InlineData("MNVTEngine.Scenes", "MNVTEngine.Assets")]
    [InlineData("MNVTEngine.Assets", "MNVTEngine.Scenes")]
    [InlineData("MNVTEngine.GameAssemblies", "MNVTEngine.Graphics")]
    [InlineData("MNVTEngine.GameAssemblies", "MNVTEngine.Platform")]
    [InlineData("MNVTEngine.Runtime", "MNVTEngine.GameAssemblies")]
    [InlineData("MNVTEngine.Assets", "MNVTEngine.Platform")]
    [InlineData("MNVTEngine.Scenes", "MNVTEngine.Platform")]
    [InlineData("MNVTEngine.ECS", "MNVTEngine.Scenes")]
    [InlineData("MNVTEngine.Input", "MNVTEngine.Platform")]
    [InlineData("MNVTEngine.Mathematics", "MNVTEngine.Graphics")]
    [InlineData("MNVTEngine.Scripting", "MNVTEngine.Graphics")]
    [InlineData("MNVTEngine.Scripting.VM", "MNVTEngine.Graphics")]
    [InlineData("MNVTEngine.Scripting.Compiler", "MNVTEngine.Graphics")]
    [InlineData("MNVTEngine.Scripting.Missions", "MNVTEngine.Graphics")]
    [InlineData("MNVTEngine.Physics2D", "MNVTEngine.Graphics")]
    [InlineData("MNVTEngine.Physics2D", "MNVTEngine.Platform")]
    [InlineData("MNVTEngine.Physics2D", "MNVTEngine.Assets")]
    [InlineData("MNVTEngine.Scenes", "MNVTEngine.Physics2D")]
    [InlineData("MNVTEngine.Audio", "MNVTEngine.Audio.OpenAL")]
    [InlineData("MNVTEngine.Audio", "MNVTEngine.Graphics")]
    [InlineData("MNVTEngine.Runtime", "MNVTEngine.Audio.OpenAL")]
    [InlineData("MNVTEngine.Scenes", "MNVTEngine.Audio")]
    public void ForbiddenDependency(string project, string forbidden)
    {
        if (!Graph.Contains(project))
        {
            return; // Module not created yet; the rule activates when it is.
        }

        Assert.DoesNotContain(forbidden, Graph.TransitiveReferences(project));
    }

    [Fact]
    public void EditorServices_StayFreeOfImGui() =>
        Assert.DoesNotContain(Graph.TransitivePackages("MNVTEngine.Editor.Services"), p => EditorUiPackages.Any(ui => p.StartsWith(ui, StringComparison.OrdinalIgnoreCase)));

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

    [Fact]
    public void DebugUi_IsReferencedOnlyByDevelopmentHosts_AndByTheGameHostOnlyInDevelopmentBuilds()
    {
        var unconditional = Graph.Projects.Where(p => p.ProjectReferences.Contains("MNVTEngine.DebugUI")).Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["MNVTEngine.Editor", "MNVTEngine.Player"], unconditional);
        Assert.Contains("MNVTEngine.DebugUI", Graph["MNVTEngine.GameHost"].DevelopmentReferences);
        Assert.DoesNotContain("MNVTEngine.DebugUI", Graph.TransitiveReferences("MNVTEngine.Runtime"));
    }

    [Fact]
    public void ExportedGameHost_ShipsNoDevelopmentTooling()
    {
        Assert.True(Graph.Contains("MNVTEngine.GameHost"));
        Assert.DoesNotContain(Graph.TransitiveReferences("MNVTEngine.GameHost"), r => Graph.Contains(r) && Graph[r].Area is "Editor" or "Tooling");
        Assert.DoesNotContain(Graph.TransitivePackages("MNVTEngine.GameHost"), p =>
            p.StartsWith("Microsoft.Build", StringComparison.OrdinalIgnoreCase) || p.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase)
            || EditorUiPackages.Any(ui => p.StartsWith(ui, StringComparison.OrdinalIgnoreCase)));
    }
}
