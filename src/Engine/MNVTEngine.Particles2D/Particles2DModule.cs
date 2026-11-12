using MNVTEngine.Runtime;
using MNVTEngine.Scenes;

namespace MNVTEngine.Particles2D;

/// <summary>The "particles.2d" engine module: draws particle systems in 2D projects (<see cref="ParticleScenePass2D"/>).</summary>
public sealed class Particles2DModule : IEngineModule, IRenderIntegration
{
    public string Id => "particles.2d";

    public void Register(ComponentRegistry components)
    {
    }

    public IScenePass CreatePass(RenderServices services) => new ParticleScenePass2D(services);
}
