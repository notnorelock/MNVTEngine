using MNVTEngine.Modules;

namespace MNVTEngine.Architecture.Tests;

/// <summary>
/// The module catalog (MNVTEngine.Modules) against the real project graph: every engine assembly belongs to one module,
/// and an assembly references only assemblies of its own module and of modules it requires (transitively). 2D-only and
/// 3D-only modules never reach each other.
/// </summary>
public class ModuleBoundaryTests
{
    private static readonly ProjectGraph Graph = ProjectGraph.Load();
    private static readonly ModuleCatalog Catalog = ModuleCatalog.Builtin;

    // Engine projects that are not part of a module: the game executable, which hosts the modules, and model import (build tooling).
    private static readonly string[] NotInAModule = ["MNVTEngine.GameHost", "MNVTEngine.Assets.Models"];

    /// <summary>
    /// References that cross module boundaries today. Each must be removed, not added to: the test fails for a new
    /// crossing and for an entry that no longer exists. Phase 3 (build-time module isolation) removes these.
    /// </summary>
    private static readonly (string From, string To)[] KnownCouplings =
    [
        ("MNVTEngine.Assets", "MNVTEngine.Scripting.Missions"), // mission compilation in the asset pipeline (scripting is optional for assets)
        ("MNVTEngine.Assets", "MNVTEngine.Terrain"),            // the terrain importer lives in Assets
        ("MNVTEngine.Runtime", "MNVTEngine.Audio"),
        ("MNVTEngine.Runtime", "MNVTEngine.Physics2D"),
        ("MNVTEngine.Runtime", "MNVTEngine.Physics3D"),
        ("MNVTEngine.Runtime", "MNVTEngine.Scripting.Bindings"),
        ("MNVTEngine.Runtime", "MNVTEngine.Terrain.Components"),
        ("MNVTEngine.Runtime", "MNVTEngine.UI"),
    ];

    private static Dictionary<string, ModuleDefinition> Owners() =>
        Catalog.Modules.SelectMany(m => m.RuntimeAssemblies.Concat(m.EditorAssemblies).Select(a => (a, m))).ToDictionary(p => p.a, p => p.m, StringComparer.Ordinal);

    private static HashSet<string> RequiredClosure(string module)
    {
        var closure = new HashSet<string>(StringComparer.Ordinal) { module };
        var stack = new Stack<string>([module]);
        while (stack.Count > 0)
        {
            foreach (var next in Catalog[stack.Pop()].Requires.Where(closure.Add))
            {
                stack.Push(next);
            }
        }

        return closure;
    }

    [Fact]
    public void EveryEngineProject_BelongsToExactlyOneModule()
    {
        var owners = Owners();
        var unowned = Graph.Projects.Where(p => p.Area == "Engine" && !NotInAModule.Contains(p.Name) && !owners.ContainsKey(p.Name)).Select(p => p.Name);
        Assert.Empty(unowned);
        var missing = owners.Keys.Where(a => a.StartsWith("MNVTEngine.", StringComparison.Ordinal) && !Graph.Contains(a));
        Assert.Empty(missing); // the catalog names projects that exist
    }

    [Fact]
    public void Assemblies_ReferenceOnlyTheirModuleAndRequiredModules_ApartFromKnownCouplings()
    {
        var owners = Owners();
        var crossings = new List<(string From, string To)>();
        foreach (var (assembly, module) in owners.Where(o => Graph.Contains(o.Key)))
        {
            var allowed = RequiredClosure(module.Id);
            crossings.AddRange(Graph[assembly].ProjectReferences
                .Where(r => owners.TryGetValue(r, out var target) && !allowed.Contains(target.Id))
                .Select(r => (assembly, r)));
        }

        Assert.Empty(crossings.Except(KnownCouplings));
        Assert.Empty(KnownCouplings.Except(crossings)); // fixed couplings must leave the list
    }

    [Fact]
    public void TwoDAndThreeDModules_NeverReachEachOther()
    {
        var owners = Owners();
        foreach (var (assembly, module) in owners.Where(o => Graph.Contains(o.Key) && o.Value.ExclusiveProfile is not null))
        {
            var reached = Graph.TransitiveReferences(assembly).Where(owners.ContainsKey).Select(r => owners[r]);
            Assert.DoesNotContain(reached, target => target.ExclusiveProfile is { } other && other != module.ExclusiveProfile);
        }

        foreach (var module in Catalog.Modules.Where(m => m.ExclusiveProfile is not null))
        {
            Assert.All(RequiredClosure(module.Id), id => Assert.True(Catalog[id].Supports(module.ExclusiveProfile!.Value), $"{module.Id} requires {id}"));
        }
    }

    [Fact]
    public void SharedModules_DoNotRequireProfileModules() =>
        Assert.All(Catalog.Modules.Where(m => m.ExclusiveProfile is null), m =>
            Assert.DoesNotContain(RequiredClosure(m.Id), id => Catalog[id].ExclusiveProfile is not null));
}
