using System.Buffers.Binary;
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

/// <summary>Decodes WAV (PCM 8/16/24/32-bit, IEEE float) and Ogg Vorbis to 16-bit PCM.</summary>
public static class AudioDecoder
{
    /// <exception cref="InvalidDataException">Unknown, unsupported or damaged audio.</exception>
    public static AudioClipData Decode(byte[] data, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            if (data.Length >= 12 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8) && data.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            {
                return DecodeWav(data);
            }

            if (data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual("OggS"u8))
            {
                return DecodeOgg(data);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or EndOfStreamException or InvalidOperationException)
        {
            throw new InvalidDataException($"'{sourceName}' is damaged: {ex.Message}", ex);
        }

        throw new InvalidDataException($"'{sourceName}' is not a WAV or Ogg Vorbis file.");
    }

    private static AudioClipData DecodeWav(byte[] data)
    {
        int channels = 0, rate = 0, bits = 0, format = 0;
        var offset = 12;
        while (offset + 8 <= data.Length)
        {
            var id = data.AsSpan(offset, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 4));
            var body = offset + 8;
            if (size < 0 || body + size > data.Length)
            {
                size = data.Length - body; // tolerate a truncated last chunk
            }

            if (id.SequenceEqual("fmt "u8))
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(body + 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 14));
                if (format == 0xFFFE && size >= 26)
                {
                    format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 24)); // WAVE_FORMAT_EXTENSIBLE sub-format
                }
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (channels == 0)
                {
                    throw new InvalidDataException("WAV 'data' chunk before 'fmt '.");
                }

                return new AudioClipData(ConvertWav(data.AsSpan(body, size), format, bits), channels, rate);
            }

            offset = body + size + (size & 1);
        }

        throw new InvalidDataException("WAV file has no 'data' chunk.");
    }

    private static short[] ConvertWav(ReadOnlySpan<byte> pcm, int format, int bits)
    {
        switch (format, bits)
        {
            case (1, 8):
                var u8 = new short[pcm.Length];
                for (var i = 0; i < u8.Length; i++)
                {
                    u8[i] = (short)((pcm[i] - 128) << 8);
                }

                return u8;
            case (1, 16):
                var s16 = new short[pcm.Length / 2];
                for (var i = 0; i < s16.Length; i++)
                {
                    s16[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm[(i * 2)..]);
                }

                return s16;
            case (1, 24):
                var s24 = new short[pcm.Length / 3];
                for (var i = 0; i < s24.Length; i++)
                {
                    s24[i] = (short)(pcm[(i * 3) + 1] | (pcm[(i * 3) + 2] << 8));
                }

                return s24;
            case (1, 32):
                var s32 = new short[pcm.Length / 4];
                for (var i = 0; i < s32.Length; i++)
                {
                    s32[i] = (short)(BinaryPrimitives.ReadInt32LittleEndian(pcm[(i * 4)..]) >> 16);
                }

                return s32;
            case (3, 32):
                var f32 = new short[pcm.Length / 4];
                for (var i = 0; i < f32.Length; i++)
                {
                    f32[i] = ToShort(BinaryPrimitives.ReadSingleLittleEndian(pcm[(i * 4)..]));
                }

                return f32;
            default:
                throw new InvalidDataException($"Unsupported WAV encoding (format {format}, {bits} bits).");
        }
    }

    private static AudioClipData DecodeOgg(byte[] data)
    {
        using var reader = new NVorbis.VorbisReader(new MemoryStream(data), closeOnDispose: true);
        var samples = new List<short>((int)Math.Min(reader.TotalSamples * reader.Channels, 1 << 26));
        var buffer = new float[4096 * reader.Channels];
        int read;
        while ((read = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                samples.Add(ToShort(buffer[i]));
            }
        }

        return new AudioClipData([.. samples], reader.Channels, reader.SampleRate);
    }

    private static short ToShort(float sample) => (short)(Math.Clamp(sample, -1f, 1f) * short.MaxValue);
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

    void Play(IAudioBuffer buffer);

    void Pause();

    void Resume();

    void Stop();
}

/// <summary>An audio output. Not thread-safe: use it from the main thread.</summary>
public interface IAudioDevice : IDisposable
{
    string Name { get; }

    float MasterVolume { get; set; }

    Vector2 ListenerPosition { get; set; }

    /// <exception cref="ObjectDisposedException">The device was disposed.</exception>
    IAudioBuffer CreateBuffer(AudioClipData clip);

    IAudioVoice CreateVoice();

    /// <summary>Called once per frame (devices that need housekeeping or simulated time).</summary>
    void Update(float deltaTime);
}

/// <summary>
/// Silent device: tracks playback state and simulated time exactly like a real one, but produces no sound. Used for
/// tests, headless runs, and when no audio hardware is available.
/// </summary>
public sealed class NullAudioDevice : IAudioDevice
{
    private readonly List<Voice> _voices = [];

    public string Name => "Null (silent)";

    public float MasterVolume { get; set; } = 1f;

    public Vector2 ListenerPosition { get; set; }

    public int LiveBuffers { get; private set; }

    public int LiveVoices => _voices.Count;

    public IAudioBuffer CreateBuffer(AudioClipData clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        LiveBuffers++;
        return new Buffer(this, clip.Duration);
    }

    public IAudioVoice CreateVoice()
    {
        var voice = new Voice(this);
        _voices.Add(voice);
        return voice;
    }

    public void Update(float deltaTime)
    {
        foreach (var voice in _voices)
        {
            voice.Advance(deltaTime);
        }
    }

    public void Dispose()
    {
    }

    private sealed class Buffer(NullAudioDevice device, float duration) : IAudioBuffer
    {
        private bool _disposed;

        public float Duration => duration;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                device.LiveBuffers--;
            }
        }
    }

    private sealed class Voice(NullAudioDevice device) : IAudioVoice
    {
        private float _duration;
        private float _time;

        public VoiceState State { get; private set; }

        public float Volume { get; set; } = 1f;

        public float Pitch { get; set; } = 1f;

        public bool Loop { get; set; }

        public bool Spatial { get; set; }

        public Vector2 Position { get; set; }

        public float ReferenceDistance { get; set; } = 1f;

        public float MaxDistance { get; set; } = 20f;

        public void Play(IAudioBuffer buffer)
        {
            _duration = buffer.Duration;
            _time = 0f;
            State = VoiceState.Playing;
        }

        public void Pause()
        {
            if (State == VoiceState.Playing)
            {
                State = VoiceState.Paused;
            }
        }

        public void Resume()
        {
            if (State == VoiceState.Paused)
            {
                State = VoiceState.Playing;
            }
        }

        public void Stop() => State = VoiceState.Stopped;

        public void Advance(float deltaTime)
        {
            if (State != VoiceState.Playing)
            {
                return;
            }

            _time += deltaTime * Pitch;
            if (_time >= _duration && !Loop)
            {
                State = VoiceState.Stopped;
            }
        }

        public void Dispose() => device._voices.Remove(this);
    }
}
