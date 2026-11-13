using MNVTEngine.Scenes;

namespace MNVTEngine.Build;

/// <summary>
/// Every engine module's implementation, for development hosts (editor, Player, <c>mnvt run</c>, build tooling) that
/// open any project. Exported games do not use this: their generated bootstrap lists only their own modules.
/// Modules are created by id, on request: a module's assembly loads only when that module is created, so a host that
/// asks for one profile's modules never loads the other profile's assemblies.
/// </summary>
public static class EngineModuleSet
{
    // One delegate per module: a lambda's body is compiled (and its module's assembly loaded) only when it is invoked.
    private static readonly Dictionary<string, Func<Audio.AudioDeviceFactory?, Microsoft.Extensions.Logging.ILogger?, IEngineModule>> Factories = new(StringComparer.Ordinal)
    {
        ["world.2d"] = (_, _) => new World2D.Rendering.World2DModule(),
        ["world.3d"] = (_, _) => new World3D.Rendering.World3DModule(),
        ["audio"] = (audio, logger) => new AudioModule(audio, logger),
        ["scripting"] = (_, _) => new ScriptingModule(),
        ["ui"] = (_, _) => new UI.UIModule(),
        ["physics.2d"] = (_, _) => new Physics2DModule(),
        ["physics.3d"] = (_, _) => new Physics3DModule(),
        ["terrain"] = (_, _) => new TerrainModule(),
        ["particles"] = (_, _) => new Particles.ParticlesModule(),
        ["particles.2d"] = (_, _) => new Particles2D.Particles2DModule(),
        ["particles.3d"] = (_, _) => new Particles3D.Particles3DModule(),
        ["browser"] = (_, _) => new Browser.BrowserModule(),
        ["browser.audio"] = (_, _) => new Browser.BrowserAudioModule(),
        ["browser.2d"] = (_, _) => new Browser2DModule(),
        ["browser.3d"] = (_, _) => new Browser3DModule(),
    };

    /// <summary>A new instance of a module by catalog id (a host owns and starts its own); null for modules without a runtime implementation.</summary>
    /// <param name="createAudio">The audio backend of a host that plays sound; without one, games run silently.</param>
    public static IEngineModule? Create(string id, Audio.AudioDeviceFactory? createAudio = null, Microsoft.Extensions.Logging.ILogger? logger = null) =>
        Factories.TryGetValue(id, out var create) ? create(createAudio, logger) : null;

    /// <summary>New instances of every module (tools that handle any project, and tests).</summary>
    public static IReadOnlyList<IEngineModule> All(Audio.AudioDeviceFactory? createAudio = null, Microsoft.Extensions.Logging.ILogger? logger = null) =>
        [.. Modules.ModuleCatalog.Builtin.Modules.Select(m => Create(m.Id, createAudio, logger)).OfType<IEngineModule>()];

    /// <summary>The modules available to a profile: what the editor offers in a 2D or a 3D project. Only these are created.</summary>
    public static IReadOnlyList<IEngineModule> ForProfile(Modules.ProjectProfile profile, Audio.AudioDeviceFactory? createAudio = null, Microsoft.Extensions.Logging.ILogger? logger = null) =>
        [.. Modules.ModuleCatalog.Builtin.Modules.Where(m => m.Supports(profile)).Select(m => Create(m.Id, createAudio, logger)).OfType<IEngineModule>()];

    /// <summary>A registry with the built-in components of every module, or only of the modules of <paramref name="profile"/>.</summary>
    public static ComponentRegistry CreateRegistry(Modules.ProjectProfile? profile = null)
    {
        var registry = ComponentRegistry.CreateDefault();
        foreach (var module in profile is { } p ? ForProfile(p) : All())
        {
            module.Register(registry);
        }

        return registry;
    }
}
