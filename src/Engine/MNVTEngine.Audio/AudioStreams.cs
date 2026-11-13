using System.Buffers.Binary;

namespace MNVTEngine.Audio;

/// <summary>
/// A decoder reading 16-bit PCM frames from an encoded stream, a block at a time: what streamed clips play from, and
/// what <see cref="AudioDecoder.Decode"/> drains for clips decoded on load. Not thread-safe; owns its stream.
/// </summary>
public interface IAudioSampleSource : IDisposable
{
    /// <summary>1 or 2.</summary>
    int Channels { get; }

    int SampleRate { get; }

    /// <summary>Total frames (samples per channel); null when the format does not say.</summary>
    long? Frames { get; }

    /// <summary>Decodes up to <paramref name="interleaved"/>.Length / <see cref="Channels"/> frames; returns the frames written, 0 at the end.</summary>
    /// <exception cref="InvalidDataException">The data is damaged.</exception>
    int Read(Span<short> interleaved);

    /// <summary>Back to the first frame (looping).</summary>
    void Rewind();
}

/// <summary>Decodes WAV (PCM 8/16/24/32-bit, IEEE float) and Ogg Vorbis to 16-bit PCM, whole or streamed.</summary>
public static class AudioDecoder
{
    /// <summary>A whole clip decoded into memory.</summary>
    /// <exception cref="InvalidDataException">Unknown, unsupported or damaged audio.</exception>
    public static AudioClipData Decode(byte[] data, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var source = Open(new MemoryStream(data, writable: false), sourceName);
        return ReadAll(source, sourceName);
    }

    /// <summary>Decodes everything left in <paramref name="source"/>.</summary>
    /// <exception cref="InvalidDataException">The data is damaged.</exception>
    public static AudioClipData ReadAll(IAudioSampleSource source, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(source);
        var samples = new List<short>((int)Math.Min((source.Frames ?? 0) * source.Channels, 1 << 26));
        var block = new short[4096 * source.Channels];
        int frames;
        while ((frames = Guard(() => source.Read(block), sourceName)) > 0)
        {
            samples.AddRange(block.AsSpan(0, frames * source.Channels));
        }

        return new AudioClipData([.. samples], source.Channels, source.SampleRate);
    }

    /// <summary>A decoder over <paramref name="stream"/> (seekable for WAV and for looping); it owns the stream.</summary>
    /// <exception cref="InvalidDataException">Unknown, unsupported or damaged audio.</exception>
    public static IAudioSampleSource Open(Stream stream, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            Span<byte> magic = stackalloc byte[12];
            var read = stream.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false);
            stream.Position = 0;
            if (read >= 12 && magic[..4].SequenceEqual("RIFF"u8) && magic.Slice(8, 4).SequenceEqual("WAVE"u8))
            {
                return Guard(() => new WavSampleSource(stream), sourceName);
            }

            if (read >= 4 && magic[..4].SequenceEqual("OggS"u8))
            {
                return Guard(() => new OggSampleSource(stream), sourceName);
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        stream.Dispose();
        throw new InvalidDataException($"'{sourceName}' is not a WAV or Ogg Vorbis file.");
    }

    /// <summary>Decoder errors (malformed headers, truncated Vorbis packets) as <see cref="InvalidDataException"/>.</summary>
    internal static T Guard<T>(Func<T> decode, string sourceName)
    {
        try
        {
            return decode();
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or EndOfStreamException or InvalidOperationException)
        {
            throw new InvalidDataException($"'{sourceName}' is damaged: {ex.Message}", ex);
        }
    }

    internal static short ToShort(float sample) => (short)(Math.Clamp(sample, -1f, 1f) * short.MaxValue);
}

/// <summary>A RIFF WAVE file read from its <c>data</c> chunk on.</summary>
internal sealed class WavSampleSource : IAudioSampleSource
{
    private readonly Stream _stream;
    private readonly int _format;
    private readonly int _bits;
    private readonly long _dataStart;
    private readonly long _dataLength;
    private long _consumed;
    private byte[] _raw = [];

    public WavSampleSource(Stream stream)
    {
        _stream = stream;
        Span<byte> header = stackalloc byte[8];
        _stream.Position = 12;
        int channels = 0, rate = 0;
        while (true)
        {
            if (_stream.ReadAtLeast(header, 8, throwOnEndOfStream: false) < 8)
            {
                throw new InvalidDataException("WAV file has no 'data' chunk.");
            }

            var size = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            var body = _stream.Position;
            var remaining = _stream.Length - body;
            if (size < 0 || size > remaining)
            {
                size = (int)Math.Min(remaining, int.MaxValue); // tolerate a truncated last chunk
            }

            if (header[..4].SequenceEqual("fmt "u8))
            {
                var fmt = new byte[Math.Max(size, 16)];
                _stream.ReadExactly(fmt.AsSpan(0, Math.Min(size, fmt.Length)));
                _format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(4));
                _bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));
                if (_format == 0xFFFE && size >= 26)
                {
                    _format = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(24)); // WAVE_FORMAT_EXTENSIBLE sub-format
                }
            }
            else if (header[..4].SequenceEqual("data"u8))
            {
                if (channels == 0)
                {
                    throw new InvalidDataException("WAV 'data' chunk before 'fmt '.");
                }

                if ((_format, _bits) is not ((1, 8) or (1, 16) or (1, 24) or (1, 32) or (3, 32)))
                {
                    throw new InvalidDataException($"Unsupported WAV encoding (format {_format}, {_bits} bits).");
                }

                _ = new AudioClipData([], channels, rate); // validates channels and rate
                (Channels, SampleRate, _dataStart, _dataLength) = (channels, rate, body, size);
                Frames = size / (_bits / 8 * channels);
                return;
            }

            _stream.Position = body + size + (size & 1);
        }
    }

    public int Channels { get; }

    public int SampleRate { get; }

    public long? Frames { get; }

    private int FrameBytes => _bits / 8 * Channels;

    public int Read(Span<short> interleaved)
    {
        var frames = (int)Math.Min(interleaved.Length / Channels, (_dataLength - _consumed) / FrameBytes);
        if (frames <= 0)
        {
            return 0;
        }

        var bytes = frames * FrameBytes;
        if (_raw.Length < bytes)
        {
            _raw = new byte[bytes];
        }

        _stream.Position = _dataStart + _consumed;
        bytes = _stream.ReadAtLeast(_raw.AsSpan(0, bytes), bytes, throwOnEndOfStream: false) / FrameBytes * FrameBytes;
        _consumed += bytes;
        Convert(_raw.AsSpan(0, bytes), interleaved);
        return bytes / FrameBytes;
    }

    public void Rewind() => _consumed = 0;

    public void Dispose() => _stream.Dispose();

    private void Convert(ReadOnlySpan<byte> pcm, Span<short> output)
    {
        switch (_format, _bits)
        {
            case (1, 8):
                for (var i = 0; i < pcm.Length; i++)
                {
                    output[i] = (short)((pcm[i] - 128) << 8);
                }

                break;
            case (1, 16):
                for (var i = 0; i < pcm.Length / 2; i++)
                {
                    output[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm[(i * 2)..]);
                }

                break;
            case (1, 24):
                for (var i = 0; i < pcm.Length / 3; i++)
                {
                    output[i] = (short)(pcm[(i * 3) + 1] | (pcm[(i * 3) + 2] << 8));
                }

                break;
            case (1, 32):
                for (var i = 0; i < pcm.Length / 4; i++)
                {
                    output[i] = (short)(BinaryPrimitives.ReadInt32LittleEndian(pcm[(i * 4)..]) >> 16);
                }

                break;
            default: // (3, 32): IEEE float, checked in the constructor
                for (var i = 0; i < pcm.Length / 4; i++)
                {
                    output[i] = AudioDecoder.ToShort(BinaryPrimitives.ReadSingleLittleEndian(pcm[(i * 4)..]));
                }

                break;
        }
    }
}

/// <summary>An Ogg Vorbis stream (NVorbis), decoded packet by packet.</summary>
internal sealed class OggSampleSource : IAudioSampleSource
{
    private readonly NVorbis.VorbisReader _reader;
    private float[] _float = [];

    public OggSampleSource(Stream stream)
    {
        _reader = new NVorbis.VorbisReader(stream, closeOnDispose: true);
        try
        {
            _ = new AudioClipData([], _reader.Channels, _reader.SampleRate); // validates channels and rate
        }
        catch
        {
            _reader.Dispose();
            throw;
        }
    }

    public int Channels => _reader.Channels;

    public int SampleRate => _reader.SampleRate;

    public long? Frames => _reader.TotalSamples > 0 ? _reader.TotalSamples : null;

    public int Read(Span<short> interleaved)
    {
        var count = interleaved.Length / Channels * Channels;
        if (_float.Length < count)
        {
            _float = new float[count];
        }

        var read = _reader.ReadSamples(_float, 0, count);
        for (var i = 0; i < read; i++)
        {
            interleaved[i] = AudioDecoder.ToShort(_float[i]);
        }

        return read / Channels;
    }

    public void Rewind() => _reader.SeekTo(0);

    public void Dispose() => _reader.Dispose();
}
