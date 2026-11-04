using Microsoft.Extensions.Logging;
using MNVTEngine.Audio;
using MNVTEngine.Core.Timing;
using MNVTEngine.ECS;
using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;

namespace MNVTEngine;

/// <summary>
/// Plays a clip. 2D by default; <see cref="Spatial"/> sources are attenuated by their distance to the
/// <see cref="AudioListener"/> (or the main camera) and follow their GameObject. Without an audio system in the scene
/// (the editor's edit mode, tests without one) playback calls do nothing.
/// </summary>
[ComponentId("MNVTEngine.AudioSource")]
public sealed class AudioSource : Component
{
    private IAudioVoice? _voice;
    private float _volume = 1f;
    private float _pitch = 1f;
    private bool _loop;

    [SerializeField]
    [AssetType(AssetTypes.Audio)]
    public AssetRef Clip { get; set; }

    /// <summary>0 = silent, 1 = as recorded.</summary>
    [SerializeField]
    public float Volume { get => _volume; set { _volume = Math.Max(0f, value); Apply(); } }

    /// <summary>Playback rate: 2 = an octave up and twice as fast.</summary>
    [SerializeField]
    public float Pitch { get => _pitch; set { _pitch = Math.Clamp(value, 0.1f, 4f); Apply(); } }

    [SerializeField]
    public bool Loop { get => _loop; set { _loop = value; Apply(); } }

    [SerializeField]
    public bool PlayOnStart { get; set; }

    /// <summary>Mixer group this source plays through (see <see cref="AudioMixer"/>); empty means Master.</summary>
    [SerializeField]
    public string Group { get; set; } = "";

    /// <summary>Attenuate by distance to the listener. OpenAL only spatialises mono clips; stereo plays unattenuated.</summary>
    [SerializeField]
    public bool Spatial { get; set; }

    /// <summary>Full volume up to this distance (world units).</summary>
    [SerializeField]
    public float ReferenceDistance { get; set; } = 2f;

    /// <summary>Attenuation stops at this distance.</summary>
    [SerializeField]
    public float MaxDistance { get; set; } = 30f;

    public bool IsPlaying => _voice?.State == VoiceState.Playing;

    public bool IsPaused => _voice?.State == VoiceState.Paused;

    public void Play()
    {
        if (System is not { } system || system.GetBuffer(Clip) is not { } buffer)
        {
            return;
        }

        _voice ??= system.CreateVoice();
        Apply();
        _voice.Position = (Vector2)Transform.Position;
        _voice.Play(buffer);
    }

    public void Pause() => _voice?.Pause();

    public void Resume() => _voice?.Resume();

    public void Stop() => _voice?.Stop();

    /// <summary>Fire-and-forget sound (footsteps, hits) that may overlap this source's own playback.</summary>
    public void PlayOneShot(AssetRef clip, float volumeScale = 1f) =>
        System?.PlayOneShot(clip, _volume * volumeScale, Group, _pitch, Spatial ? (Vector2)Transform.Position : null, ReferenceDistance, MaxDistance);

    public override void Start()
    {
        if (PlayOnStart)
        {
            Play();
        }
    }

    public override void OnDisable() => _voice?.Stop();

    public override void OnDestroy()
    {
        if (_voice is not null)
        {
            System?.Release(_voice);
            _voice = null;
        }
    }

    /// <summary>Keeps spatial voices at the GameObject's position and applies the mixer (called each frame).</summary>
    internal void Follow(AudioSystem system)
    {
        if (_voice is null)
        {
            return;
        }

        _voice.Volume = _volume * system.Mix(Group);
        if (Spatial)
        {
            _voice.Position = (Vector2)Transform.Position;
        }
    }

    private AudioSystem? System => GameObject is { } owner ? AudioSystem.For(owner.Scene) : null;

    private void Apply()
    {
        if (_voice is null)
        {
            return;
        }

        _voice.Volume = _volume * (System?.Mix(Group) ?? 1f);
        _voice.Pitch = _pitch;
        _voice.Loop = _loop;
        _voice.Spatial = Spatial;
        _voice.ReferenceDistance = ReferenceDistance;
        _voice.MaxDistance = MaxDistance;
    }
}

/// <summary>Where the player "hears" from. Without one, the main camera is used.</summary>
[ComponentId("MNVTEngine.AudioListener")]
public sealed class AudioListener : Component;

public static class AudioComponents
{
    public static Scenes.ComponentRegistry Register(Scenes.ComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Register<AudioSource>().Register<AudioListener>().Register<AudioMixer>();
    }
}

/// <summary>
/// Audio for one scene, attached by the host with its device. Decodes and uploads clips on first use (cached until the
/// scene unloads), owns every voice, moves the listener and spatial sources each frame, recycles finished one-shots,
/// and releases everything when the scene unloads. The device itself belongs to the host.
/// </summary>
public sealed partial class AudioSystem : GameSystem
{
    private readonly Scene _scene;
    private readonly Func<AssetRef, byte[]> _readClip;
    private readonly Dictionary<Guid, IAudioBuffer?> _buffers = [];
    private readonly List<IAudioVoice> _voices = [];
    private readonly List<(IAudioVoice Voice, float Volume, string Group)> _oneShots = [];
    private AudioMixer? _mixer;

    private AudioSystem(Scene scene, IAudioDevice device, Func<AssetRef, byte[]> readClip)
    {
        _scene = scene;
        Device = device;
        _readClip = readClip;
        scene.Unloaded += OnUnloaded;
    }

    public IAudioDevice Device { get; }

    /// <summary>Voices alive in this scene (sources plus one-shots still playing).</summary>
    public int VoiceCount => _voices.Count;

    /// <summary>Decoded PCM held by this scene's audio buffers.</summary>
    public long BufferBytes { get; private set; }

    /// <summary>Clips uploaded (or attempted) by this scene.</summary>
    public int ClipCount => _buffers.Count;

    /// <summary>Clips that could not be loaded (they never play).</summary>
    public int FailedClipCount => _buffers.Values.Count(b => b is null);

    /// <param name="readClip">Bytes of an audio asset (from the project folder or the game's content archive).</param>
    /// <exception cref="InvalidOperationException">The scene already has an audio system.</exception>
    public static AudioSystem Attach(Scene scene, IAudioDevice device, Func<AssetRef, byte[]> readClip)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(readClip);
        if (For(scene) is not null)
        {
            throw new InvalidOperationException("The scene already has an audio system.");
        }

        var system = new AudioSystem(scene, device, readClip);
        scene.Systems.Add(system);
        return system;
    }

    public static AudioSystem? For(Scene scene) => scene?.Systems.Get<AudioSystem>();

    public override void Update(World world, FrameTime time)
    {
        Device.Update(time.UnscaledDeltaTime);
        Core.Diagnostics.Profiler.Count("Audio voices", _voices.Count);
        var listener = _scene.FindObjectsOfType<AudioListener>().FirstOrDefault(l => l.IsActiveAndEnabled)?.Transform
            ?? _scene.MainCamera?.Transform;
        Device.ListenerPosition = listener is null ? Vector2.Zero : (Vector2)listener.Position;
        _mixer = _scene.FindObjectsOfType<AudioMixer>().FirstOrDefault(m => m.IsActiveAndEnabled);
        foreach (var source in world.Pool<AudioSource>().Components)
        {
            source.Follow(this);
        }

        for (var i = _oneShots.Count - 1; i >= 0; i--)
        {
            var (voice, volume, group) = _oneShots[i];
            if (voice.State == VoiceState.Stopped)
            {
                Release(voice);
                _oneShots.RemoveAt(i);
            }
            else
            {
                voice.Volume = volume * Mix(group);
            }
        }
    }

    /// <summary>The scene mixer used this frame, if any.</summary>
    public AudioMixer? Mixer => _mixer ??= _scene.FindObjectsOfType<AudioMixer>().FirstOrDefault(m => m.IsActiveAndEnabled);

    /// <summary>Volume factor of a mixer group (1 without a mixer).</summary>
    internal float Mix(string group) => Mixer?.EffectiveVolume(group) ?? 1f;

    internal IAudioVoice CreateVoice()
    {
        var voice = Device.CreateVoice();
        _voices.Add(voice);
        return voice;
    }

    internal void Release(IAudioVoice voice)
    {
        if (_voices.Remove(voice))
        {
            voice.Stop();
            voice.Dispose();
        }
    }

    /// <summary>The uploaded clip, or null (logged once) when it is missing or cannot be decoded.</summary>
    internal IAudioBuffer? GetBuffer(AssetRef clip)
    {
        if (clip.IsNone)
        {
            return null;
        }

        if (_buffers.TryGetValue(clip.Guid, out var cached))
        {
            return cached;
        }

        IAudioBuffer? buffer = null;
        try
        {
            var data = AudioDecoder.Decode(_readClip(clip), clip.ToString());
            buffer = Device.CreateBuffer(data);
            BufferBytes += data.Samples.Length * (long)sizeof(short);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            LogClipFailed(_scene.Logger, ex, clip.ToString());
        }

        _buffers[clip.Guid] = buffer;
        return buffer;
    }

    internal void PlayOneShot(AssetRef clip, float volume, string group, float pitch, Vector2? position, float referenceDistance, float maxDistance)
    {
        if (GetBuffer(clip) is not { } buffer)
        {
            return;
        }

        var voice = CreateVoice();
        (voice.Volume, voice.Pitch, voice.Loop, voice.Spatial) = (volume * Mix(group), pitch, false, position is not null);
        (voice.ReferenceDistance, voice.MaxDistance) = (referenceDistance, maxDistance);
        voice.Position = position ?? Vector2.Zero;
        voice.Play(buffer);
        _oneShots.Add((voice, volume, group));
    }

    private void OnUnloaded(Scene scene)
    {
        scene.Unloaded -= OnUnloaded;
        foreach (var voice in _voices.ToList())
        {
            Release(voice);
        }

        _oneShots.Clear();
        foreach (var buffer in _buffers.Values)
        {
            buffer?.Dispose();
        }

        _buffers.Clear();
        BufferBytes = 0;
        scene.Systems.Remove(this);
    }

    [LoggerMessage(EventId = 2400, Level = LogLevel.Error, Message = "Audio clip {Clip} could not be loaded; it will not play")]
    private static partial void LogClipFailed(ILogger logger, Exception exception, string clip);
}
