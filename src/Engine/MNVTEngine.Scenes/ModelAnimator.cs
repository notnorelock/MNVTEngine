using Microsoft.Extensions.Logging;
using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;

namespace MNVTEngine;

/// <summary>The animated parts of one node at one time; null parts are not animated by the clip.</summary>
/// <param name="Weights">Blend shape weights of the node's mesh renderer (one per shape), or null.</param>
public record struct NodePose(Vector3? Position, Quaternion? Rotation, Vector3? Scale, float[]? Weights = null);

/// <summary>An animation clip of an imported model, as the runtime provides it (see <see cref="IModelAnimationProvider"/>).</summary>
public abstract class ModelClip
{
    public abstract string Name { get; }

    /// <summary>Seconds.</summary>
    public abstract float Duration { get; }

    /// <summary>The nodes the clip animates, by name; null is the model's root node (the GameObject with the animator).</summary>
    public abstract IReadOnlyList<string?> Targets { get; }

    /// <summary>Writes the pose of every target (same order as <see cref="Targets"/>) at <paramref name="time"/>.</summary>
    public abstract void Sample(float time, Span<NodePose> poses);
}

/// <summary>Implemented by the host (runtime, editor) to give scenes the animation clips of imported models.</summary>
public interface IModelAnimationProvider
{
    /// <summary>Clip names of a model, in file order; empty when it has none or cannot be loaded.</summary>
    IReadOnlyList<string> ClipNames(AssetRef model);

    /// <summary>A clip by name (null or empty: the first); null when the model or clip is missing (the host logs why).</summary>
    ModelClip? GetClip(AssetRef model, string? clip);

    /// <summary>The blend shape names of a model mesh (<c>asset:&lt;guid&gt;#mesh/&lt;name&gt;</c>), in index order.</summary>
    IReadOnlyList<string> BlendShapeNames(AssetRef mesh) => [];

    /// <summary>The default weight of each blend shape of a model mesh (what it shows when nothing sets a weight).</summary>
    IReadOnlyList<float> BlendShapeDefaults(AssetRef mesh) => [];
}

/// <summary>
/// Plays the animation clips of an imported model on the model's GameObject hierarchy: each animated node is the
/// descendant GameObject of the same name (as <c>ModelInstantiation</c> creates them; the model's root node is this
/// GameObject), and its local position, rotation and scale follow the clip. Skinned meshes under it deform with the
/// joints. Adding a model with animations to a scene adds an animator to its root.
/// </summary>
[ComponentId("MNVTEngine.ModelAnimator")]
public sealed partial class ModelAnimator : Component
{
    private ModelClip? _current;
    private Transform?[] _bound = [];
    private NodePose[] _poses = [];
    private bool _warnedMissing;

    /// <summary>The model whose clips play (usually the model this GameObject was created from).</summary>
    [SerializeField]
    [AssetType(AssetTypes.Model)]
    public AssetRef Model { get; set; }

    /// <summary>Clip played on start (and by <see cref="Play()"/>); empty is the model's first clip.</summary>
    [SerializeField]
    public string Clip { get; set; } = "";

    /// <summary>Playback speed multiplier; negative plays backwards.</summary>
    [SerializeField]
    public float Speed { get; set; } = 1f;

    [SerializeField]
    public bool Loop { get; set; } = true;

    [SerializeField]
    public bool PlayOnStart { get; set; } = true;

    public bool IsPlaying { get; private set; }

    /// <summary>The clip being shown, or null.</summary>
    public string? CurrentClip => _current?.Name;

    /// <summary>Seconds into the current clip.</summary>
    public float PlaybackTime { get; private set; }

    /// <summary>Length of the current clip in seconds (0 without one).</summary>
    public float Duration => _current?.Duration ?? 0f;

    /// <summary>Raised when a non-looping clip reaches its end.</summary>
    public event Action<ModelAnimator>? Finished;

    /// <summary>The clips of <see cref="Model"/>.</summary>
    public IReadOnlyList<string> ClipNames => GameObject.Scene.ModelAnimations?.ClipNames(Model) ?? [];

    public override void Start()
    {
        if (PlayOnStart)
        {
            Play();
        }
    }

    /// <summary>Restarts <see cref="Clip"/>.</summary>
    public void Play() => Play(Clip, restart: true);

    /// <summary>Plays a clip by name; keeps its progress if it is already playing, unless <paramref name="restart"/>.</summary>
    public void Play(string? clip, bool restart = false)
    {
        if (!restart && IsPlaying && _current is not null && (string.IsNullOrEmpty(clip) || clip == _current.Name))
        {
            return;
        }

        if (Load(clip) is not { } loaded)
        {
            return;
        }

        if (!ReferenceEquals(loaded, _current))
        {
            _current = loaded;
            Bind(loaded);
        }

        PlaybackTime = Speed < 0f ? loaded.Duration : 0f;
        IsPlaying = true;
        Apply();
    }

    /// <summary>Freezes the pose; <see cref="Resume"/> continues.</summary>
    public void Pause() => IsPlaying = false;

    public void Resume() => IsPlaying = _current is not null;

    /// <summary>Stops and shows the first frame.</summary>
    public void Stop()
    {
        IsPlaying = false;
        PlaybackTime = 0f;
        if (_current is not null)
        {
            Apply();
        }
    }

    /// <summary>Shows the current clip (loading <see cref="Clip"/> if none) at <paramref name="time"/> seconds, without playing.</summary>
    public void Sample(float time)
    {
        if (_current is null && Load(Clip) is { } loaded)
        {
            _current = loaded;
            Bind(loaded);
        }

        if (_current is not null)
        {
            PlaybackTime = Math.Clamp(time, 0f, _current.Duration);
            Apply();
        }
    }

    public override void Update()
    {
        if (!IsPlaying || _current is not { } clip)
        {
            return;
        }

        PlaybackTime += Time.DeltaTime * Speed;
        var duration = clip.Duration;
        if (Loop && duration > 0f)
        {
            PlaybackTime %= duration;
            if (PlaybackTime < 0f)
            {
                PlaybackTime += duration;
            }
        }
        else if (PlaybackTime >= duration || PlaybackTime <= 0f)
        {
            PlaybackTime = Math.Clamp(PlaybackTime, 0f, duration);
            Apply();
            IsPlaying = false;
            Finished?.Invoke(this);
            return;
        }

        Apply();
    }

    private void Apply()
    {
        var clip = _current!;
        clip.Sample(PlaybackTime, _poses);
        for (var i = 0; i < _bound.Length; i++)
        {
            if (_bound[i] is not { } transform)
            {
                continue;
            }

            ref var pose = ref _poses[i];
            if (pose.Position is { } position)
            {
                transform.LocalPosition = position;
            }

            if (pose.Rotation is { } rotation)
            {
                transform.LocalRotation = rotation;
            }

            if (pose.Scale is { } scale)
            {
                transform.LocalScale = scale;
            }

            if (pose.Weights is { } weights && transform.GameObject.GetComponent<MeshRenderer>() is { } renderer)
            {
                for (var w = 0; w < weights.Length; w++)
                {
                    renderer.SetBlendShapeWeight(w, weights[w]);
                }
            }
        }
    }

    /// <summary>Finds each target node among this GameObject's descendants (names are unique within an imported model).</summary>
    private void Bind(ModelClip clip)
    {
        var byName = new Dictionary<string, Transform>(StringComparer.Ordinal);
        Collect(Transform, byName);
        _bound = [.. clip.Targets.Select(name => name is null ? Transform : byName.GetValueOrDefault(name))];
        _poses = new NodePose[_bound.Length];
        var missing = clip.Targets.Where((name, i) => _bound[i] is null).ToList();
        if (missing.Count > 0 && !_warnedMissing)
        {
            _warnedMissing = true;
            LogMissingNodes(GameObject.Scene.Logger, GameObject.Name, clip.Name, string.Join(", ", missing.Take(5)), missing.Count);
        }

        static void Collect(Transform node, Dictionary<string, Transform> into)
        {
            foreach (var child in node.Children)
            {
                into.TryAdd(child.GameObject.Name, child);
                Collect(child, into);
            }
        }
    }

    private ModelClip? Load(string? clip)
    {
        if (Model.IsNone)
        {
            return null;
        }

        if (GameObject.Scene.ModelAnimations is not { } provider)
        {
            LogNoProvider(GameObject.Scene.Logger, GameObject.Name);
            return null;
        }

        return provider.GetClip(Model, clip);
    }

    [LoggerMessage(EventId = 1400, Level = LogLevel.Warning, Message = "ModelAnimator on '{GameObject}': clip '{Clip}' animates {Count} node(s) that are not under it ({Names}); they stay still")]
    private static partial void LogMissingNodes(ILogger logger, string gameObject, string clip, string names, int count);

    [LoggerMessage(EventId = 1401, Level = LogLevel.Error, Message = "ModelAnimator on '{GameObject}': the scene has no model animation provider; the host must set Scene.ModelAnimations")]
    private static partial void LogNoProvider(ILogger logger, string gameObject);
}
