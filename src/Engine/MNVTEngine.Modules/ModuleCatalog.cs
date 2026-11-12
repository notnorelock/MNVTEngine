using System.Text.RegularExpressions;

namespace MNVTEngine.Modules;

/// <summary>The catalog contains invalid definitions; <see cref="Diagnostics"/> lists every problem.</summary>
public sealed class ModuleCatalogException(IReadOnlyList<ModuleDiagnostic> diagnostics)
    : InvalidOperationException("Invalid module catalog: " + string.Join(" ", diagnostics.Select(d => $"{d.Code}: {d.Message}")))
{
    public IReadOnlyList<ModuleDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>
/// A validated, immutable set of module definitions, ordered by id. Definitions are registered explicitly
/// (<see cref="BuiltinModules"/>); nothing is discovered by scanning assemblies.
/// </summary>
public sealed partial class ModuleCatalog
{
    private static readonly Lazy<ModuleCatalog> BuiltinCatalog = new(() => Create(BuiltinModules.All));
    private readonly Dictionary<string, ModuleDefinition> _byId;
    private readonly Dictionary<string, ModuleDefinition> _byComponent;

    private ModuleCatalog(IEnumerable<ModuleDefinition> modules)
    {
        Modules = [.. modules.OrderBy(m => m.Id, StringComparer.Ordinal)];
        _byId = Modules.ToDictionary(m => m.Id, StringComparer.Ordinal);
        _byComponent = Modules.SelectMany(m => m.Components.Select(c => (c, m))).ToDictionary(p => p.c, p => p.m, StringComparer.Ordinal);
    }

    /// <summary>The engine's modules.</summary>
    public static ModuleCatalog Builtin => BuiltinCatalog.Value;

    /// <summary>Every module, ordered by id.</summary>
    public IReadOnlyList<ModuleDefinition> Modules { get; }

    public ModuleDefinition this[string id] => _byId.TryGetValue(id, out var module) ? module : throw new KeyNotFoundException($"No module '{id}'.");

    public bool TryGet(string id, out ModuleDefinition module) => _byId.TryGetValue(id, out module!);

    /// <summary>The module whose run time an asset type needs; null for shared types (textures, materials, fonts, scenes).</summary>
    public ModuleDefinition? ModuleOfAssetType(string assetType) => Modules.FirstOrDefault(m => m.AssetTypes.Contains(assetType, StringComparer.Ordinal));

    /// <summary>The module that ships an assembly; null for assemblies of no module.</summary>
    public ModuleDefinition? ModuleOfAssembly(string assemblyName) => Modules.FirstOrDefault(m => m.RuntimeAssemblies.Contains(assemblyName, StringComparer.Ordinal));

    /// <summary>The module that provides a component id; null for game components.</summary>
    public ModuleDefinition? ModuleOfComponent(string componentType) => _byComponent.GetValueOrDefault(componentType);

    /// <exception cref="ModuleCatalogException">Any definition is invalid; nothing is half-registered.</exception>
    public static ModuleCatalog Create(IEnumerable<ModuleDefinition> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var list = modules.ToList();
        var problems = Validate(list);
        return problems.Count > 0 ? throw new ModuleCatalogException(problems) : new ModuleCatalog(list);
    }

    /// <summary>Every problem in a set of definitions (empty when they form a valid catalog).</summary>
    public static IReadOnlyList<ModuleDiagnostic> Validate(IReadOnlyList<ModuleDefinition> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var problems = new List<ModuleDiagnostic>();
        var byId = new Dictionary<string, ModuleDefinition>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            if (!byId.TryAdd(module.Id, module))
            {
                problems.Add(ModuleDiagnostic.Error(ModuleDiagnostic.DuplicateModule, $"Module '{module.Id}' is defined more than once.", module.Id));
            }
        }

        foreach (var module in byId.Values.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            ValidateDefinition(module, byId, problems);
        }

        ValidateOwnership(byId.Values, problems);
        if (problems.Count == 0 && FindCycle(byId) is { } cycle)
        {
            problems.Add(ModuleDiagnostic.Error(ModuleDiagnostic.CircularDependency, $"Circular module dependency: {string.Join(" -> ", cycle)}.", cycle[0]));
        }

        return problems;
    }

    private static void ValidateDefinition(ModuleDefinition module, Dictionary<string, ModuleDefinition> byId, List<ModuleDiagnostic> problems)
    {
        void Invalid(string message) => problems.Add(ModuleDiagnostic.Error(ModuleDiagnostic.InvalidDefinition, $"Module '{module.Id}': {message}", module.Id));

        if (!IdPattern().IsMatch(module.Id))
        {
            Invalid("the id must be lowercase letters and digits in dot-separated parts, e.g. 'physics.2d'.");
        }

        if (string.IsNullOrWhiteSpace(module.DisplayName))
        {
            Invalid("a display name is required.");
        }

        if (module.Profiles.Count == 0 || module.Profiles.Distinct().Count() != module.Profiles.Count || module.Profiles.Any(p => !Enum.IsDefined(p)))
        {
            Invalid("it must support at least one profile, each listed once.");
        }

        if (module.Activation == ModuleActivation.Always && module.ExclusiveProfile is not null)
        {
            Invalid("a module every project has must support every profile.");
        }

        var references = module.Requires.Concat(module.Optional).ToList();
        if (references.Contains(module.Id, StringComparer.Ordinal))
        {
            Invalid("it cannot depend on itself.");
        }

        if (references.Distinct(StringComparer.Ordinal).Count() != references.Count)
        {
            Invalid("each dependency is listed once, as required or optional.");
        }

        foreach (var reference in references.Distinct(StringComparer.Ordinal).Where(r => r != module.Id))
        {
            if (!byId.TryGetValue(reference, out var dependency))
            {
                problems.Add(ModuleDiagnostic.Error(ModuleDiagnostic.MissingDependency, $"Module '{module.Id}' depends on '{reference}', which is not in the catalog.", module.Id));
            }
            else if (module.Requires.Contains(reference) && module.Profiles.Any(p => !dependency.Supports(p)))
            {
                var missing = string.Join(", ", module.Profiles.Where(p => !dependency.Supports(p)).Select(p => p.DisplayName()));
                problems.Add(ModuleDiagnostic.Error(ModuleDiagnostic.DependencyProfileMismatch, $"Module '{module.Id}' requires '{reference}', which is not available in {missing}.", module.Id));
            }
        }
    }

    /// <summary>An assembly, native library, build asset or component belongs to one module only.</summary>
    private static void ValidateOwnership(IEnumerable<ModuleDefinition> modules, List<ModuleDiagnostic> problems)
    {
        foreach (var (kind, select) in new (string, Func<ModuleDefinition, IReadOnlyList<string>>)[]
        {
            ("runtime assembly", m => m.RuntimeAssemblies), ("editor assembly", m => m.EditorAssemblies),
            ("native library", m => m.NativeLibraries), ("build asset", m => m.BuildAssets), ("component", m => m.Components), ("asset type", m => m.AssetTypes),
        })
        {
            foreach (var group in modules.SelectMany(m => select(m).Select(item => (Item: item, Module: m.Id))).GroupBy(p => p.Item, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                var owners = string.Join(", ", group.Select(p => p.Module).Distinct().Order(StringComparer.Ordinal));
                problems.Add(ModuleDiagnostic.Error(ModuleDiagnostic.InvalidDefinition, $"The {kind} '{group.Key}' is claimed by more than one module entry ({owners}).", group.First().Module));
            }
        }
    }

    /// <summary>A cycle over required and optional dependencies, as module ids ending where it started; null when there is none.</summary>
    private static List<string>? FindCycle(Dictionary<string, ModuleDefinition> byId)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 visiting, 2 done
        var path = new List<string>();

        List<string>? Visit(string id)
        {
            state[id] = 1;
            path.Add(id);
            foreach (var next in byId[id].Requires.Concat(byId[id].Optional).Where(byId.ContainsKey).Order(StringComparer.Ordinal))
            {
                if (state.GetValueOrDefault(next) == 1)
                {
                    return [.. path.SkipWhile(p => p != next), next];
                }

                if (state.GetValueOrDefault(next) == 0 && Visit(next) is { } found)
                {
                    return found;
                }
            }

            path.RemoveAt(path.Count - 1);
            state[id] = 2;
            return null;
        }

        foreach (var id in byId.Keys.Order(StringComparer.Ordinal))
        {
            if (state.GetValueOrDefault(id) == 0 && Visit(id) is { } cycle)
            {
                return cycle;
            }
        }

        return null;
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(\\.[a-z0-9]+)*$")]
    private static partial Regex IdPattern();
}
