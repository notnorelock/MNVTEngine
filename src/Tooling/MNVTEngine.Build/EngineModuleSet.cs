using MNVTEngine.Scenes;

namespace MNVTEngine.Build;

/// <summary>
/// Every engine module's implementation, for development hosts (editor, Player, <c>mnvt run</c>, build tooling) that
/// open any project. Exported games do not use this: their generated bootstrap lists only their own modules.
/// </summary>
public static class EngineModuleSet
{
    /// <summary>New instances of every module (a host owns and starts its own).</summary>
    /// <param name="createAudio">The audio backend of a host that plays sound; without one, games run silently.</param>
    public static IReadOnlyList<IEngineModule> All(Audio.AudioDeviceFactory? createAudio = null, Microsoft.Extensions.Logging.ILogger? logger = null) =>
    [
        new World2D.Rendering.World2DModule(),
        new World3D.Rendering.World3DModule(),
        new AudioModule(createAudio, logger),
        new ScriptingModule(),
        new UI.UIModule(),
        new Physics2DModule(),
        new Physics3DModule(),
        new TerrainModule(),
    ];

    /// <summary>The modules available to a profile: what the editor offers in a 2D or a 3D project.</summary>
    public static IReadOnlyList<IEngineModule> ForProfile(Modules.ProjectProfile profile) =>
        [.. All().Where(m => Modules.ModuleCatalog.Builtin.TryGet(m.Id, out var definition) && definition.Supports(profile))];

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
