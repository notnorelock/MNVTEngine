using MNVTEngine.Scenes;

namespace MNVTEngine.Runtime;

/// <summary>Every built-in component type (scene basics, scripting, physics). Hosts start from this registry and add game components.</summary>
public static class EngineComponents
{
    public static ComponentRegistry CreateRegistry() =>
        Physics2DComponents.Register(ScriptingComponents.Register(ComponentRegistry.CreateDefault()));
}
