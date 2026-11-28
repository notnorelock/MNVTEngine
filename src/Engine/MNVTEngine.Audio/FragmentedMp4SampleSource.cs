// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

using SharpJaad.AAC;

namespace MNVTEngine.Audio;

/// <summary>
/// AAC in fragmented MP4 (what HLS and DASH serve, SoundCloud among them), split here and decoded with SharpJaad on any
/// platform. The file is read front to back, a fragment at a time, so it also plays a file still being written (give it
/// a stream whose reads wait for the data). Seeking walks the fragments to the target and decodes one frame ahead of it.
/// An MP4 without fragments is refused with <see cref="NotSupportedException"/> (<see cref="AudioDecoder"/> hands it to
/// Media Foundation).
/// </summary>
internal sealed class FragmentedMp4SampleSource : IAudioSampleSource
{
    private const int MaxHeaderBox = 8 << 20;

    private readonly Stream _stream;
    private readonly Mp4Track _track;
    private readonly List<(long Position, long Time)> _fragments = [];
    private readonly List<(long Offset, int Size, uint Duration)> _samples = [];
    private readonly SampleBuffer _buffer = new();
    private Decoder _decoder;
    private short[] _pcm = [];
    private int _sample, _pcmStart, _pcmEnd;
    private long _nextBox, _time, _seekTime = -1, _skip;

    public FragmentedMp4SampleSource(Stream stream)
    {
        _stream = AudioDecoder.Seekable(stream);
        while (true)
        {
            var box = ReadBox(_nextBox) ?? throw new InvalidDataException("The MP4 file has no movie header (moov).");
            _nextBox = box.End;
            if (box.Type == "moov")
            {
                _track = Mp4Track.Read(ReadBody(box));
                break;
            }
        }

        _buffer.SetBigEndian(false);
        _decoder = NewDecoder();
        if (!DecodeNext())
        {
            throw new InvalidDataException("The MP4 file has no AAC audio.");
        }

        (Channels, SampleRate) = (_buffer.Channels, _buffer.SampleRate);
        if (Channels is not (1 or 2))
        {
            throw new InvalidDataException($"The MP4's AAC has {Channels} channels; only mono and stereo play.");
        }
    }

    public int Channels { get; }

    public int SampleRate { get; }

    /// <summary>Unknown: a fragmented file does not say its length up front (shortcut: read mehd when a caller needs it).</summary>
    public long? Frames => null;

    public int Read(Span<short> interleaved)
    {
        var written = 0;
        while (written + Channels <= interleaved.Length && (_pcmStart < _pcmEnd || DecodeNext()))
        {
            var count = Math.Min(_pcmEnd - _pcmStart, (interleaved.Length - written) / Channels * Channels);
            _pcm.AsSpan(_pcmStart, count).CopyTo(interleaved[written..]);
            (_pcmStart, written) = (_pcmStart + count, written + count);
        }

        return written / Channels;
    }

    public void Seek(long frame)
    {
        _seekTime = Math.Max(0, frame) * _track.Timescale / SampleRate;
        var start = _fragments.FindLastIndex(f => f.Time <= _seekTime);
        _nextBox = _fragments[Math.Max(0, start)].Position;
        _samples.Clear();
        (_sample, _pcmStart, _pcmEnd, _skip) = (0, 0, 0, 0);
        _decoder = NewDecoder(); // a fresh state: the frame before the target settles it
    }

    public void Dispose() => _stream.Dispose();

    /// <summary>Decodes the next frame into the buffer (skipping to a seek's target first); false at the end.</summary>
    private bool DecodeNext()
    {
        while (_sample < _samples.Count || NextFragment())
        {
            var (offset, size, duration) = _samples[_sample++];
            var time = _time;
            _time += duration;
            if (_seekTime >= 0 && time + (2L * duration) <= _seekTime)
            {
                continue; // before the target, and not the frame just ahead of it
            }

            var frame = new byte[size];
            _stream.Position = offset;
            if (_stream.ReadAtLeast(frame, size, throwOnEndOfStream: false) < size)
            {
                return false; // cut off (a download that stopped): its end
            }

            try
            {
                _decoder.DecodeFrame(frame, _buffer);
            }
            catch (AACException ex)
            {
                throw new InvalidDataException($"A damaged AAC frame: {ex.Message}", ex);
            }

            var samples = _buffer.Data.Length / sizeof(short);
            if (_pcm.Length < samples)
            {
                _pcm = new short[samples];
            }

            Buffer.BlockCopy(_buffer.Data, 0, _pcm, 0, samples * sizeof(short));
            var channels = Math.Max(1, _buffer.Channels);
            (_pcmStart, _pcmEnd) = (0, samples / channels * channels);
            if (_seekTime >= 0)
            {
                (_skip, _seekTime) = (Math.Max(0, (_seekTime - time) * _buffer.SampleRate / _track.Timescale), -1);
            }

            var dropped = (int)Math.Min(_skip, _pcmEnd / channels);
            (_skip, _pcmStart) = (_skip - dropped, dropped * channels);
            if (_pcmStart < _pcmEnd)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads on to the next fragment (moof) of the track and lists its frames; false at the end of the file.</summary>
    private bool NextFragment()
    {
        _sample = 0;
        _samples.Clear();
        while (ReadBox(_nextBox) is { } box)
        {
            _nextBox = box.End;
            if (box.Type != "moof")
            {
                continue;
            }

            byte[] body;
            try
            {
                body = ReadBody(box);
            }
            catch (EndOfStreamException)
            {
                return false; // cut off inside the fragment's header: the end
            }

            var time = _track.ReadFragment(body, box.Start, _samples);
            if (time is { } decoded)
            {
                _time = decoded;
            }

            if (_fragments.Count == 0 || _fragments[^1].Position < box.Start)
            {
                _fragments.Add((box.Start, _time));
            }

            if (_samples.Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    private Decoder NewDecoder()
    {
        try
        {
            return new Decoder(_track.Config);
        }
        catch (AACException ex)
        {
            throw new InvalidDataException($"The MP4's AAC configuration is not supported: {ex.Message}", ex);
        }
    }

    /// <summary>The box at <paramref name="position"/>; null at the end of the file (or cut off inside its header).</summary>
    private (string Type, long Start, long Body, long End)? ReadBox(long position)
    {
        Span<byte> header = stackalloc byte[16];
        _stream.Position = position;
        if (_stream.ReadAtLeast(header[..8], 8, throwOnEndOfStream: false) < 8)
        {
            return null;
        }

        long size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header), body = position + 8;
        var type = System.Text.Encoding.ASCII.GetString(header.Slice(4, 4));
        if (size == 1)
        {
            if (_stream.ReadAtLeast(header[8..], 8, throwOnEndOfStream: false) < 8)
            {
                return null;
            }

            (size, body) = ((long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(header[8..]), position + 16);
        }

        if (size != 0 && size < body - position)
        {
            throw new InvalidDataException($"The MP4 box '{type}' has a broken size.");
        }

        return (type, position, body, size == 0 ? long.MaxValue : position + size); // 0: to the end of the file
    }

    private byte[] ReadBody((string Type, long Start, long Body, long End) box)
    {
        if (box.End - box.Body > MaxHeaderBox)
        {
            throw new InvalidDataException($"The MP4 box '{box.Type}' is too large.");
        }

        var body = new byte[box.End - box.Body];
        _stream.Position = box.Body;
        _stream.ReadExactly(body);
        return body;
    }
}
