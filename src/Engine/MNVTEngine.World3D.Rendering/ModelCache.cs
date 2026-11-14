using Microsoft.Extensions.Logging;
using MNVTEngine.Assets.Models;
using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Materials;
using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;
using Mesh = MNVTEngine.Graphics.Meshes.Mesh;

namespace MNVTEngine.Runtime;

/// <summary>
/// Imported models: their artifact, read on first use, with engine materials and embedded textures; and environment
/// images. The world.3d module adds it to the <see cref="ResourceCache"/>, which evicts and disposes it.
/// </summary>
public sealed partial class ModelCache(ResourceCache resources) : IResourceExtension
{
    /// <summary>Sub-asset prefix of a model's meshes: <c>asset:&lt;guid&gt;#mesh/Body</c>.</summary>
    public const string MeshSubAssetPrefix = "mesh/";

    private readonly Dictionary<Guid, LoadedModel?> _models = [];

    /// <summary>Raised with the CPU meshes of evicted models, so GPU copies can be released.</summary>
    public event Action<IReadOnlyCollection<Mesh>>? MeshesEvicted;

    public int LoadedModelCount => _models.Values.Count(m => m is not null);

    /// <summary>
    /// The model of a Model asset (its imported artifact); null, logged once, when it is not a model, was never imported
    /// or the artifact is unreadable. Reloaded when the model or one of its textures changes.
    /// </summary>
    public ModelData? GetModel(Guid guid) => Load(guid)?.Data;

    /// <summary>
    /// A model mesh by reference (<c>asset:&lt;guid&gt;#mesh/&lt;name&gt;</c>); a reference to the whole model resolves to its
    /// mesh when it has exactly one. Null when the model or mesh is missing (logged once).
    /// </summary>
    public ModelMesh? GetModelMesh(AssetRef asset)
    {
        if (asset.IsNone || Load(asset.Guid) is not { } model)
        {
            return null;
        }

        if (asset.SubAsset is null && model.Data.Meshes.Count == 1)
        {
            return model.Data.Meshes[0];
        }

        var sub = asset.SubAsset ?? "";
        var name = sub.StartsWith(MeshSubAssetPrefix, StringComparison.Ordinal) ? sub[MeshSubAssetPrefix.Length..] : sub;
        if (model.Data.FindMesh(name) is { } mesh)
        {
            return mesh;
        }

        if (model.ReportedMissing.Add(name))
        {
            LogModelMeshMissing(resources.Logger, ModelName(asset.Guid), name);
        }

        return null;
    }

    /// <summary>
    /// The material of a model material: its override (an extracted or remapped <c>.mnvtmat</c>) when it has one and it
    /// loads, else the imported material on the Standard shader. Null for an index out of range.
    /// </summary>
    public Material? GetModelMaterial(Guid model, int index)
    {
        if (Load(model) is not { } loaded || index < 0 || index >= loaded.Materials.Length)
        {
            return null;
        }

        return loaded.Data.Materials[index].Override is { } replacement ? resources.GetMaterial(new AssetRef(replacement)) ?? loaded.Materials[index] : loaded.Materials[index];
    }

    private LoadedModel? Load(Guid guid)
    {
        if (_models.TryGetValue(guid, out var cached))
        {
            return cached;
        }

        LoadedModel? loaded = null;
        var name = ModelName(guid);
        try
        {
            if (resources.Assets.GetAssetType(guid) != AssetTypes.Model)
            {
                throw new InvalidDataException($"asset:{guid} is not a model asset in this project.");
            }

            var artifact = resources.Assets.ReadArtifact(guid, ModelArtifact.Name)
                ?? throw new InvalidDataException("The model has not been imported (no artifact); open the project in the editor or fix its import errors.");
            var data = ModelArtifact.Read(artifact, name => resources.Assets.ReadArtifact(guid, name));
            loaded = new LoadedModel(data, new Texture2D?[data.Textures.Count], []);
            loaded.Materials = [.. data.Materials.Select(m => BuildModelMaterial(loaded, m, name))];
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            if (loaded is not null)
            {
                ReleaseEmbedded(loaded);
            }

            loaded = null;
            LogModelFailed(resources.Logger, ex, name);
        }

        _models.Add(guid, loaded);
        return loaded;
    }

    private string ModelName(Guid guid) => resources.Assets.Get(guid)?.Path ?? $"asset:{guid}";

    private Material BuildModelMaterial(LoadedModel model, ModelMaterial source, string modelName)
    {
        var blend = source.AlphaMode == ModelAlphaMode.Blend ? BlendMode.Alpha : BlendMode.Opaque;
        var material = new Material(resources.GetBuiltinShader(MaterialDefinition.BuiltinStandard), blend, $"{modelName}#{source.Name}")
        {
            Cull = source.DoubleSided ? CullMode.None : CullMode.Back,
            RenderQueue = blend != BlendMode.Opaque ? Material.TransparentQueue : source.AlphaMode == ModelAlphaMode.Mask ? Material.AlphaTestQueue : Material.GeometryQueue,
        };
        material.DepthWrite = !material.IsTransparent;
        material.SetColor("_BaseColor", source.BaseColor);
        material.SetFloat("_Metallic", source.Metallic);
        material.SetFloat("_Roughness", source.Roughness);
        material.SetFloat("_NormalScale", source.NormalScale);
        material.SetFloat("_OcclusionStrength", source.OcclusionStrength);
        material.SetColor("_EmissiveColor", source.Emissive);
        material.SetFloat("_AlphaCutoff", source.AlphaMode == ModelAlphaMode.Mask ? source.AlphaCutoff : 0f);
        foreach (var (role, property) in ModelMaterialSlots.Textures)
        {
            if (source.Textures.TryGetValue(role, out var index) && TextureOf(model, index) is { } texture)
            {
                material.SetTexture(property, texture);
            }

            if (source.UVSets.TryGetValue(role, out var set))
            {
                material.SetFloat(ModelMaterialSlots.UVSetProperty(property), set);
            }
        }

        MaterialProperties.ApplyDefaults(material, MaterialShaders.StandardProperties, resources.GetDefaultTexture);
        return material;
    }

    /// <summary>A model texture: the project texture it references, or its embedded image; null (default slot) when unresolved.</summary>
    private Texture2D? TextureOf(LoadedModel model, int index)
    {
        if (index < 0 || index >= model.Data.Textures.Count)
        {
            return null;
        }

        var texture = model.Data.Textures[index];
        if (texture.ProjectAsset is { } guid)
        {
            model.Dependencies.Add(guid);
            return resources.GetTexture(new AssetRef(guid));
        }

        if (texture.Embedded is { } bytes)
        {
            return model.Embedded[index] ??= resources.Device.CreateTexture(Image.Decode(bytes, texture.Name), SamplerState.LinearRepeat, texture.Name);
        }

        return null;
    }

    private static void ReleaseEmbedded(LoadedModel model)
    {
        foreach (var texture in model.Embedded)
        {
            texture?.Dispose();
        }
    }

    /// <summary>Drops models whose artifact or textures changed; their meshes are reported for GPU release.</summary>
    public void Evict(IReadOnlyCollection<Guid> changed)
    {
        EvictModels(changed);
        EvictEnvironments(changed);
    }

    public void Dispose()
    {
        DisposeModels();
        DisposeEnvironments();
    }

    private void EvictModels(IReadOnlyCollection<Guid> changed)
    {
        var meshes = new List<Mesh>();
        foreach (var (guid, loaded) in _models.Where(m => changed.Contains(m.Key) || (m.Value?.Dependencies.Any(changed.Contains) ?? false)).ToList())
        {
            if (loaded is not null)
            {
                ReleaseEmbedded(loaded);
                meshes.AddRange(loaded.Data.Meshes.SelectMany(m => m.LoadedMeshes)); // streamed detail that is not loaded has no GPU copy
            }

            _models.Remove(guid);
        }

        if (meshes.Count > 0)
        {
            MeshesEvicted?.Invoke(meshes);
        }
    }

    private void DisposeModels()
    {
        foreach (var loaded in _models.Values)
        {
            if (loaded is not null)
            {
                ReleaseEmbedded(loaded);
            }
        }

        _models.Clear();
    }

    private sealed record LoadedModel(ModelData Data, Texture2D?[] Embedded, HashSet<Guid> Dependencies)
    {
        public Material[] Materials { get; set; } = [];

        public HashSet<string> ReportedMissing { get; } = new(StringComparer.Ordinal);
    }

    [LoggerMessage(EventId = 3004, Level = LogLevel.Error, Message = "Model '{Asset}' could not be loaded; its meshes are not drawn")]
    private static partial void LogModelFailed(ILogger logger, Exception exception, string asset);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Warning, Message = "Model '{Asset}' has no mesh named '{Mesh}' (renamed or removed in the source file? A reference to a model with several meshes must name one: #mesh/<name>)")]
    private static partial void LogModelMeshMissing(ILogger logger, string asset, string mesh);
}
