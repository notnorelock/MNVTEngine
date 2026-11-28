// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

using System.Buffers.Binary;

namespace MNVTEngine.Audio;

/// <summary>
/// The first AAC track of a fragmented MP4's movie header (moov), and the frames its fragments (moof) list. MP4 is
/// untrusted input: every read is bounds-checked and a damaged box is an <see cref="InvalidDataException"/>.
/// </summary>
internal sealed class Mp4Track
{
    private const int MaxFrame = 1 << 16, MaxFramesPerRun = 1 << 16;

    private Mp4Track(uint id, long timescale, byte[] config, uint defaultDuration, uint defaultSize) =>
        (Id, Timescale, Config, DefaultDuration, DefaultSize) = (id, timescale, config, defaultDuration, defaultSize);

    public uint Id { get; }

    /// <summary>Time units per second of the track's frame times.</summary>
    public long Timescale { get; }

    /// <summary>The AAC decoder's configuration (AudioSpecificConfig).</summary>
    public byte[] Config { get; }

    public uint DefaultDuration { get; }

    public uint DefaultSize { get; }

    /// <exception cref="NotSupportedException">The MP4 is not fragmented.</exception>
    /// <exception cref="InvalidDataException">No AAC track, or a damaged header.</exception>
    public static Mp4Track Read(byte[] moov)
    {
        if (Child(moov, (0, moov.Length), "mvex") is not { } mvex)
        {
            throw new NotSupportedException("The MP4 file is not fragmented.");
        }

        foreach (var (type, body, end) in Boxes(moov, 0, moov.Length))
        {
            if (type != "trak" || Audio(moov, (body, end)) is not { } track)
            {
                continue;
            }

            var (duration, size) = (0u, 0u);
            foreach (var (_, trexBody, _) in Boxes(moov, mvex.Body, mvex.End).Where(b => b.Type == "trex"))
            {
                if (U32(moov, trexBody + 4) == track.Id)
                {
                    (duration, size) = (U32(moov, trexBody + 12), U32(moov, trexBody + 16));
                }
            }

            return new Mp4Track(track.Id, track.Timescale, track.Config, duration, size);
        }

        throw new InvalidDataException("The MP4 file has no AAC audio track.");
    }

    /// <summary>Adds the frames of this track that a fragment lists (<paramref name="start"/>: where the moof begins); its decode time, if it says.</summary>
    public long? ReadFragment(byte[] moof, long start, List<(long Offset, int Size, uint Duration)> frames)
    {
        long? time = null;
        foreach (var (_, traf, trafEnd) in Boxes(moof, 0, moof.Length).Where(b => b.Type == "traf"))
        {
            var (mine, dataStart, duration, size, next) = (false, start, DefaultDuration, DefaultSize, -1L);
            foreach (var (type, body, _) in Boxes(moof, traf, trafEnd))
            {
                var flags = U32(moof, body) & 0xFFFFFF;
                if (type == "tfhd")
                {
                    mine = U32(moof, body + 4) == Id;
                    var at = body + 8;
                    dataStart = (flags & 0x01) != 0 ? (long)U64(moof, (at += 8) - 8) : start; // else: from the moof
                    at += (flags & 0x02) != 0 ? 4 : 0;
                    duration = (flags & 0x08) != 0 ? U32(moof, (at += 4) - 4) : duration;
                    size = (flags & 0x10) != 0 ? U32(moof, at) : size;
                }
                else if (mine && type == "tfdt")
                {
                    time = moof[body] == 1 ? (long)U64(moof, body + 4) : U32(moof, body + 4);
                }
                else if (mine && type == "trun")
                {
                    next = Run(moof, body, flags, next >= 0 ? next : dataStart, dataStart, duration, size, frames);
                }
            }
        }

        return time;
    }

    /// <summary>A track run's frames; where the next run's data starts.</summary>
    private static long Run(byte[] moof, int body, uint flags, long offset, long dataStart, uint duration, uint size, List<(long, int, uint)> frames)
    {
        var count = U32(moof, body + 4);
        if (count > MaxFramesPerRun)
        {
            throw new InvalidDataException("An MP4 fragment lists too many frames.");
        }

        var at = body + 8;
        offset = (flags & 0x01) != 0 ? dataStart + (int)U32(moof, (at += 4) - 4) : offset;
        at += (flags & 0x04) != 0 ? 4 : 0; // the first frame's flags
        for (var i = 0; i < count; i++)
        {
            var frameDuration = (flags & 0x100) != 0 ? U32(moof, (at += 4) - 4) : duration;
            var frameSize = (flags & 0x200) != 0 ? U32(moof, (at += 4) - 4) : size;
            at += ((flags & 0x400) != 0 ? 4 : 0) + ((flags & 0x800) != 0 ? 4 : 0);
            if (frameSize is 0 or > MaxFrame)
            {
                throw new InvalidDataException("An MP4 frame has a broken size.");
            }

            frames.Add((offset, (int)frameSize, frameDuration));
            offset += frameSize;
        }

        return offset;
    }

    /// <summary>A trak's id, time scale and AAC configuration; null when it is not AAC audio.</summary>
    private static (uint Id, long Timescale, byte[] Config)? Audio(byte[] d, (int Body, int End) trak)
    {
        var mdia = Child(d, trak, "mdia");
        if (Child(d, trak, "tkhd") is not { } tkhd || mdia is null || Child(d, mdia.Value, "hdlr") is not { } hdlr || Child(d, mdia.Value, "mdhd") is not { } mdhd
            || U32(d, hdlr.Body + 8) != 0x736F756E) // 'soun'
        {
            return null;
        }

        var stsd = Child(d, mdia.Value, "minf") is { } minf && Child(d, minf, "stbl") is { } stbl ? Child(d, stbl, "stsd") : null;
        var mp4a = stsd is { } entries ? Child(d, (entries.Body + 8, entries.End), "mp4a") : null;
        var esds = mp4a is { } entry ? Child(d, (entry.Body + 28, entry.End), "esds") : null;
        if (esds is not { } descriptors || DecoderInfo(d, descriptors.Body + 4, descriptors.End) is not { } config)
        {
            return null;
        }

        var id = U32(d, tkhd.Body + (d[tkhd.Body] == 1 ? 20 : 12));
        var timescale = U32(d, mdhd.Body + (d[mdhd.Body] == 1 ? 20 : 12));
        return timescale == 0 ? throw new InvalidDataException("The MP4's audio track has no time scale.") : (id, timescale, config);
    }

    /// <summary>The DecoderSpecificInfo inside an esds's descriptors, when the decoder is AAC.</summary>
    private static byte[]? DecoderInfo(byte[] d, int at, int end)
    {
        while (at < end)
        {
            var tag = Byte(d, at++);
            var size = 0;
            for (var i = 0; i < 4; i++)
            {
                var b = Byte(d, at++);
                size = (size << 7) | (b & 0x7F);
                if ((b & 0x80) == 0)
                {
                    break;
                }
            }

            switch (tag)
            {
                case 0x03: // ES descriptor: its id, flags and their fields, then the descriptors inside
                    var flags = Byte(d, at + 2);
                    at += 3 + ((flags & 0x80) != 0 ? 2 : 0) + ((flags & 0x20) != 0 ? 2 : 0);
                    at += (flags & 0x40) != 0 ? 1 + Byte(d, at) : 0;
                    break;
                case 0x04: // decoder configuration: 0x40 is AAC (0x66-0x68 MPEG-2 AAC)
                    if (Byte(d, at) is not (0x40 or 0x66 or 0x67 or 0x68))
                    {
                        return null;
                    }

                    at += 13;
                    break;
                case 0x05:
                    return at + size <= end && size > 0 ? d[at..(at + size)] : throw new InvalidDataException("The MP4's AAC configuration is damaged.");
                default:
                    at += size;
                    break;
            }
        }

        return null;
    }

    private static (int Body, int End)? Child(byte[] d, (int Body, int End) parent, string type)
    {
        foreach (var (name, body, end) in Boxes(d, parent.Body, parent.End))
        {
            if (name == type)
            {
                return (body, end);
            }
        }

        return null;
    }

    private static IEnumerable<(string Type, int Body, int End)> Boxes(byte[] d, int at, int end)
    {
        while (at + 8 <= end)
        {
            var size = U32(d, at);
            if (size < 8 || at + size > end)
            {
                throw new InvalidDataException("An MP4 box has a broken size."); // shortcut: 64-bit sizes only for top-level boxes, which is where they occur
            }

            yield return (System.Text.Encoding.ASCII.GetString(d, at + 4, 4), at + 8, at + (int)size);
            at += (int)size;
        }
    }

    private static byte Byte(byte[] d, int at) => at >= 0 && at < d.Length ? d[at] : throw new InvalidDataException("An MP4 header is cut off.");

    private static uint U32(byte[] d, int at) =>
        at >= 0 && at + 4 <= d.Length ? BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(at)) : throw new InvalidDataException("An MP4 header is cut off.");

    private static ulong U64(byte[] d, int at) =>
        at >= 0 && at + 8 <= d.Length ? BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(at)) : throw new InvalidDataException("An MP4 header is cut off.");
}
