// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-27.

using System.Buffers.Binary;
using Concentus;

namespace MNVTEngine.Audio;

/// <summary>
/// The first Opus track of a WebM (Matroska) file, decoded with Concentus at 48 kHz: what YouTube serves as audio. The
/// blocks are indexed on open, so seeking jumps to the block and decodes 80 ms ahead of it for the decoder to settle.
/// </summary>
internal sealed class WebmOpusSampleSource : IAudioSampleSource
{
    public const uint EbmlMagic = 0x1A45DFA3;
    private const uint Segment = 0x18538067, Info = 0x1549A966, Tracks = 0x1654AE6B, TrackEntry = 0xAE, Cluster = 0x1F43B675, BlockGroup = 0xA0;
    private const uint TimecodeScale = 0x2AD7B1, Duration = 0x4489, TrackNumber = 0xD7, CodecId = 0x86, CodecPrivate = 0x63A2;
    private const uint Timecode = 0xE7, SimpleBlock = 0xA3, Block = 0xA1;
    private const int Rate = 48000, MaxFrame = 5760, PreRollFrames = Rate * 80 / 1000;

    private readonly Stream _stream;
    private readonly List<(long Offset, int Length, long Frame)> _blocks = [];
    private readonly short[] _pcm;
    private readonly int _preSkip;
    private byte[] _packet = new byte[1024];
    private IOpusDecoder _decoder;
    private int _next, _pcmStart, _pcmEnd;
    private long _skip;

    public WebmOpusSampleSource(Stream stream)
    {
        _stream = stream.CanSeek ? stream : Copy(stream);
        var (scale, duration, opusTrack, head, clusterTime) = (1_000_000L, 0.0, 0L, (byte[]?)null, 0L);
        var found = new List<(long Offset, int Length, long Track, long Time)>();
        long track = 0;
        string? codec = null;
        byte[]? codecPrivate = null;
        Walk(_stream.Length);
        if (head is not { Length: >= 19 } || !head.AsSpan(0, 8).SequenceEqual("OpusHead"u8) || head[9] is not (1 or 2))
        {
            throw new InvalidDataException("The WebM file has no mono or stereo Opus track.");
        }

        (Channels, _preSkip) = (head[9], BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10)));
        foreach (var (offset, length, number, time) in found.Where(b => b.Track == opusTrack))
        {
            _blocks.Add((offset, length, time * scale * Rate / 1_000_000_000));
        }

        Frames = duration > 0 ? Math.Max(0, (long)(duration * scale * Rate / 1e9) - _preSkip) : null;
        (_pcm, _decoder, _skip) = (new short[MaxFrame * Channels], OpusCodecFactory.CreateDecoder(Rate, Channels), _preSkip);

        void Walk(long end)
        {
            while (_stream.Position < end)
            {
                var id = (uint)ReadVint(keepMarker: true, out _);
                var size = ReadVint(keepMarker: false, out var unknown);
                var start = _stream.Position;
                var stop = unknown ? end : Math.Min(end, start + size);
                switch (id)
                {
                    case Segment or Info or Tracks or Cluster or BlockGroup:
                        Walk(stop);
                        break;
                    case TrackEntry:
                        (track, codec, codecPrivate) = (0, null, null);
                        Walk(stop);
                        if (codec == "A_OPUS" && head is null)
                        {
                            (opusTrack, head) = (track, codecPrivate);
                        }

                        break;
                    case TimecodeScale: scale = (long)ReadUInt(size); break;
                    case Duration: duration = size == 4 ? BinaryPrimitives.ReadSingleBigEndian(ReadBytes(4)) : BinaryPrimitives.ReadDoubleBigEndian(ReadBytes(8)); break;
                    case TrackNumber: track = (long)ReadUInt(size); break;
                    case CodecId: codec = System.Text.Encoding.ASCII.GetString(ReadBytes(size)).TrimEnd('\0'); break;
                    case CodecPrivate: codecPrivate = ReadBytes(size); break;
                    case Timecode: clusterTime = (long)ReadUInt(size); break;
                    case SimpleBlock or Block:
                        var number = ReadVint(keepMarker: false, out _);
                        var header = ReadBytes(3); // relative time (int16), flags
                        if ((header[2] & 0x06) != 0)
                        {
                            throw new InvalidDataException("Laced WebM blocks are not supported."); // shortcut: YouTube never laces Opus; add lacing when a source does
                        }

                        found.Add((_stream.Position, (int)(stop - _stream.Position), number, clusterTime + BinaryPrimitives.ReadInt16BigEndian(header)));
                        break;
                }

                _stream.Position = stop;
            }
        }
    }

    public int Channels { get; }

    public int SampleRate => Rate;

    public long? Frames { get; }

    public int Read(Span<short> interleaved)
    {
        var written = 0;
        while (written + Channels <= interleaved.Length)
        {
            if (_pcmStart == _pcmEnd && !DecodeNext())
            {
                break;
            }

            var count = Math.Min(_pcmEnd - _pcmStart, (interleaved.Length - written) / Channels * Channels);
            _pcm.AsSpan(_pcmStart, count).CopyTo(interleaved[written..]);
            (_pcmStart, written) = (_pcmStart + count, written + count);
        }

        return written / Channels;
    }

    public void Seek(long frame)
    {
        var target = Math.Max(0, frame) + _preSkip;
        var index = _blocks.FindLastIndex(b => b.Frame <= target - PreRollFrames);
        _next = Math.Max(0, index);
        _skip = _blocks.Count > 0 ? target - _blocks[_next].Frame : 0;
        (_pcmStart, _pcmEnd) = (0, 0);
        (_decoder as IDisposable)?.Dispose();
        _decoder = OpusCodecFactory.CreateDecoder(Rate, Channels); // a fresh state: the pre-roll settles it
    }

    public void Dispose()
    {
        (_decoder as IDisposable)?.Dispose();
        _stream.Dispose();
    }

    /// <summary>Decodes the next block into the buffer, dropping what <see cref="_skip"/> says; false at the end.</summary>
    private bool DecodeNext()
    {
        while (_next < _blocks.Count)
        {
            var (offset, length, _) = _blocks[_next++];
            if (_packet.Length < length)
            {
                _packet = new byte[length];
            }

            _stream.Position = offset;
            _stream.ReadExactly(_packet, 0, length);
            int frames;
            try
            {
                frames = _decoder.Decode(_packet.AsSpan(0, length), _pcm, MaxFrame);
            }
            catch (OpusException ex)
            {
                throw new InvalidDataException($"A damaged Opus packet: {ex.Message}", ex);
            }

            var dropped = (int)Math.Clamp(_skip, 0, frames);
            _skip -= dropped;
            (_pcmStart, _pcmEnd) = (dropped * Channels, frames * Channels);
            if (_pcmStart < _pcmEnd)
            {
                return true;
            }
        }

        return false;
    }

    private static MemoryStream Copy(Stream stream)
    {
        var copy = new MemoryStream();
        stream.CopyTo(copy);
        stream.Dispose();
        copy.Position = 0;
        return copy;
    }

    /// <summary>An EBML variable-length integer: an element id (with its marker bits) or a size (all ones: unknown).</summary>
    private long ReadVint(bool keepMarker, out bool unknown)
    {
        var first = _stream.ReadByte();
        if (first <= 0)
        {
            throw new EndOfStreamException("The WebM file ends inside an element.");
        }

        var length = System.Numerics.BitOperations.LeadingZeroCount((uint)first) - 23;
        var value = (long)(keepMarker ? first : first & (0xFF >> length));
        var allOnes = value == (0xFF >> length);
        for (var i = 1; i < length; i++)
        {
            var next = _stream.ReadByte();
            if (next < 0)
            {
                throw new EndOfStreamException("The WebM file ends inside an element.");
            }

            (value, allOnes) = ((value << 8) | (uint)next, allOnes && next == 0xFF);
        }

        unknown = !keepMarker && allOnes;
        return value;
    }

    private byte[] ReadBytes(long count)
    {
        if (count is < 0 or > 1 << 20)
        {
            throw new InvalidDataException("A WebM header element is too large.");
        }

        var bytes = new byte[count];
        _stream.ReadExactly(bytes);
        return bytes;
    }

    private ulong ReadUInt(long size) => ReadBytes(Math.Min(size, 8)).Aggregate(0UL, (value, b) => (value << 8) | b);
}
