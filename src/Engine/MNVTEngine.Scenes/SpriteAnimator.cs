using Microsoft.Extensions.Logging;
using MNVTEngine.Serialization;

namespace MNVTEngine;

/// <summary>
/// Plays <c>.mnvtanim</c> clips on the <see cref="SpriteRenderer"/> of its GameObject by switching its texture and
/// source rectangle. Game code switches clips with <see cref="Play(AssetRef, bool)"/>; calling it every frame with the
/// clip that is already playing does nothing, so state logic can simply say what should be shown.
/// </summary>
[ComponentId("MNVTEngine.SpriteAnimator")]
public sealed partial class SpriteAnimator : Component
{
    private readonly Dictionary<Guid, SpriteAnimationClip?> _clips = [];
    private SpriteAnimationClip? _current;
    private AssetRef _currentRef;

    /// <summary>Clip played on start (and by <see cref="Play()"/>).</summary>
    [SerializeField]
    [AssetType(AssetTypes.Animation)]
    public AssetRef Clip { get; set; }

    /// <summary>Playback speed multiplier.</summary>
    [SerializeField]
    public float Speed { get; set; } = 1f;

    [SerializeField]
    public bool PlayOnStart { get; set; } = true;

    public bool IsPlaying { get; private set; }

    /// <summary>The clip being shown, or <see cref="AssetRef.None"/>.</summary>
    public AssetRef CurrentClip => _currentRef;

    /// <summary>Index into the current clip's frames.</summary>
    public int Frame { get; private set; }

    /// <summary>Seconds since the current clip started (scaled by <see cref="Speed"/>).</summary>
    public float PlaybackTime { get; private set; }

    /// <summary>Raised when a non-looping clip reaches its last frame.</summary>
    public event Action<SpriteAnimator>? Finished;

    public override void Start()
    {
        if (PlayOnStart && !Clip.IsNone)
        {
            Play(Clip);
        }
    }

    /// <summary>Restarts <see cref="Clip"/>.</summary>
    public void Play() => Play(Clip, restart: true);

    /// <summary>Shows <paramref name="clip"/>; keeps its progress if it is already playing, unless <paramref name="restart"/>.</summary>
    public void Play(AssetRef clip, bool restart = false)
    {
        if (!restart && IsPlaying && clip == _currentRef)
        {
            return;
        }

        if (Load(clip) is not { } loaded)
        {
            return;
        }

        _current = loaded;
        _currentRef = clip;
        PlaybackTime = 0f;
        Frame = 0;
        IsPlaying = true;
        Apply();
    }

    /// <summary>Freezes on the current frame; <see cref="Resume"/> continues.</summary>
    public void Pause() => IsPlaying = false;

    public void Resume() => IsPlaying = _current is not null;

    /// <summary>Stops and shows the first frame.</summary>
    public void Stop()
    {
        IsPlaying = false;
        PlaybackTime = 0f;
        Frame = 0;
        if (_current is not null)
        {
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
        Frame = clip.FrameAt(PlaybackTime);
        Apply();
        if (!clip.Loop && PlaybackTime >= clip.Duration)
        {
            IsPlaying = false;
            Finished?.Invoke(this);
        }
    }

    private void Apply()
    {
        if (GameObject.GetComponent<SpriteRenderer>() is { } renderer)
        {
            renderer.Texture = new AssetRef(_current!.Texture);
            renderer.SourceRect = _current.FrameRect(Frame);
        }
    }

    /// <summary>Parsed clip, cached per animator; null (logged once) if missing or invalid.</summary>
    private SpriteAnimationClip? Load(AssetRef clip)
    {
        if (clip.IsNone)
        {
            return null;
        }

        if (_clips.TryGetValue(clip.Guid, out var cached))
        {
            return cached;
        }

        SpriteAnimationClip? parsed = null;
        try
        {
            var reader = GameObject.Scene.TextAssets ?? throw new InvalidOperationException("The scene has no text asset reader; the host must set Scene.TextAssets.");
            parsed = SpriteAnimationClip.Parse(reader.ReadText(clip, AssetTypes.Animation), clip.ToString());
        }
        catch (Exception ex) when (ex is InvalidOperationException or SerializationException)
        {
            LogClipFailed(GameObject.Scene.Logger, ex, clip.ToString(), GameObject.Name);
        }

        _clips[clip.Guid] = parsed;
        return parsed;
    }

    [LoggerMessage(EventId = 2300, Level = LogLevel.Error, Message = "Animation clip {Clip} on '{Owner}' could not be loaded")]
    private static partial void LogClipFailed(ILogger logger, Exception exception, string clip, string owner);
}
