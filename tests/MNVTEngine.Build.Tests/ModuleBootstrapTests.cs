using System.Xml.Linq;
using MNVTEngine.Modules;

namespace MNVTEngine.Build.Tests;

public sealed class ModuleBootstrapTests
{
    private static ModuleResolution Resolve(ProjectProfile profile, IReadOnlyDictionary<string, IReadOnlyList<string>> usage) =>
        ModuleResolver.Resolve(ModuleCatalog.Builtin, profile, usage: usage);

    [Fact]
    public void Source_CreatesExactlyTheResolvedModules_InStartupOrder_WithoutReflection()
    {
        var resolution = Resolve(ProjectProfile.TwoD, new Dictionary<string, IReadOnlyList<string>> { ["physics.2d"] = ["Rigidbody2D in Scenes/Main.mnvt"] });
        var source = ModuleBootstrap.Source(resolution);

        Assert.Contains("new global::MNVTEngine.World2D.Rendering.World2DModule()", source, StringComparison.Ordinal);
        Assert.Contains("new global::MNVTEngine.Physics2DModule()", source, StringComparison.Ordinal);
        Assert.True(source.IndexOf("World2DModule", StringComparison.Ordinal) < source.IndexOf("Physics2DModule", StringComparison.Ordinal)); // dependency first
        Assert.DoesNotContain("AudioModule", source, StringComparison.Ordinal);
        Assert.DoesNotContain("World3D", source, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection", source, StringComparison.Ordinal);
        Assert.Contains("_ = logger;", source, StringComparison.Ordinal); // no module takes the logger: no unused-parameter warning
        Assert.Equal(source, ModuleBootstrap.Source(Resolve(ProjectProfile.TwoD, new Dictionary<string, IReadOnlyList<string>> { ["physics.2d"] = ["Rigidbody2D in Scenes/Main.mnvt"] }))); // deterministic

        var props = ModuleBootstrap.Props(resolution, @"C:\Work\ModuleBootstrap.g.cs");
        var references = XDocument.Parse(props).Descendants("ProjectReference").Select(r => (string)r.Attribute("Include")!).ToList();
        Assert.Equal([@"$(MSBuildProjectDirectory)\..\MNVTEngine.Physics2D\MNVTEngine.Physics2D.csproj", @"$(MSBuildProjectDirectory)\..\MNVTEngine.World2D.Rendering\MNVTEngine.World2D.Rendering.csproj"], references);
        Assert.Contains(XDocument.Parse(props).Descendants("Compile"), c => (string?)c.Attribute("Remove") == "HostModules.cs");
    }

    [Fact]
    public void TheHostsDevelopmentReferences_AreEveryModuleOfTheBootstrap()
    {
        var host = XDocument.Load(EngineLocation.GameHostProject(EngineLocation.FindRoot()));
        var development = host.Descendants("ItemGroup").Single(g => (string?)g.Attribute("Condition") == "'$(MnvtBootstrap)' == ''")
            .Elements("ProjectReference").Select(r => Path.GetFileNameWithoutExtension((string)r.Attribute("Include")!)).Order(StringComparer.Ordinal);
        Assert.Equal(ModuleBootstrap.Projects(ModuleBootstrap.ImplementedModules), development);

        // Every module with components has a bootstrap entry, so a game can never resolve a module its host cannot create.
        Assert.All(ModuleCatalog.Builtin.Modules.Where(m => m.Components.Count > 0 && m.Activation != ModuleActivation.Always && m.Id != "devtools"),
            m => Assert.Contains(m.Id, ModuleBootstrap.ImplementedModules));
    }
}
