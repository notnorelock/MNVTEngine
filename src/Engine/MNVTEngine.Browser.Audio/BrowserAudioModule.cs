using MNVTEngine.Audio;
using MNVTEngine.Core.Timing;
using MNVTEngine.ECS;
using MNVTEngine.Scenes;

namespace MNVTEngine.Browser;

/// <summary>
/// The "browser.audio" engine module: each frame, moves every browser's page audio into a streaming voice of the
/// scene's audio output, at the browser's <see cref="BrowserInstance.Volume"/>, on the mixer bus
/// <see cref="BrowserInstance.AudioGroup"/>.
/// </summary>
public sealed class BrowserAudioModule : IEngineModule, ISceneIntegration
{
    private readonly BrowserAudio _audio = new();

    public string Id => "browser.audio";

    public void Register(ComponentRegistry components)
    {
    }

    public void SceneLoaded(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.Systems.Get<AudioPump>() is null)
        {
            scene.Systems.Add(new AudioPump(_audio, scene));
        }
    }

    public void Shutdown() => _audio.Clear();

    private sealed class AudioPump(BrowserAudio audio, Scene scene) : GameSystem
    {
        public override int Order => 1; // after the browser system has delivered this frame's messages

        public override void Update(World world, FrameTime time)
        {
            if (scene.GetService<BrowserService>() is { } service)
            {
                audio.Pump(service.Browsers, AudioSystem.For(scene)?.Device);
            }
        }
    }
}

/// <summary>The voices of the browsers' page audio (one per browser playing sound).</summary>
public sealed class BrowserAudio
{
    private readonly Dictionary<BrowserInstance, IStreamingVoice> _voices = [];
    private readonly short[] _buffer = new short[48000 * 2]; // a second of stereo: more than one frame ever brings

    /// <summary>Seconds of a browser's page audio queued in the audio output (0 when it plays nothing).</summary>
    public float QueuedSeconds(BrowserInstance browser) => _voices.TryGetValue(browser, out var voice) ? voice.QueuedSeconds : 0f;

    /// <summary>The voice a browser's page audio plays through, if it plays any.</summary>
    public IStreamingVoice? Voice(BrowserInstance browser) => _voices.GetValueOrDefault(browser);

    /// <summary>
    /// Moves the page audio produced since the last call into the browsers' voices of <paramref name="device"/>.
    /// Without a device the audio is read and dropped (the rings never back up). Closed browsers' voices are released.
    /// </summary>
    public void Pump(IEnumerable<BrowserInstance> browsers, IAudioDevice? device)
    {
        ArgumentNullException.ThrowIfNull(browsers);
        foreach (var gone in _voices.Keys.Where(b => b.State == BrowserState.Closed).ToList())
        {
            _voices.Remove(gone, out var voice);
            voice!.Dispose();
        }

        foreach (var browser in browsers)
        {
            var frames = browser.ReadAudio(_buffer, out var channels, out var sampleRate);
            if (device is null || channels == 0)
            {
                continue;
            }

            if (!_voices.TryGetValue(browser, out var voice) || voice.Channels != channels || voice.SampleRate != sampleRate)
            {
                voice?.Dispose();
                _voices[browser] = voice = device.CreateStreamingVoice(channels, sampleRate);
            }

            (voice.Volume, voice.Bus) = (Math.Max(0f, browser.Volume), browser.AudioGroup);

            // More than a quarter second queued means the game ran slower than the page: drop, so the sound stays in sync.
            if (frames > 0 && voice.QueuedSeconds < 0.25f)
            {
                voice.Queue(_buffer.AsSpan(0, frames * channels));
            }
        }
    }

    /// <summary>Releases every voice (the module's shutdown).</summary>
    public void Clear()
    {
        foreach (var voice in _voices.Values)
        {
            voice.Dispose();
        }

        _voices.Clear();
    }
}
