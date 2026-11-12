namespace MNVTEngine.Modules;

/// <summary>An edge of the resolved graph: <see cref="Module"/> depends on <see cref="DependsOn"/>.</summary>
public sealed record ModuleDependency(string Module, string DependsOn, bool IsOptional);

/// <summary>A catalog module that is not part of the project, and why.</summary>
public sealed record ExcludedModule(ModuleDefinition Module, string Reason);

/// <summary>The modules of one project: what is enabled, in startup order, what is not, and every problem found.</summary>
public sealed class ModuleResolution
{
    internal ModuleResolution(ProjectProfile profile, IReadOnlyList<ModuleDefinition> modules, IReadOnlyDictionary<string, string> reasons,
        IReadOnlyList<ExcludedModule> excluded, IReadOnlyList<ModuleDependency> dependencies, IReadOnlyList<ModuleDiagnostic> diagnostics)
    {
        Profile = profile;
        Modules = modules;
        Reasons = reasons;
        Excluded = excluded;
        Dependencies = dependencies;
        Diagnostics = diagnostics;
    }

    public ProjectProfile Profile { get; }

    /// <summary>Enabled modules in startup order: every module after its dependencies, ties by id. Shut down in reverse.</summary>
    public IReadOnlyList<ModuleDefinition> Modules { get; }

    /// <summary>
    /// Why each enabled module is enabled: "always", "enabled in the project", "used: &lt;evidence&gt;", "required by x", or
    /// without a usage analysis "available in 2D".
    /// </summary>
    public IReadOnlyDictionary<string, string> Reasons { get; }

    public IReadOnlyList<ExcludedModule> Excluded { get; }

    /// <summary>Edges between enabled modules, ordered by module then dependency.</summary>
    public IReadOnlyList<ModuleDependency> Dependencies { get; }

    public IReadOnlyList<ModuleDiagnostic> Diagnostics { get; }

    /// <summary>No errors: the project can start with exactly <see cref="Modules"/>.</summary>
    public bool Succeeded => Diagnostics.All(d => d.Severity != ModuleSeverity.Error);

    public bool IsEnabled(string id) => Modules.Any(m => m.Id == id);

    /// <summary>
    /// Scene or prefab components of catalog modules this project does not have (MNVB608). Game components and
    /// components of enabled modules are fine.
    /// </summary>
    public IReadOnlyList<ModuleDiagnostic> CheckContent(IEnumerable<ProfileEvidence> components, ModuleCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(catalog);
        return [.. components
            .Select(c => (Component: c, Module: catalog.ModuleOfComponent(c.ComponentType)))
            .Where(p => p.Module is not null && !IsEnabled(p.Module.Id))
            .GroupBy(p => (p.Component.ComponentType, p.Module!.Id))
            .OrderBy(g => g.Key.Id, StringComparer.Ordinal).ThenBy(g => g.Key.ComponentType, StringComparer.Ordinal)
            .Select(g => ModuleDiagnostic.Error(ModuleDiagnostic.ComponentOfDisabledModule,
                $"{g.Key.ComponentType} (module '{g.Key.Id}', {Excluded.FirstOrDefault(e => e.Module.Id == g.Key.Id)?.Reason ?? "not enabled"}) is used in "
                + string.Join(", ", g.Select(p => p.Component.Source).Distinct().Order(StringComparer.Ordinal).Take(5)) + ".", g.Key.Id))];
    }
}

/// <summary>
/// Turns a profile, the project's module switches (<c>"modules": { "terrain": false }</c>) and, for builds, what the
/// project uses into the set of enabled modules. Required dependencies are added automatically (and reported); a dependency that is unavailable in the
/// profile, or that the project disables, is an error, never silently enabled or dropped.
/// </summary>
public static class ModuleResolver
{
    /// <param name="usage">
    /// What the project uses, by module id, with the evidence (a build's usage analysis): Auto modules are then included
    /// only when used. Null (development runs, the editor): every Auto module of the profile is available.
    /// </param>
    public static ModuleResolution Resolve(ModuleCatalog catalog, ProjectProfile profile, IReadOnlyDictionary<string, bool>? switches = null, IReadOnlyDictionary<string, IReadOnlyList<string>>? usage = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        switches ??= new Dictionary<string, bool>();
        var diagnostics = new List<ModuleDiagnostic>();
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
        var profileName = profile.DisplayName();

        foreach (var (id, enabled) in switches.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            if (!catalog.TryGet(id, out var module))
            {
                diagnostics.Add(ModuleDiagnostic.Error(ModuleDiagnostic.UnknownModule, $"The project names module '{id}', which does not exist. Known modules: {string.Join(", ", catalog.Modules.Select(m => m.Id))}.", id));
            }
            else if (enabled && !module.Supports(profile))
            {
                diagnostics.Add(ModuleDiagnostic.Error(ModuleDiagnostic.IncompatibleModule, $"Module '{id}' is not available in {profileName} projects; remove it from \"modules\".", id));
            }
            else if (!enabled && module.Activation == ModuleActivation.Always)
            {
                diagnostics.Add(ModuleDiagnostic.Error(ModuleDiagnostic.RequiredModuleDisabled, $"Module '{id}' is part of every project and cannot be disabled.", id));
            }
        }

        // Used modules the project cannot have: of the other profile, or forbidden by the project.
        foreach (var (id, evidence) in (usage ?? new Dictionary<string, IReadOnlyList<string>>()).Where(u => u.Value.Count > 0).OrderBy(u => u.Key, StringComparer.Ordinal))
        {
            if (!catalog.TryGet(id, out var module))
            {
                continue;
            }

            if (!module.Supports(profile))
            {
                diagnostics.Add(ModuleDiagnostic.Error(ModuleDiagnostic.ComponentOfDisabledModule, $"The project uses module '{id}', which is not available in {profileName} projects: {evidence[0]}.", id));
            }
            else if (switches.TryGetValue(id, out var allowed) && !allowed)
            {
                diagnostics.Add(ModuleDiagnostic.Error(ModuleDiagnostic.ComponentOfDisabledModule, $"The project uses module '{id}', which it disables: {evidence[0]}. Enable it, or remove what uses it.", id));
            }
        }

        foreach (var module in catalog.Modules.Where(m => m.Supports(profile)))
        {
            var switched = switches.TryGetValue(module.Id, out var on) ? on : (bool?)null;
            var used = usage is null ? null : usage.TryGetValue(module.Id, out var evidence) && evidence.Count > 0 ? evidence[0] : null;
            var reason = module.Activation switch
            {
                ModuleActivation.Always => "always",
                _ when switched is true => "enabled in the project",
                ModuleActivation.Auto when switched is not false && usage is null => $"available in {profileName}",
                ModuleActivation.Auto when switched is not false && used is not null => "used: " + used,
                _ => null,
            };
            if (reason is not null)
            {
                reasons[module.Id] = reason;
            }
        }

        // Required dependencies, transitively. The queue is ordered so reasons and messages are deterministic.
        var pending = new SortedSet<string>(reasons.Keys, StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var id = pending.Min!;
            pending.Remove(id);
            foreach (var required in catalog[id].Requires.Order(StringComparer.Ordinal))
            {
                if (reasons.ContainsKey(required))
                {
                    continue;
                }

                var dependency = catalog[required];
                if (!dependency.Supports(profile))
                {
                    diagnostics.Add(ModuleDiagnostic.Error(ModuleDiagnostic.IncompatibleModule, $"Module '{id}' requires '{required}', which is not available in {profileName} projects.", id));
                }
                else if (switches.TryGetValue(required, out var on) && !on)
                {
                    diagnostics.Add(ModuleDiagnostic.Error(ModuleDiagnostic.RequiredModuleDisabled, $"Module '{id}' requires '{required}', which the project disables; enable it or disable '{id}'.", required));
                }
                else
                {
                    reasons[required] = $"required by {id}";
                    diagnostics.Add(ModuleDiagnostic.Info(ModuleDiagnostic.EnabledAsDependency, $"Module '{required}' is enabled because '{id}' requires it.", required));
                    pending.Add(required);
                }
            }
        }

        var dependencies = reasons.Keys.Order(StringComparer.Ordinal)
            .SelectMany(id => catalog[id].Requires.Select(d => new ModuleDependency(id, d, false))
                .Concat(catalog[id].Optional.Select(d => new ModuleDependency(id, d, true)))
                .Where(e => reasons.ContainsKey(e.DependsOn))
                .OrderBy(e => e.DependsOn, StringComparer.Ordinal))
            .ToList();

        var order = StartupOrder(reasons.Keys, dependencies, diagnostics);
        var excluded = catalog.Modules.Where(m => !reasons.ContainsKey(m.Id))
            .Select(m => new ExcludedModule(m, ExclusionReason(m, profile, switches, diagnostics, usage is not null))).ToList();
        return new ModuleResolution(profile, [.. order.Select(id => catalog[id])], reasons, excluded, dependencies, diagnostics);
    }

    private static string ExclusionReason(ModuleDefinition module, ProjectProfile profile, IReadOnlyDictionary<string, bool> switches, List<ModuleDiagnostic> diagnostics, bool analysed) =>
        !module.Supports(profile) ? $"not available in {profile.DisplayName()}"
        : switches.TryGetValue(module.Id, out var on) && !on ? "disabled in the project"
        : module.Activation == ModuleActivation.OptIn ? "opt-in, not enabled"
        : module.Activation == ModuleActivation.Auto && analysed ? "not used: no components, assets or API of it in the project"
        : diagnostics.Any(d => d.Module == module.Id) ? "not resolved"
        : "not enabled";

    /// <summary>Kahn's algorithm with an ordered ready set: deterministic, and every module after its dependencies.</summary>
    private static List<string> StartupOrder(IEnumerable<string> modules, List<ModuleDependency> dependencies, List<ModuleDiagnostic> diagnostics)
    {
        var remaining = modules.ToDictionary(id => id, id => dependencies.Count(e => e.Module == id), StringComparer.Ordinal);
        var ready = new SortedSet<string>(remaining.Where(p => p.Value == 0).Select(p => p.Key), StringComparer.Ordinal);
        var order = new List<string>();
        while (ready.Count > 0)
        {
            var id = ready.Min!;
            ready.Remove(id);
            order.Add(id);
            foreach (var dependent in dependencies.Where(e => e.DependsOn == id).Select(e => e.Module))
            {
                if (--remaining[dependent] == 0)
                {
                    ready.Add(dependent);
                }
            }
        }

        if (order.Count < remaining.Count)
        {
            var stuck = remaining.Keys.Where(id => !order.Contains(id)).Order(StringComparer.Ordinal).ToList();
            diagnostics.Add(ModuleDiagnostic.Error(ModuleDiagnostic.CircularDependency, $"Modules {string.Join(", ", stuck)} depend on each other in a cycle.", stuck[0]));
        }

        return order;
    }
}
