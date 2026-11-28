// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

using System.Buffers.Binary;
using Concentus;

namespace MNVTEngine.Audio;

/// <summary>
/// The first Opus track of a WebM (Matroska) file, decoded with Concentus at 48 kHz: what YouTube serves as audio. The
/// file is read front to back, a cluster at a time as playback reaches it, so it also plays a file still being written
/// (give it a stream whose reads wait for the data). Seeking indexes on to the block and decodes 80 ms ahead of it for
/// the decoder to settle.
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
    private long _scale = 1_000_000, _opusTrack;
    private byte[] _packet = new byte[1024];
    private IOpusDecoder _decoder;
    private int _next, _pcmStart, _pcmEnd;
    private long _skip, _scan = -1, _segmentEnd = long.MaxValue, _clusterTime;

    public WebmOpusSampleSource(Stream stream)
    {
        _stream = AudioDecoder.Seekable(stream);
        var (duration, head, track) = (0.0, (byte[]?)null, 0L);
        string? codec = null;
        byte[]? codecPrivate = null;
        while (_scan < 0 && _stream.Position < _segmentEnd && TryReadElement(out var id, out var stop, out var at))
        {
            switch (id)
            {
                case Segment:
                    _segmentEnd = stop;
                    continue; // its children follow
                case Cluster:
                    _scan = at; // the blocks are indexed as playback reaches them
                    continue;
                default:
                    Walk(id, stop);
                    _stream.Position = stop;
                    break;
            }
        }

        if (head is not { Length: >= 19 } || !head.AsSpan(0, 8).SequenceEqual("OpusHead"u8) || head[9] is not (1 or 2))
        {
            throw new InvalidDataException("The WebM file has no mono or stereo Opus track.");
        }

        (Channels, _preSkip) = (head[9], BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10)));
        Frames = duration > 0 ? Math.Max(0, (long)(duration * _scale * Rate / 1e9) - _preSkip) : null;
        (_pcm, _decoder, _skip) = (new short[MaxFrame * Channels], OpusCodecFactory.CreateDecoder(Rate, Channels), _preSkip);

        void Walk(uint id, long stop)
        {
            switch (id)
            {
                case Info or Tracks:
                    while (_stream.Position < stop && TryReadElement(out var child, out var childStop, out _))
                    {
                        Walk(child, childStop);
                        _stream.Position = childStop;
                    }

                    break;
                case TrackEntry:
                    (track, codec, codecPrivate) = (0, null, null);
                    Walk(Tracks, stop);
                    if (codec == "A_OPUS" && head is null)
                    {
                        (_opusTrack, head) = (track, codecPrivate);
                    }

                    break;
                case TimecodeScale: _scale = (long)ReadUInt(stop - _stream.Position); break;
                case Duration: duration = stop - _stream.Position == 4 ? BinaryPrimitives.ReadSingleBigEndian(ReadBytes(4)) : BinaryPrimitives.ReadDoubleBigEndian(ReadBytes(8)); break;
                case TrackNumber: track = (long)ReadUInt(stop - _stream.Position); break;
                case CodecId: codec = System.Text.Encoding.ASCII.GetString(ReadBytes(stop - _stream.Position)).TrimEnd('\0'); break;
                case CodecPrivate: codecPrivate = ReadBytes(stop - _stream.Position); break;
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
        while ((_blocks.Count == 0 || _blocks[^1].Frame < target) && ScanCluster())
        {
            // indexes on to the target (waiting for a file that is still being written)
        }

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
        while (_next < _blocks.Count || ScanCluster())
        {
            if (_next == _blocks.Count)
            {
                continue; // a cluster without blocks of this track
            }

            var (offset, length, _) = _blocks[_next++];
            if (_packet.Length < length)
            {
                _packet = new byte[length];
            }

            _stream.Position = offset;
            if (_stream.ReadAtLeast(_packet.AsSpan(0, length), length, throwOnEndOfStream: false) < length)
            {
                return false; // cut off (a download that stopped): its end
            }

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

    /// <summary>Indexes the next cluster's blocks of the Opus track; false at the end of the segment or the file.</summary>
    private bool ScanCluster()
    {
        while (_scan >= 0)
        {
            _stream.Position = _scan;
            if (_scan >= _segmentEnd || !TryReadElement(out var id, out var stop, out _))
            {
                _scan = -1;
                return false;
            }

            _scan = stop;
            if (id == Cluster)
            {
                IndexBlocks(stop);
                return true;
            }
        }

        return false;
    }

    private void IndexBlocks(long stop)
    {
        while (_stream.Position < stop && TryReadElement(out var id, out var end, out _))
        {
            switch (id)
            {
                case Cluster or BlockGroup: // a cluster inside: the one before had an unknown size (shortcut: read to the end then, not as the file grows)
                    IndexBlocks(end);
                    break;
                case Timecode:
                    _clusterTime = (long)ReadUInt(end - _stream.Position);
                    break;
                case SimpleBlock or Block:
                    var number = ReadVint(keepMarker: false, out _);
                    var header = ReadBytes(3); // relative time (int16), flags
                    if ((header[2] & 0x06) != 0)
                    {
                        throw new InvalidDataException("Laced WebM blocks are not supported."); // shortcut: YouTube never laces Opus; add lacing when a source does
                    }

                    if (end - _stream.Position is < 0 or > 1 << 20)
                    {
                        throw new InvalidDataException("A WebM block is too large.");
                    }

                    if (number == _opusTrack)
                    {
                        _blocks.Add((_stream.Position, (int)(end - _stream.Position), (_clusterTime + BinaryPrimitives.ReadInt16BigEndian(header)) * _scale * Rate / 1_000_000_000));
                    }

                    break;
            }

            _stream.Position = end;
        }
    }

    /// <summary>The next element's id and where it ends (an unknown size: the segment's end); false at the end of the file.</summary>
    private bool TryReadElement(out uint id, out long stop, out long at)
    {
        (at, id, stop) = (_stream.Position, 0, _stream.Position);
        var first = _stream.ReadByte();
        if (first < 0)
        {
            return false;
        }

        try
        {
            id = (uint)ReadVint(keepMarker: true, out _, first);
            var size = ReadVint(keepMarker: false, out var unknown);
            stop = unknown ? _segmentEnd : _stream.Position + size;
            return true;
        }
        catch (EndOfStreamException)
        {
            return false; // cut off inside a header: the end
        }
    }

    /// <summary>An EBML variable-length integer: an element id (with its marker bits) or a size (all ones: unknown).</summary>
    private long ReadVint(bool keepMarker, out bool unknown, int first = -1)
    {
        first = first < 0 ? _stream.ReadByte() : first;
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
