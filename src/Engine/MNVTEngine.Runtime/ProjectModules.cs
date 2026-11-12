using MNVTEngine.Modules;
using MNVTEngine.Scenes;

namespace MNVTEngine.Runtime;

/// <summary>The modules of a game project: its profile, the profile's defaults and the project's "modules" switches.</summary>
public static class ProjectModules
{
    /// <exception cref="ProfileConflictException">A project from before profiles mixes 2D and 3D content.</exception>
    public static ModuleResolution Resolve(GameProject project, ModuleCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        return ModuleResolver.Resolve(catalog ?? ModuleCatalog.Builtin, ProjectProfileMigration.Resolve(project).Profile, project.Modules);
    }

    /// <summary>
    /// The implementations of the resolved modules, in startup order, taken from the ones a host was built with.
    /// A resolved module that provides components but is not in the host is MNVB611 (the host was built for other modules);
    /// infrastructure (core) registers its components itself.
    /// </summary>
    public static (IReadOnlyList<IEngineModule> Modules, IReadOnlyList<ModuleDiagnostic> Missing) Implementations(ModuleResolution resolution, IReadOnlyList<IEngineModule> available)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(available);
        var byId = available.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var modules = new List<IEngineModule>();
        var missing = new List<ModuleDiagnostic>();
        foreach (var module in resolution.Modules)
        {
            if (byId.TryGetValue(module.Id, out var implementation))
            {
                modules.Add(implementation);
            }
            else if (module.Components.Count > 0 && module.Activation != ModuleActivation.Always)
            {
                missing.Add(ModuleDiagnostic.Error(ModuleDiagnostic.ModuleNotInHost, $"Module '{module.Id}' is part of the project but not built into this executable; rebuild the game.", module.Id));
            }
        }

        return (modules, missing);
    }

    /// <summary>Resolution problems plus scene and prefab components of modules the project does not have (MNVB608).</summary>
    /// <exception cref="ProfileConflictException">A project from before profiles mixes 2D and 3D content.</exception>
    public static (ModuleResolution Resolution, IReadOnlyList<ModuleDiagnostic> Diagnostics) Validate(GameProject project, ModuleCatalog? catalog = null)
    {
        catalog ??= ModuleCatalog.Builtin;
        var resolution = Resolve(project, catalog);
        return (resolution, [.. resolution.Diagnostics, .. resolution.CheckContent(ProjectProfileMigration.Components(project), catalog)]);
    }
}
