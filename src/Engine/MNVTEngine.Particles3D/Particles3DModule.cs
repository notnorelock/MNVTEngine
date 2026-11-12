using MNVTEngine.Graphics;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;

namespace MNVTEngine.Particles3D;

/// <summary>The "particles.3d" engine module: draws particle systems in 3D projects (<see cref="ParticleScenePass3D"/>).</summary>
public sealed class Particles3DModule : IEngineModule, IRenderIntegration, IResourceIntegration
{
    public string Id => "particles.3d";

    /// <summary>The particle shader, precompiled by builds (games have no shader compiler).</summary>
    public IReadOnlyList<ShaderSource> BuiltinShaders => [ParticleVertex.Shader];

    public void Register(ComponentRegistry components)
    {
    }

    public void RegisterResources(ResourceCache resources)
    {
    }

    public IScenePass CreatePass(RenderServices services) => new ParticleScenePass3D(services);
}
