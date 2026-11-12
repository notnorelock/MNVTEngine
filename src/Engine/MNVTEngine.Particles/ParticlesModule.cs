using MNVTEngine.Scenes;

namespace MNVTEngine.Particles;

/// <summary>The "particles" engine module: the particle system component (simulation only; particles.2d and particles.3d draw).</summary>
public sealed class ParticlesModule : IEngineModule
{
    public string Id => "particles";

    public void Register(ComponentRegistry components) => ParticleComponents.Register(components);
}

public static class ParticleComponents
{
    public static ComponentRegistry Register(ComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Register<ParticleSystem>();
    }
}
