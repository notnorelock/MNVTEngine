using MNVTEngine.Scenes;

namespace MNVTEngine;

/// <summary>The components of the 2D world (module world.2d).</summary>
public static class World2DComponents
{
    public static ComponentRegistry Register(ComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Register<SpriteRenderer>().Register<SpriteAnimator>().Register<Tilemap>().Register<Light2D>().Register<GlobalLight2D>();
    }
}
