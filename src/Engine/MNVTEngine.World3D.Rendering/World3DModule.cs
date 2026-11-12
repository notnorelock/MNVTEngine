using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Assets;
using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Materials;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;

namespace MNVTEngine.World3D.Rendering;

/// <summary>
/// The "world.3d" engine module: 3D components, the mesh, shadow and sky shaders, the model cache, the 3D pass (with
/// other modules' mesh sources, such as terrain) and the model animation service of every scene.
/// </summary>
public sealed class World3DModule : IEngineModule, IResourceIntegration, IRenderIntegration, ISceneIntegration
{
    private readonly Dictionary<AssetDatabase, ModelAnimationProvider> _animations = [];
    private AssetDatabase? _assets;

    public string Id => "world.3d";

    public IReadOnlyList<ShaderSource> BuiltinShaders { get; } =
    [
        MaterialShaders.Standard, MaterialShaders.Unlit, MaterialShaders.ShadowCaster, MaterialShaders.StandardSkinned, MaterialShaders.UnlitSkinned,
        MaterialShaders.ShadowCasterSkinned, MaterialShaders.StandardInstanced, MaterialShaders.UnlitInstanced, MaterialShaders.Sky,
    ];

    public void Register(ComponentRegistry components) => World3DComponents.Register(components);

    public void RegisterResources(ResourceCache resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        _assets = resources.Assets; // scenes of this host read model animations from the same assets
        if (resources.TryGetExtension<ModelCache>(out _))
        {
            return; // this cache already has world.3d's resources
        }

        resources.RegisterBuiltinShader(MaterialDefinition.BuiltinStandard, MaterialShaders.Standard, MaterialShaders.StandardProperties);
        resources.RegisterBuiltinShader(MaterialDefinition.BuiltinUnlit, MaterialShaders.Unlit, MaterialShaders.UnlitProperties);
        resources.RegisterBuiltinShader(MaterialShaders.ShadowCasterId, MaterialShaders.ShadowCaster);
        resources.RegisterBuiltinShader(MaterialShaders.StandardSkinnedId, MaterialShaders.StandardSkinned);
        resources.RegisterBuiltinShader(MaterialShaders.UnlitSkinnedId, MaterialShaders.UnlitSkinned);
        resources.RegisterBuiltinShader(MaterialShaders.ShadowCasterSkinnedId, MaterialShaders.ShadowCasterSkinned);
        resources.RegisterBuiltinShader(MaterialShaders.StandardInstancedId, MaterialShaders.StandardInstanced);
        resources.RegisterBuiltinShader(MaterialShaders.UnlitInstancedId, MaterialShaders.UnlitInstanced);
        resources.RegisterBuiltinShader(MaterialShaders.SkyId, MaterialShaders.Sky);
        resources.AddExtension(new ModelCache(resources));
    }

    public IScenePass CreatePass(RenderServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new MeshScenePass(services, [.. services.Modules.OfType<IMeshItemIntegration>().Select(m => m.CreateItemSource(services))]);
    }

    public void SceneLoaded(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (_assets is not { } assets)
        {
            return; // no resources registered yet: the host draws nothing 3D, so model animations have no source
        }

        if (!_animations.TryGetValue(assets, out var provider))
        {
            _animations[assets] = provider = new ModelAnimationProvider(assets, NullLogger.Instance);
        }

        scene.ModelAnimations = provider;
        scene.MeshData = provider;
    }

    public void Shutdown()
    {
        foreach (var provider in _animations.Values)
        {
            provider.Dispose();
        }

        _animations.Clear();
    }
}
