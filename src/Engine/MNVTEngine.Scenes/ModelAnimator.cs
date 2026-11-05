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
/// joints. Adding a model with animations to a scene adds an animator to its root. Clips crossfade on the base layer
/// (<see cref="CrossFade"/>), overlay layers blend on top (optionally masked to a part of the hierarchy), and root
/// motion can move the GameObject.
/// </summary>
[ComponentId("MNVTEngine.ModelAnimator")]
public sealed partial class ModelAnimator : Component
{
    private readonly List<Track> _base = []; // the playing clip last; older ones fading out (crossfades)
    private readonly SortedDictionary<int, Track> _layers = [];
    private readonly Dictionary<Transform, Accumulator> _blend = new(ReferenceEqualityComparer.Instance);
    private Dictionary<string, Transform>? _nodes;
    private Vector3? _rootAnchor;
    private float? _rootYawAnchor;
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

    /// <summary>
    /// Root motion: the movement the clips give <see cref="RootMotionNode"/> moves this GameObject instead (so a walk
    /// cycle walks), and the node itself stays in place. Looping clips keep moving forward.
    /// </summary>
    [SerializeField]
    public bool ApplyRootMotion { get; set; }

    /// <summary>The node whose movement is root motion (often the hips); empty is the model's root node.</summary>
    [SerializeField]
    public string RootMotionNode { get; set; } = "";

    /// <summary>Root motion moves the GameObject only horizontally (XZ); the node keeps its vertical motion (bobbing).</summary>
    [SerializeField]
    public bool RootMotionHorizontalOnly { get; set; } = true;

    /// <summary>
    /// Root motion also turns: the node's turn around the up axis turns this GameObject (a turn-in-place clip turns it),
    /// the node keeps facing the way it did on the first frame, and the movement follows the GameObject's new heading.
    /// Up to half a turn per clip cycle is carried across loops.
    /// </summary>
    [SerializeField]
    public bool RootMotionRotation { get; set; }

    public bool IsPlaying { get; private set; }

    /// <summary>The clip being shown on the base layer (the one faded in last), or null.</summary>
    public string? CurrentClip => Current?.Clip.Name;

    /// <summary>Seconds into the current clip.</summary>
    public float PlaybackTime => Current?.Time ?? 0f;

    /// <summary>Length of the current clip in seconds (0 without one).</summary>
    public float Duration => Current?.Clip.Duration ?? 0f;

    /// <summary>True while a crossfade is blending clips on the base layer.</summary>
    public bool IsCrossFading => _base.Count > 1;

    /// <summary>Raised when a non-looping clip reaches its end.</summary>
    public event Action<ModelAnimator>? Finished;

    /// <summary>The clips of <see cref="Model"/>.</summary>
    public IReadOnlyList<string> ClipNames => GameObject.Scene.ModelAnimations?.ClipNames(Model) ?? [];

    private Track? Current => _base.Count > 0 ? _base[^1] : null;

    public override void Start()
    {
        if (PlayOnStart)
        {
            Play();
        }
    }

    /// <summary>Restarts <see cref="Clip"/>.</summary>
    public void Play() => Play(Clip, restart: true);

    /// <summary>Plays a clip by name at once; keeps its progress if it is already playing, unless <paramref name="restart"/>.</summary>
    public void Play(string? clip, bool restart = false)
    {
        if (!restart && IsPlaying && Current is { } current && (string.IsNullOrEmpty(clip) || clip == current.Clip.Name) && !IsCrossFading)
        {
            return;
        }

        if (NewTrack(clip) is not { } track)
        {
            return;
        }

        _base.Clear();
        _base.Add(track);
        IsPlaying = true;
        Apply(0f);
    }

    /// <summary>
    /// Fades from what plays now to <paramref name="clip"/> over <paramref name="seconds"/>: both keep playing while their
    /// weights cross. Fading to the clip already playing does nothing.
    /// </summary>
    public void CrossFade(string clip, float seconds)
    {
        if (Current is { } current && current.Clip.Name == clip && current.FadeRate >= 0f)
        {
            return;
        }

        if (NewTrack(clip) is not { } track)
        {
            return;
        }

        if (_base.Count == 0 || seconds <= 0f)
        {
            Play(clip, restart: true);
            return;
        }

        var rate = 1f / seconds;
        foreach (var old in _base)
        {
            old.FadeRate = -rate;
        }

        track.Weight = 0f;
        track.FadeRate = rate;
        _base.Add(track);
        IsPlaying = true;
    }

    /// <summary>
    /// Plays <paramref name="clip"/> on an overlay layer (1 and up, applied in order) with <paramref name="weight"/> (0–1)
    /// over the layers below; <paramref name="maskRoot"/> limits it to that node and its descendants (for example the
    /// spine for an upper-body wave). A null clip removes the layer.
    /// </summary>
    public void SetLayer(int layer, string? clip, float weight = 1f, string? maskRoot = null, bool loop = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(layer, 1);
        if (clip is null)
        {
            _layers.Remove(layer);
            return;
        }

        if (!_layers.TryGetValue(layer, out var track) || track.Clip.Name != clip || track.MaskRoot != maskRoot)
        {
            if (NewTrack(clip) is not { } created)
            {
                return;
            }

            track = created;
            track.MaskRoot = maskRoot;
            track.Mask = maskRoot is null ? null : MaskOf(maskRoot);
            _layers[layer] = track;
        }

        (track.Weight, track.Loop) = (Math.Clamp(weight, 0f, 1f), loop);
    }

    /// <summary>Changes the weight of an overlay layer (0 hides it without stopping it).</summary>
    public void SetLayerWeight(int layer, float weight)
    {
        if (_layers.TryGetValue(layer, out var track))
        {
            track.Weight = Math.Clamp(weight, 0f, 1f);
        }
    }

    /// <summary>Freezes the pose; <see cref="Resume"/> continues.</summary>
    public void Pause() => IsPlaying = false;

    public void Resume() => IsPlaying = Current is not null;

    /// <summary>Stops, drops crossfades, and shows the current clip's first frame.</summary>
    public void Stop()
    {
        IsPlaying = false;
        if (Current is { } current)
        {
            _base.Clear();
            current.Time = 0f;
            current.Weight = 1f;
            current.FadeRate = 0f;
            _base.Add(current);
            Apply(0f);
        }
    }

    /// <summary>Shows the current clip (loading <see cref="Clip"/> if none) at <paramref name="time"/> seconds, without playing.</summary>
    public void Sample(float time)
    {
        if (Current is null && NewTrack(Clip) is { } track)
        {
            _base.Add(track);
        }

        if (Current is { } current)
        {
            current.Time = Math.Clamp(time, 0f, current.Clip.Duration);
            Apply(0f);
        }
    }

    public override void Update()
    {
        if (!IsPlaying || Current is null)
        {
            return;
        }

        var dt = Time.DeltaTime;
        var finished = false;
        foreach (var track in _base)
        {
            finished |= Advance(track, dt * Speed, Loop) && ReferenceEquals(track, Current);
            track.Weight = Math.Clamp(track.Weight + (track.FadeRate * dt), 0f, 1f);
        }

        _base.RemoveAll(t => t.FadeRate < 0f && t.Weight <= 0f);
        if (Current is { FadeRate: > 0f, Weight: >= 1f } faded)
        {
            faded.FadeRate = 0f;
        }

        foreach (var track in _layers.Values)
        {
            Advance(track, dt * Speed, track.Loop);
        }

        Apply(dt);
        if (finished)
        {
            IsPlaying = false;
            Finished?.Invoke(this);
        }
    }

    /// <summary>Moves a track's time; true when a non-looping track reached its end.</summary>
    private static bool Advance(Track track, float delta, bool loop)
    {
        var duration = track.Clip.Duration;
        track.PreviousTime = track.Time;
        track.Time += delta;
        track.Wraps = 0;
        if (loop && duration > 0f)
        {
            while (track.Time >= duration)
            {
                track.Time -= duration;
                track.Wraps++;
            }

            while (track.Time < 0f)
            {
                track.Time += duration;
                track.Wraps--;
            }

            return false;
        }

        if (track.Time >= duration || track.Time <= 0f)
        {
            track.Time = Math.Clamp(track.Time, 0f, duration);
            return true;
        }

        return false;
    }

    /// <summary>Samples every track, blends base tracks by weight, overlays layers, applies root motion, writes transforms.</summary>
    private void Apply(float dt)
    {
        foreach (var accumulator in _blend.Values)
        {
            accumulator.Clear();
        }

        var motionNode = ApplyRootMotion ? RootMotionTransform() : null;
        var motion = Vector3.Zero;
        var turn = 0f;
        var totalWeight = _base.Sum(t => t.Weight);
        foreach (var track in _base)
        {
            if (track.Weight <= 0f || totalWeight <= 0f)
            {
                continue;
            }

            var weight = track.Weight / totalWeight;
            track.Clip.Sample(track.Time, track.Poses);
            Accumulate(track, weight, overlay: false);
            if (motionNode is not null && dt != 0f)
            {
                var delta = RootDelta(track, motionNode);
                if (RootMotionRotation && RootYaw(track, motionNode) is var (yaw, yawDelta))
                {
                    // The clip moves the node along its own heading; the movement follows the GameObject's heading instead:
                    // the first-frame heading for a child node (the GameObject's turn is in the parent space already), the
                    // GameObject's current heading when it is the node.
                    var heading = ReferenceEquals(motionNode, Transform) ? Yaw(Transform.LocalRotation) : _rootYawAnchor ??= yaw;
                    delta = Quaternion.AngleAxis(Mathf.DeltaAngle(yaw, heading), Vector3.Up) * delta;
                    turn += yawDelta * weight;
                }

                motion += delta * weight;
            }
        }

        foreach (var layer in _layers.Values)
        {
            if (layer.Weight > 0f)
            {
                layer.Clip.Sample(layer.Time, layer.Poses);
                Accumulate(layer, layer.Weight, overlay: true);
            }
        }

        foreach (var (transform, a) in _blend)
        {
            if (a.Touched)
            {
                Write(transform, a, motionNode);
            }
        }

        if (motionNode is not null && motion != Vector3.Zero)
        {
            // The node's motion is in its parent's space; the GameObject moves by it in world space.
            var parent = motionNode.Parent is { } p ? p.LocalToWorldMatrix : Matrix4x4.Identity;
            var world = parent.MultiplyVector(motion);
            Transform.Position += RootMotionHorizontalOnly ? new Vector3(world.X, 0f, world.Z) : world;
        }

        if (turn != 0f)
        {
            Transform.Rotation = Quaternion.AngleAxis(turn, Vector3.Up) * Transform.Rotation;
        }
    }

    /// <summary>The node's heading in a track now and how far it turned this frame (degrees around +Y, across loop wraps).</summary>
    private static (float Yaw, float Delta)? RootYaw(Track track, Transform node)
    {
        var index = Array.IndexOf(track.Bound, node);
        if (index < 0 || track.Poses[index].Rotation is not { } now)
        {
            return null;
        }

        var previous = track.SampleNodeRotation(index, track.PreviousTime) ?? now;
        float Turn(Quaternion from, Quaternion to) => Mathf.DeltaAngle(Yaw(from), Yaw(to));
        if (track.Wraps == 0)
        {
            return (Yaw(now), Turn(previous, now));
        }

        var start = track.SampleNodeRotation(index, 0f) ?? now;
        var end = track.SampleNodeRotation(index, track.Clip.Duration) ?? now;
        var cycle = Turn(start, end);
        var delta = track.Wraps > 0 ? Turn(previous, end) + (cycle * (track.Wraps - 1)) + Turn(start, now) : Turn(previous, start) + (cycle * (track.Wraps + 1)) + Turn(end, now);
        return (Yaw(now), delta);
    }

    /// <summary>The heading of a rotation: the angle around +Y that turns -Z towards where it points.</summary>
    private static float Yaw(Quaternion rotation)
    {
        var forward = rotation * Vector3.Forward;
        return MathF.Atan2(-forward.X, -forward.Z) * Mathf.Rad2Deg;
    }

    /// <summary>How far the root motion node moved this frame in one track (including across loop wraps).</summary>
    private static Vector3 RootDelta(Track track, Transform node)
    {
        var index = Array.IndexOf(track.Bound, node);
        if (index < 0)
        {
            return Vector3.Zero;
        }

        var now = track.Poses[index].Position ?? node.LocalPosition;
        var previous = track.SampleNodePosition(index, track.PreviousTime) ?? now;
        if (track.Wraps == 0)
        {
            return now - previous;
        }

        var start = track.SampleNodePosition(index, 0f) ?? now;
        var end = track.SampleNodePosition(index, track.Clip.Duration) ?? now;
        var cycle = end - start;
        // Forward: from the previous time to the end, the full cycles in between, then from the start to now.
        return track.Wraps > 0 ? (end - previous) + (cycle * (track.Wraps - 1)) + (now - start) : (start - previous) + (cycle * (track.Wraps + 1)) + (now - end);
    }

    private void Accumulate(Track track, float weight, bool overlay)
    {
        for (var i = 0; i < track.Bound.Length; i++)
        {
            if (track.Bound[i] is not { } transform || (overlay && track.Mask is { } mask && !mask.Contains(transform)))
            {
                continue;
            }

            if (!_blend.TryGetValue(transform, out var a))
            {
                a = new Accumulator();
                _blend[transform] = a;
            }

            a.Add(track.Poses[i], weight, overlay);
        }
    }

    private void Write(Transform transform, Accumulator a, Transform? motionNode)
    {
        if (a.Position is { } position)
        {
            if (ReferenceEquals(transform, motionNode))
            {
                // The movement went to the GameObject: keep the node at its first-frame spot (and its bobbing if asked).
                // When the node is the GameObject itself, it keeps where the movement took it.
                var anchor = ReferenceEquals(transform, Transform) ? transform.LocalPosition : _rootAnchor ??= position;
                position = RootMotionHorizontalOnly ? new Vector3(anchor.X, position.Y, anchor.Z) : anchor;
            }

            transform.LocalPosition = position;
        }

        if (a.Rotation is { } rotation)
        {
            if (RootMotionRotation && ReferenceEquals(transform, motionNode))
            {
                // The turn went to the GameObject: the node keeps its first-frame heading (the GameObject itself keeps its own).
                var yaw = Yaw(rotation);
                var keep = ReferenceEquals(transform, Transform) ? Yaw(transform.LocalRotation) : _rootYawAnchor ??= yaw;
                rotation = Quaternion.AngleAxis(Mathf.DeltaAngle(yaw, keep), Vector3.Up) * rotation;
            }

            transform.LocalRotation = rotation;
        }

        if (a.Scale is { } scale)
        {
            transform.LocalScale = scale;
        }

        if (a.Weights is { } weights && transform.GameObject.GetComponent<MeshRenderer>() is { } renderer)
        {
            for (var w = 0; w < weights.Length; w++)
            {
                renderer.SetBlendShapeWeight(w, weights[w] / a.WeightsTotal);
            }
        }
    }

    private Transform? RootMotionTransform() =>
        string.IsNullOrEmpty(RootMotionNode) ? Transform : Nodes().GetValueOrDefault(RootMotionNode);

    private Track? NewTrack(string? clip)
    {
        if (Load(clip) is not { } loaded)
        {
            return null;
        }

        var nodes = Nodes();
        Transform?[] bound = [.. loaded.Targets.Select(name => name is null ? Transform : nodes.GetValueOrDefault(name))];
        var missing = loaded.Targets.Where((name, i) => bound[i] is null).ToList();
        if (missing.Count > 0 && !_warnedMissing)
        {
            _warnedMissing = true;
            LogMissingNodes(GameObject.Scene.Logger, GameObject.Name, loaded.Name, string.Join(", ", missing.Take(5)), missing.Count);
        }

        return new Track(loaded, bound) { Time = Speed < 0f ? loaded.Duration : 0f };
    }

    /// <summary>Descendants by name (names are unique within an imported model); found once, refreshed when a name is missing.</summary>
    private Dictionary<string, Transform> Nodes()
    {
        if (_nodes is null)
        {
            _nodes = new Dictionary<string, Transform>(StringComparer.Ordinal);
            Collect(Transform, _nodes);
        }

        return _nodes;

        static void Collect(Transform node, Dictionary<string, Transform> into)
        {
            foreach (var child in node.Children)
            {
                into.TryAdd(child.GameObject.Name, child);
                Collect(child, into);
            }
        }
    }

    private HashSet<Transform> MaskOf(string root)
    {
        var mask = new HashSet<Transform>(ReferenceEqualityComparer.Instance);
        if (Nodes().GetValueOrDefault(root) is { } start)
        {
            Add(start);
        }

        return mask;

        void Add(Transform node)
        {
            mask.Add(node);
            foreach (var child in node.Children)
            {
                Add(child);
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

    /// <summary>One clip playing: its time, weight (and fade), bound nodes and sampled poses.</summary>
    private sealed class Track(ModelClip clip, Transform?[] bound)
    {
        private NodePose[]? _probe;

        public ModelClip Clip { get; } = clip;

        public Transform?[] Bound { get; } = bound;

        public NodePose[] Poses { get; } = new NodePose[bound.Length];

        public float Time { get; set; }

        public float PreviousTime { get; set; }

        /// <summary>Loop wraps in the last advance (negative when playing backwards).</summary>
        public int Wraps { get; set; }

        public float Weight { get; set; } = 1f;

        /// <summary>Weight change per second (crossfades).</summary>
        public float FadeRate { get; set; }

        public bool Loop { get; set; } = true;

        public string? MaskRoot { get; set; }

        public HashSet<Transform>? Mask { get; set; }

        public Vector3? SampleNodePosition(int index, float time)
        {
            _probe ??= new NodePose[Bound.Length];
            Clip.Sample(time, _probe);
            return _probe[index].Position;
        }

        public Quaternion? SampleNodeRotation(int index, float time)
        {
            _probe ??= new NodePose[Bound.Length];
            Clip.Sample(time, _probe);
            return _probe[index].Rotation;
        }
    }

    /// <summary>A node's pose blended from several tracks: weighted positions and scales, sign-aligned quaternion sums.</summary>
    private sealed class Accumulator
    {
        private Vector3 _position;
        private float _positionWeight;
        private Quaternion _rotation;
        private float _rotationWeight;
        private Vector3 _scale;
        private float _scaleWeight;
        private float[]? _weights;
        private float _weightsWeight;
        private Vector3? _overlayPosition;
        private Quaternion? _overlayRotation;
        private Vector3? _overlayScale;

        public bool Touched { get; private set; }

        public Vector3? Position => _overlayPosition ?? (_positionWeight > 0f ? _position / _positionWeight : null);

        public Quaternion? Rotation => _overlayRotation ?? (_rotationWeight > 0f ? _rotation.Normalized : null);

        public Vector3? Scale => _overlayScale ?? (_scaleWeight > 0f ? _scale / _scaleWeight : null);

        /// <summary>Blend shape weight sums; divide by <see cref="WeightsTotal"/>.</summary>
        public float[]? Weights => _weights is not null && _weightsWeight > 0f ? _weights : null;

        public float WeightsTotal => _weightsWeight;

        public void Clear()
        {
            (_position, _positionWeight, _rotation, _rotationWeight, _scale, _scaleWeight, _weightsWeight) = (default, 0f, default, 0f, default, 0f, 0f);
            (_overlayPosition, _overlayRotation, _overlayScale, Touched) = (null, null, null, false);
            if (_weights is not null)
            {
                Array.Clear(_weights);
            }
        }

        public void Add(in NodePose pose, float weight, bool overlay)
        {
            Touched = true;
            if (overlay)
            {
                // An overlay layer blends over everything below it by its weight.
                if (pose.Position is { } p)
                {
                    _overlayPosition = Vector3.LerpUnclamped(Position ?? p, p, weight);
                }

                if (pose.Rotation is { } r)
                {
                    _overlayRotation = Quaternion.Slerp(Rotation ?? r, r, weight);
                }

                if (pose.Scale is { } s)
                {
                    _overlayScale = Vector3.LerpUnclamped(Scale ?? s, s, weight);
                }

                if (pose.Weights is { } layered)
                {
                    var hadBelow = _weightsWeight > 0f;
                    EnsureWeights(layered.Length);
                    for (var i = 0; i < layered.Length; i++)
                    {
                        var below = hadBelow ? _weights![i] / _weightsWeight : layered[i];
                        _weights![i] = below + ((layered[i] - below) * weight);
                    }

                    _weightsWeight = 1f;
                }

                return;
            }

            if (pose.Position is { } position)
            {
                _position += position * weight;
                _positionWeight += weight;
            }

            if (pose.Rotation is { } rotation)
            {
                // Quaternions q and -q are the same rotation: add them on the same side so blends take the short way.
                var aligned = _rotationWeight > 0f && Quaternion.Dot(_rotation, rotation) < 0f ? new Quaternion(-rotation.X, -rotation.Y, -rotation.Z, -rotation.W) : rotation;
                _rotation = new Quaternion(_rotation.X + (aligned.X * weight), _rotation.Y + (aligned.Y * weight), _rotation.Z + (aligned.Z * weight), _rotation.W + (aligned.W * weight));
                _rotationWeight += weight;
            }

            if (pose.Scale is { } scale)
            {
                _scale += scale * weight;
                _scaleWeight += weight;
            }

            if (pose.Weights is { } weights)
            {
                EnsureWeights(weights.Length);
                for (var i = 0; i < weights.Length; i++)
                {
                    _weights![i] += weights[i] * weight;
                }

                _weightsWeight += weight;
            }
        }

        private void EnsureWeights(int count)
        {
            if (_weights is null || _weights.Length < count)
            {
                var grown = new float[count];
                _weights?.CopyTo(grown, 0);
                _weights = grown;
            }
        }
    }

    [LoggerMessage(EventId = 1400, Level = LogLevel.Warning, Message = "ModelAnimator on '{GameObject}': clip '{Clip}' animates {Count} node(s) that are not under it ({Names}); they stay still")]
    private static partial void LogMissingNodes(ILogger logger, string gameObject, string clip, string names, int count);

    [LoggerMessage(EventId = 1401, Level = LogLevel.Error, Message = "ModelAnimator on '{GameObject}': the scene has no model animation provider; the host must set Scene.ModelAnimations")]
    private static partial void LogNoProvider(ILogger logger, string gameObject);
}
