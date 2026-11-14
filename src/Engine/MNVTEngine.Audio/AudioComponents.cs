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

    /// <summary>Pan and attenuate by position relative to the listener. Only mono clips are spatialised; stereo plays as recorded.</summary>
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

    /// <summary>Seconds into the playing clip; 0 when stopped. Setting it seeks (streamed clips too).</summary>
    public float Time
    {
        get => _voice?.Time ?? 0f;
        set
        {
            if (_voice is not null)
            {
                _voice.Time = value;
            }
        }
    }

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

    /// <summary>Moved by DontDestroyOnLoad: the voice and its clip go to the new scene's audio, so playback continues.</summary>
    protected override void OnMovedToScene(Scene previous)
    {
        if (_voice is null || AudioSystem.For(previous) is not { } old)
        {
            return;
        }

        if (System is { } current)
        {
            current.Adopt(old, _voice, Clip);
        }
        else
        {
            old.Release(_voice); // no audio in the new scene
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

        _voice.Volume = _volume;
        _voice.Bus = Group;
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

        _voice.Volume = _volume;
        _voice.Bus = Group;
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
    private readonly IAssetReader _assets;
    private readonly Dictionary<Guid, IAudioBuffer?> _buffers = [];
    private readonly List<IAudioVoice> _voices = [];
    private readonly List<IAudioVoice> _oneShots = [];
    private readonly List<IAudioBuffer> _adoptedBuffers = [];
    private AudioMixer? _mixer;
    private int? _layout;

    private AudioSystem(Scene scene, IAudioDevice device, IAssetReader assets)
    {
        _scene = scene;
        Device = device;
        _assets = assets;
        scene.Unloaded += OnUnloaded;
    }

    /// <summary>Import setting <c>"loadType"</c> of audio assets: <c>"auto"</c> (default), <c>"decompressOnLoad"</c> or <c>"streaming"</c>.</summary>
    public const string LoadTypeSetting = "loadType";

    /// <summary>
    /// With <c>"loadType": "auto"</c>, clips whose decoded PCM would be larger than this are streamed (decoded while they
    /// play) instead of decoded into memory. 4 MiB is about 24 s of 44.1 kHz stereo.
    /// </summary>
    public long StreamingThreshold { get; set; } = 4 * 1024 * 1024;

    /// <summary>Clips this scene plays streamed.</summary>
    public int StreamedClipCount { get; private set; }

    public IAudioDevice Device { get; }

    /// <summary>Voices alive in this scene (sources plus one-shots still playing).</summary>
    public int VoiceCount => _voices.Count;

    /// <summary>Decoded PCM this scene's audio decoded (buffers adopted from a previous scene are not counted).</summary>
    public long BufferBytes { get; private set; }

    /// <summary>Clips uploaded (or attempted) by this scene.</summary>
    public int ClipCount => _buffers.Count;

    /// <summary>Clips that could not be loaded (they never play).</summary>
    public int FailedClipCount => _buffers.Values.Count(b => b is null);

    /// <param name="readClip">Bytes of an audio asset. Clips are then always decoded into memory (no streams, no import settings).</param>
    /// <exception cref="InvalidOperationException">The scene already has an audio system.</exception>
    public static AudioSystem Attach(Scene scene, IAudioDevice device, Func<AssetRef, byte[]> readClip)
    {
        ArgumentNullException.ThrowIfNull(readClip);
        return Attach(scene, device, new BytesReader(readClip));
    }

    /// <param name="assets">Audio assets (the project folder, or the game's packages through the virtual file system) and their import settings.</param>
    /// <exception cref="InvalidOperationException">The scene already has an audio system.</exception>
    public static AudioSystem Attach(Scene scene, IAudioDevice device, IAssetReader assets)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(assets);
        if (For(scene) is not null)
        {
            throw new InvalidOperationException("The scene already has an audio system.");
        }

        var system = new AudioSystem(scene, device, assets);
        scene.Systems.Add(system);
        return system;
    }

    private sealed class BytesReader(Func<AssetRef, byte[]> read) : IAssetReader
    {
        public byte[] ReadAllBytes(AssetRef asset) => read(asset);
    }

    public static AudioSystem? For(Scene scene) => scene?.Systems.Get<AudioSystem>();

    public override void Update(World world, FrameTime time)
    {
        _mixer = _scene.FindObjectsOfType<AudioMixer>().FirstOrDefault(m => m.IsActiveAndEnabled);
        ApplyMixer(); // before rendering: this frame's sound already uses this frame's faders
        Device.Update(time.UnscaledDeltaTime);
        while (Device.TakeStreamError() is { } error)
        {
            LogStreamFailed(_scene.Logger, error);
        }

        Core.Diagnostics.Profiler.Count("Audio voices", _voices.Count);
        var listener = _scene.FindObjectsOfType<AudioListener>().FirstOrDefault(l => l.IsActiveAndEnabled)?.Transform
            ?? _scene.MainCamera?.Transform;
        Device.ListenerPosition = listener is null ? Vector2.Zero : (Vector2)listener.Position;
        foreach (var source in world.Pool<AudioSource>().Components)
        {
            source.Follow(this);
        }

        for (var i = _oneShots.Count - 1; i >= 0; i--)
        {
            if (_oneShots[i].State == VoiceState.Stopped)
            {
                Release(_oneShots[i]);
                _oneShots.RemoveAt(i);
            }
        }
    }

    /// <summary>Hands the scene mixer's groups and effects to the device as buses, when they changed (or the mixer did).</summary>
    private void ApplyMixer()
    {
        var fingerprint = _mixer is { } mixer ? HashCode.Combine(mixer, mixer.LayoutFingerprint()) : 0;
        if (_layout == fingerprint)
        {
            return;
        }

        _layout = fingerprint;
        Device.SetBuses(_mixer?.BusLayout() ?? []);
    }

    /// <summary>The scene mixer used this frame, if any.</summary>
    public AudioMixer? Mixer => _mixer ??= _scene.FindObjectsOfType<AudioMixer>().FirstOrDefault(m => m.IsActiveAndEnabled);

    /// <summary>Volume factor of a mixer group (1 without a mixer; unknown groups play through Master).</summary>
    public float Mix(string group) => Mixer?.EffectiveVolume(group) ?? 1f;

    internal IAudioVoice CreateVoice()
    {
        var voice = Device.CreateVoice();
        _voices.Add(voice);
        return voice;
    }

    /// <summary>
    /// Takes over a voice from another scene's audio (a DontDestroyOnLoad source), with the buffer it plays: that scene
    /// releases its own voices and buffers when it unloads.
    /// </summary>
    internal void Adopt(AudioSystem from, IAudioVoice voice, AssetRef clip)
    {
        if (from._voices.Remove(voice))
        {
            _voices.Add(voice);
        }

        if (!clip.IsNone && from._buffers.Remove(clip.Guid, out var buffer) && buffer is not null)
        {
            if (!_buffers.TryAdd(clip.Guid, buffer))
            {
                _adoptedBuffers.Add(buffer); // this scene loaded the clip too; keep the playing copy alive until unload
            }
        }
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
            var name = clip.ToString();
            var loadType = _assets.GetImportSetting(clip, LoadTypeSetting) ?? "auto";
            using var source = AudioDecoder.Open(_assets.OpenRead(clip), name);
            var decodedBytes = source.Frames is { } frames ? frames * source.Channels * sizeof(short) : (long?)null;
            if (loadType == "streaming" || (loadType == "auto" && decodedBytes > StreamingThreshold))
            {
                buffer = Device.CreateStreamedBuffer(() => AudioDecoder.Open(_assets.OpenRead(clip), name));
                StreamedClipCount++;
            }
            else
            {
                var data = AudioDecoder.ReadAll(source, name);
                buffer = Device.CreateBuffer(data);
                BufferBytes += data.Samples.Length * (long)sizeof(short);
            }
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
        (voice.Volume, voice.Bus, voice.Pitch, voice.Loop, voice.Spatial) = (volume, group, pitch, false, position is not null);
        (voice.ReferenceDistance, voice.MaxDistance) = (referenceDistance, maxDistance);
        voice.Position = position ?? Vector2.Zero;
        voice.Play(buffer);
        _oneShots.Add(voice);
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
        foreach (var buffer in _adoptedBuffers)
        {
            buffer.Dispose();
        }

        _adoptedBuffers.Clear();
        BufferBytes = 0;
        scene.Systems.Remove(this);
    }

    [LoggerMessage(EventId = 2400, Level = LogLevel.Error, Message = "Audio clip {Clip} could not be loaded; it will not play")]
    private static partial void LogClipFailed(ILogger logger, Exception exception, string clip);

    [LoggerMessage(EventId = 2401, Level = LogLevel.Error, Message = "A streamed audio clip stopped: it could not be read or decoded")]
    private static partial void LogStreamFailed(ILogger logger, Exception exception);
}

/// <summary>
/// The "audio" engine module: registers audio sources, listeners and mixers, owns the audio output (created at
/// initialization from the host's factory, silent without one) and attaches an <see cref="AudioSystem"/> to every scene.
/// </summary>
public sealed class AudioModule(Audio.AudioDeviceFactory? createDevice = null, Microsoft.Extensions.Logging.ILogger? logger = null)
    : Scenes.IEngineModule, Scenes.ISceneIntegration, Scenes.IDiagnosticsIntegration
{
    private Audio.IAudioDevice? _device;

    public string Id => "audio";

    /// <summary>The output scenes play on; null before initialization.</summary>
    public Audio.IAudioDevice? Device => _device;

    public void Register(Scenes.ComponentRegistry components) => AudioComponents.Register(components);

    public void Initialize() => _device = Audio.AudioDevices.CreateOrSilent(createDevice, logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

    public void Shutdown()
    {
        _device?.Dispose();
        _device = null;
    }

    public void SceneLoaded(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (_device is { } device && scene.AssetReader is { } assets)
        {
            AudioSystem.Attach(scene, device, assets);
        }

        scene.SetService<ISceneAudio>(new SceneAudio(scene));
    }

    public IEnumerable<Diagnostics.IMetricsProvider> CreateMetricsProviders(Func<Scene?> scene) => [new AudioMetrics(scene)];

    /// <summary>Voices and buffer memory of the running scene's audio.</summary>
    private sealed class AudioMetrics(Func<Scene?> scene) : Diagnostics.IMetricsProvider
    {
        public string Name => "Audio";

        public void Collect(Diagnostics.MetricsSnapshot metrics)
        {
            if (scene() is { } current && AudioSystem.For(current) is { } audio)
            {
                metrics.Set(Diagnostics.Metrics.AudioSources, audio.VoiceCount);
                metrics.Set(Diagnostics.Metrics.AudioMemory, audio.BufferBytes);
                if (audio.Device is MixingAudioDevice mixer)
                {
                    metrics.Set(Diagnostics.Metrics.AudioStreams, mixer.ActiveStreams);
                    metrics.Set(Diagnostics.Metrics.AudioUnderruns, mixer.StreamUnderruns);
                }
                else
                {
                    metrics.SetUnsupported(Diagnostics.Metrics.AudioStreams);
                    metrics.SetUnsupported(Diagnostics.Metrics.AudioUnderruns);
                }
            }
        }
    }
}

/// <summary>The audio module's <see cref="ISceneAudio"/>: sources and the active mixer of one scene.</summary>
internal sealed class SceneAudio(Scene scene) : ISceneAudio
{
    public void PlaySound(GameObject target) => Source(target).Play();

    public void StopSound(GameObject target) => Source(target).Stop();

    public void SetGroupVolume(string group, float volume) => WithMixer(mixer => mixer.SetVolume(group, volume));

    public void SetGroupMuted(string group, bool muted) => WithMixer(mixer => mixer.SetMuted(group, muted));

    private static AudioSource Source(GameObject target) =>
        target.GetComponent<AudioSource>() ?? throw new InvalidOperationException($"'{target.Name}' has no AudioSource.");

    private void WithMixer(Action<AudioMixer> action)
    {
        var mixer = scene.FindObjectsOfType<AudioMixer>().FirstOrDefault(m => m.IsActiveAndEnabled)
            ?? throw new InvalidOperationException("The scene has no AudioMixer.");
        try
        {
            action(mixer);
        }
        catch (KeyNotFoundException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }
}
