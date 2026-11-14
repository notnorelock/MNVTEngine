using MNVTEngine.Mathematics;

namespace MNVTEngine.Audio;

/// <summary>Decoded 16-bit PCM, interleaved when stereo.</summary>
public sealed class AudioClipData
{
    public AudioClipData(short[] samples, int channels, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (channels is not (1 or 2))
        {
            throw new InvalidDataException($"Only mono and stereo audio are supported, got {channels} channels.");
        }

        if (sampleRate is < 1000 or > 384000)
        {
            throw new InvalidDataException($"Unsupported sample rate {sampleRate} Hz.");
        }

        Samples = samples;
        Channels = channels;
        SampleRate = sampleRate;
    }

    public short[] Samples { get; }

    public int Channels { get; }

    public int SampleRate { get; }

    public float Duration => Samples.Length / (float)(Channels * SampleRate);
}

public enum VoiceState
{
    Stopped,
    Playing,
    Paused,
}

/// <summary>A clip uploaded to the device. Owned by whoever created it.</summary>
public interface IAudioBuffer : IDisposable
{
    float Duration { get; }

    /// <summary>Decoded while it plays (<see cref="IAudioDevice.CreateStreamedBuffer"/>) rather than held in memory.</summary>
    bool IsStreamed => false;
}

/// <summary>One playing sound (an OpenAL source). Positions are world units; non-spatial voices ignore position and distance.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716", Justification = "Play/Stop/Resume/Loop are the natural audio terms; the engine API is consumed from C#.")]
public interface IAudioVoice : IDisposable
{
    VoiceState State { get; }

    float Volume { get; set; }

    float Pitch { get; set; }

    bool Loop { get; set; }

    bool Spatial { get; set; }

    Vector2 Position { get; set; }

    /// <summary>Full volume up to this distance, then attenuated.</summary>
    float ReferenceDistance { get; set; }

    /// <summary>No further attenuation beyond this distance.</summary>
    float MaxDistance { get; set; }

    /// <summary>The mixer bus (an <c>AudioMixer</c> group) it plays through; empty or unknown means Master.</summary>
    string Bus { get; set; }

    /// <summary>
    /// Seconds into the clip (looping wraps it); 0 when stopped. Setting it seeks, clamped to the clip: a streamed clip
    /// restarts decoding there, so the seek is heard after the first block decodes.
    /// </summary>
    float Time { get; set; }

    void Play(IAudioBuffer buffer);

    void Pause();

    void Resume();

    void Stop();
}

/// <summary>
/// A voice fed with 16-bit PCM while it plays (a browser page, a video, voice chat). It plays whatever is queued and
/// goes quiet, not stopped for good, when the samples run out; the next <see cref="Queue"/> continues.
/// </summary>
public interface IStreamingVoice : IDisposable
{
    int Channels { get; }

    int SampleRate { get; }

    float Volume { get; set; }

    /// <summary>The mixer bus it plays through; empty or unknown means Master.</summary>
    string Bus { get; set; }

    /// <summary>Seconds of audio queued and not played yet.</summary>
    float QueuedSeconds { get; }

    /// <param name="interleaved">Whole frames: a multiple of <see cref="Channels"/> samples.</param>
    void Queue(ReadOnlySpan<short> interleaved);
}

/// <summary>An audio output. Not thread-safe: use it from the main thread.</summary>
public interface IAudioDevice : IDisposable
{
    string Name { get; }

    float MasterVolume { get; set; }

    Vector2 ListenerPosition { get; set; }

    /// <exception cref="ObjectDisposedException">The device was disposed.</exception>
    IAudioBuffer CreateBuffer(AudioClipData clip);

    /// <summary>
    /// A clip decoded while it plays (music, ambience, dialogue): only a short buffer per playing voice is in memory.
    /// Every voice that plays it opens its own decoder with <paramref name="open"/>, on the device's streaming thread.
    /// </summary>
    /// <param name="open">Opens a decoder at the clip's start; called once now (for the format) and once per playback.</param>
    /// <exception cref="InvalidDataException">The clip cannot be decoded.</exception>
    /// <exception cref="ObjectDisposedException">The device was disposed.</exception>
    IAudioBuffer CreateStreamedBuffer(Func<IAudioSampleSource> open);

    /// <summary>The next read or decoding error of a streamed clip (its voice stopped early), for the caller to log; null when none.</summary>
    Exception? TakeStreamError();

    IAudioVoice CreateVoice();

    /// <param name="channels">1 or 2.</param>
    IStreamingVoice CreateStreamingVoice(int channels, int sampleRate);

    /// <summary>The mixer buses: faders, mutes, parents and insert effects (see <see cref="MixingAudioDevice"/>).</summary>
    void SetBuses(IReadOnlyList<AudioBusSettings> buses);

    /// <summary>A bus's level after its fader; false for a bus that does not exist.</summary>
    bool TryGetMeter(string bus, out AudioMeter meter);

    /// <summary>Clears a bus's peak hold and clip indicator.</summary>
    void ResetMeter(string bus);

    /// <summary>Called once per frame (devices that need housekeeping or simulated time).</summary>
    void Update(float deltaTime);
}

/// <summary>
/// The mixer without an output: everything is mixed, metered and timed exactly as it would be heard, but nothing is
/// played; <see cref="IAudioDevice.Update"/> renders the time that passed. Used for tests, headless runs, and when no
/// audio hardware is available.
/// </summary>
public sealed class NullAudioDevice() : MixingAudioDevice(null)
{
    public override string Name => "Null (silent)";
}
