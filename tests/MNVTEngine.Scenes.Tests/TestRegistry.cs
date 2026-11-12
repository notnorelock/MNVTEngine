namespace MNVTEngine.Scenes.Tests;

/// <summary>The shared scene components plus the world modules' components these tests serialize.</summary>
internal static class TestRegistry
{
    public static ComponentRegistry Create() => World3DComponents.Register(World2DComponents.Register(ComponentRegistry.CreateDefault()));
}
