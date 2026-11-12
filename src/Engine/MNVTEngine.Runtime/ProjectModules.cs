using MNVTEngine.Modules;
using MNVTEngine.Scenes;

namespace MNVTEngine.Runtime;

/// <summary>
/// The runtime implementations of the catalog's modules, created explicitly by id (no assembly scanning). Modules
/// without one are infrastructure the host itself provides (core, platform, graphics, assets, runtime), modules whose
/// components still live in core (world.2d, world.3d), or the debug UI, which the host adds itself.
/// </summary>
public static class EngineModules
{
    /// <summary>The module's runtime implementation; null when it has none.</summary>
    public static IEngineModule? Create(string id) => id switch
    {
        "audio" => new AudioModule(),
        "scripting" => new ScriptingModule(),
        "ui" => new UI.UIModule(),
        "physics.2d" => new Physics2DModule(),
        "physics.3d" => new Physics3DModule(),
        "terrain" => new TerrainModule(),
        _ => null,
    };

    /// <summary>Implementations of the resolved modules, in startup order, for a <see cref="ModuleHost"/>.</summary>
    public static IReadOnlyList<IEngineModule> For(ModuleResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        return [.. resolution.Modules.Select(m => Create(m.Id)).OfType<IEngineModule>()];
    }
}

/// <summary>The modules of a game project: its profile, the profile's defaults and the project's "modules" switches.</summary>
public static class ProjectModules
{
    /// <exception cref="ProfileConflictException">A project from before profiles mixes 2D and 3D content.</exception>
    public static ModuleResolution Resolve(GameProject project, ModuleCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        return ModuleResolver.Resolve(catalog ?? ModuleCatalog.Builtin, ProjectProfileMigration.Resolve(project).Profile, project.Modules);
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
