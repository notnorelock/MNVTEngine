using Microsoft.Extensions.Logging;
using MNVTEngine.Assets;
using MNVTEngine.Assets.Models;
using MNVTEngine.Serialization;

namespace MNVTEngine.Runtime;

/// <summary>
/// Gives scenes the animation clips of imported models (<see cref="Scene.ModelAnimations"/>), read from model artifacts
/// on first use and dropped when the model reimports. A missing model or clip is logged once.
/// </summary>
public sealed partial class ModelAnimationProvider : IModelAnimationProvider, IMeshDataProvider, IDisposable
{
    private readonly AssetDatabase _assets;
    private readonly ILogger _logger;
    private readonly Dictionary<Guid, ModelData?> _models = [];
    private readonly Dictionary<(Guid, string), Clip> _clips = [];
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    public ModelAnimationProvider(AssetDatabase assets, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(assets);
        _assets = assets;
        _logger = logger;
        _assets.AssetsChanged += Evict;
    }

    public IReadOnlyList<string> ClipNames(AssetRef model) => Load(model.Guid) is { } data ? [.. data.Animations.Select(a => a.Name)] : [];

    public ModelClip? GetClip(AssetRef model, string? clip)
    {
        if (Load(model.Guid) is not { } data)
        {
            return null;
        }

        var animation = string.IsNullOrEmpty(clip) ? data.Animations.FirstOrDefault() : data.Animations.Find(a => a.Name == clip);
        if (animation is null)
        {
            Report($"{model.Guid}#{clip}", () => LogClipMissing(_logger, Name(model.Guid), string.IsNullOrEmpty(clip) ? "(first)" : clip, string.Join(", ", data.Animations.Select(a => a.Name))));
            return null;
        }

        if (!_clips.TryGetValue((model.Guid, animation.Name), out var result))
        {
            result = new Clip(data, animation);
            _clips[(model.Guid, animation.Name)] = result;
        }

        return result;
    }

    public IReadOnlyList<string> BlendShapeNames(AssetRef mesh) => MeshOf(mesh) is { } m ? [.. m.BlendShapes.Select(s => s.Name)] : [];

    public IReadOnlyList<float> BlendShapeDefaults(AssetRef mesh) => MeshOf(mesh)?.DefaultBlendShapeWeights ?? [];

    /// <summary>A mesh by reference (<c>#mesh/&lt;name&gt;</c>, or a model with exactly one mesh).</summary>
    private ModelMesh? MeshOf(AssetRef mesh)
    {
        if (Load(mesh.Guid) is not { } data)
        {
            return null;
        }

        if (mesh.SubAsset is not { } sub)
        {
            return data.Meshes.Count == 1 ? data.Meshes[0] : null;
        }

        return data.FindMesh(sub.StartsWith(ResourceCache.MeshSubAssetPrefix, StringComparison.Ordinal) ? sub[ResourceCache.MeshSubAssetPrefix.Length..] : sub);
    }

    /// <summary>A renderer's triangles: its model mesh (bind pose), or its primitive.</summary>
    public MeshData? GetMeshData(MeshRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        var mesh = !renderer.Mesh.IsNone ? MeshOf(renderer.Mesh)?.Mesh : renderer.Primitive != PrimitiveMesh.None ? SceneRenderer.GetPrimitive(renderer.Primitive) : null;
        return mesh is null ? null : new MeshData(mesh.Positions, [.. mesh.IndexSpan.ToArray().Select(i => (int)i)]);
    }

    public void Dispose() => _assets.AssetsChanged -= Evict;

    private ModelData? Load(Guid guid)
    {
        if (guid == Guid.Empty)
        {
            return null;
        }

        if (_models.TryGetValue(guid, out var cached))
        {
            return cached;
        }

        ModelData? data = null;
        try
        {
            if (_assets.GetAssetType(guid) != AssetTypes.Model)
            {
                throw new InvalidDataException($"asset:{guid} is not a model in this project.");
            }

            data = ModelArtifact.Read(_assets.ReadArtifact(guid, ModelArtifact.Name)
                ?? throw new InvalidDataException("The model has not been imported (no artifact)."));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            LogModelFailed(_logger, ex, Name(guid));
        }

        _models[guid] = data;
        return data;
    }

    private string Name(Guid guid) => _assets.Get(guid)?.Path ?? $"asset:{guid}";

    private void Report(string key, Action log)
    {
        if (_reported.Add(key))
        {
            log();
        }
    }

    private void Evict(IReadOnlyCollection<Guid> changed)
    {
        foreach (var guid in changed)
        {
            _models.Remove(guid);
            foreach (var key in _clips.Keys.Where(k => k.Item1 == guid).ToList())
            {
                _clips.Remove(key);
            }
        }
    }

    /// <summary>A model animation as a scene clip: targets by node name, sampled per channel.</summary>
    private sealed class Clip : ModelClip
    {
        private readonly ModelAnimation _animation;
        private readonly int[] _targetOf; // channel -> target index
        private readonly float[]?[] _weights; // per target with a weights channel: one value per blend shape (reused)

        public Clip(ModelData model, ModelAnimation animation)
        {
            _animation = animation;
            var nodes = animation.Channels.Select(c => c.Node).Distinct().ToList();
            Targets = [.. nodes.Select(n => n == 0 ? null : model.Nodes[n].Name)];
            _targetOf = [.. animation.Channels.Select(c => nodes.IndexOf(c.Node))];
            _weights = new float[]?[nodes.Count];
            foreach (var channel in animation.Channels.Where(c => c.Path == AnimationPath.Weights))
            {
                var mesh = model.Nodes[channel.Node].Mesh;
                _weights[nodes.IndexOf(channel.Node)] = new float[mesh >= 0 ? model.Meshes[mesh].BlendShapes.Count : 0];
            }

            Duration = animation.Duration;
        }

        public override string Name => _animation.Name;

        public override float Duration { get; }

        public override IReadOnlyList<string?> Targets { get; }

        public override void Sample(float time, Span<NodePose> poses)
        {
            poses.Clear();
            for (var i = 0; i < _animation.Channels.Count; i++)
            {
                var channel = _animation.Channels[i];
                if (channel.Times.Length == 0)
                {
                    continue;
                }

                ref var pose = ref poses[_targetOf[i]];
                switch (channel.Path)
                {
                    case AnimationPath.Translation:
                        pose = pose with { Position = AnimationSampling.SampleVector3(channel, time) };
                        break;
                    case AnimationPath.Rotation:
                        pose = pose with { Rotation = AnimationSampling.SampleRotation(channel, time) };
                        break;
                    case AnimationPath.Scale:
                        pose = pose with { Scale = AnimationSampling.SampleVector3(channel, time) };
                        break;
                    case AnimationPath.Weights when _weights[_targetOf[i]] is { Length: > 0 } weights:
                        AnimationSampling.SampleWeights(channel, time, weights);
                        pose = pose with { Weights = weights };
                        break;
                }
            }
        }
    }

    [LoggerMessage(EventId = 3006, Level = LogLevel.Error, Message = "Model '{Asset}' could not be loaded for animation")]
    private static partial void LogModelFailed(ILogger logger, Exception exception, string asset);

    [LoggerMessage(EventId = 3007, Level = LogLevel.Warning, Message = "Model '{Asset}' has no animation clip {Clip}; its clips: {Clips}")]
    private static partial void LogClipMissing(ILogger logger, string asset, string clip, string clips);
}
