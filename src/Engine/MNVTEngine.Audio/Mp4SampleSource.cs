// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-27.

using System.Runtime.InteropServices;

namespace MNVTEngine.Audio;

/// <summary>
/// AAC in MP4 (also fragmented, as SoundCloud serves it) through Windows Media Foundation's source reader, which
/// decodes it to 16-bit PCM at the file's own rate. Windows only, and only from a file: the reader opens it by path.
/// </summary>
internal sealed unsafe partial class Mp4SampleSource : IAudioSampleSource
{
    private const uint FirstAudio = 0xFFFFFFFD, AllStreams = 0xFFFFFFFE, MediaSource = 0xFFFFFFFF, EndOfStream = 0x2;
    private const int Version = 0x00020070, Lite = 1;
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), Audio = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"), Pcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid BitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669"), ChannelCount = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid SamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba"), PresentationDuration = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    private static readonly Lock StartupLock = new();
    private static bool _started;

    private nint _reader;
    private short[] _pcm = [];
    private int _pcmStart, _pcmEnd;
    private long _skip;
    private bool _ended;

    public Mp4SampleSource(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("MP4 audio is decoded by Windows Media Foundation.");
        }

        Startup();
        Check(MFCreateSourceReaderFromURL(path, 0, out _reader), "open");
        try
        {
            Check(Call(_reader, 4, AllStreams, 0), "select"); // SetStreamSelection: only the audio
            Check(Call(_reader, 4, FirstAudio, 1), "select");
            Check(MFCreateMediaType(out var wanted), "media type");
            try
            {
                SetGuid(wanted, MajorType, Audio);
                SetGuid(wanted, Subtype, Pcm);
                var bits = BitsPerSample;
                Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(wanted, 21))(wanted, &bits, 16), "bits");
                Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)Slot(_reader, 7))(_reader, FirstAudio, 0, wanted), "decoder"); // SetCurrentMediaType
            }
            finally
            {
                Release(wanted);
            }

            nint current;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(_reader, 6))(_reader, FirstAudio, &current), "format"); // GetCurrentMediaType
            (Channels, SampleRate) = ((int)GetUInt(current, ChannelCount), (int)GetUInt(current, SamplesPerSecond));
            Release(current);
            _ = new AudioClipData([], Channels, SampleRate); // validates channels and rate
            var duration = stackalloc long[3]; // PROPVARIANT: type, then the value
            var key = PresentationDuration;
            if (((delegate* unmanaged[Stdcall]<nint, uint, Guid*, long*, int>)Slot(_reader, 12))(_reader, MediaSource, &key, duration) >= 0)
            {
                Frames = duration[1] * SampleRate / 10_000_000;
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int Channels { get; }

    public int SampleRate { get; }

    public long? Frames { get; }

    public int Read(Span<short> interleaved)
    {
        var written = 0;
        while (written + Channels <= interleaved.Length && (_pcmStart < _pcmEnd || ReadSample()))
        {
            var count = Math.Min(_pcmEnd - _pcmStart, (interleaved.Length - written) / Channels * Channels);
            _pcm.AsSpan(_pcmStart, count).CopyTo(interleaved[written..]);
            (_pcmStart, written) = (_pcmStart + count, written + count);
        }

        return written / Channels;
    }

    public void Seek(long frame)
    {
        var target = Math.Max(0, frame);
        var position = stackalloc long[3];
        (position[0], position[1]) = (20, target * 10_000_000 / SampleRate); // VT_I8, in 100 ns units
        var anywhere = Guid.Empty;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, long*, int>)Slot(_reader, 8))(_reader, &anywhere, position), "seek");
        (_pcmStart, _pcmEnd, _ended, _skip) = (0, 0, false, -target - 1); // the first sample says how far it lands before
    }

    public void Dispose()
    {
        Release(_reader);
        _reader = 0;
    }

    /// <summary>Decodes the next sample into the buffer; false at the end.</summary>
    private bool ReadSample()
    {
        while (!_ended)
        {
            uint stream, flags;
            long time;
            nint sample;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint*, long*, nint*, int>)Slot(_reader, 9))(_reader, FirstAudio, 0, &stream, &flags, &time, &sample), "read");
            _ended = (flags & EndOfStream) != 0;
            if (sample == 0)
            {
                continue;
            }

            try
            {
                if (_skip < 0)
                {
                    _skip = Math.Max(0, (-_skip - 1) - (time * SampleRate / 10_000_000)); // frames between where it landed and the target
                }

                Copy(sample);
            }
            finally
            {
                Release(sample);
            }

            var dropped = (int)Math.Min(_skip, (_pcmEnd - _pcmStart) / Channels);
            (_skip, _pcmStart) = (_skip - dropped, _pcmStart + (dropped * Channels));
            if (_pcmStart < _pcmEnd)
            {
                return true;
            }
        }

        return false;
    }

    private void Copy(nint sample)
    {
        nint buffer;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(sample, 41))(sample, &buffer), "buffer"); // ConvertToContiguousBuffer
        try
        {
            byte* data;
            uint length;
            Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, null, &length), "lock");
            var count = (int)length / sizeof(short);
            if (_pcm.Length < count)
            {
                _pcm = new short[count];
            }

            new ReadOnlySpan<short>(data, count).CopyTo(_pcm);
            ((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, 4))(buffer); // Unlock
            (_pcmStart, _pcmEnd) = (0, count / Channels * Channels);
        }
        finally
        {
            Release(buffer);
        }
    }

    private static void Startup()
    {
        lock (StartupLock)
        {
            if (!_started)
            {
                Check(MFStartup(Version, Lite), "start"); // once per process; Media Foundation stays up for later files
                _started = true;
            }
        }
    }

    private static void* Slot(nint com, int index) => (*(void***)com)[index];

    private static int Call(nint com, int index, uint stream, int selected) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Slot(com, index))(com, stream, selected);

    private static void Release(nint com)
    {
        if (com != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(com, 2))(com);
        }
    }

    private static void SetGuid(nint attributes, Guid key, Guid value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(attributes, 24))(attributes, &key, &value), "media type");

    private static uint GetUInt(nint attributes, Guid key)
    {
        uint value;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Slot(attributes, 7))(attributes, &key, &value), "format");
        return value;
    }

    private static void Check(int result, string step)
    {
        if (result < 0)
        {
            throw new InvalidDataException($"Media Foundation could not decode the MP4 ({step}: 0x{result:X8}).");
        }
    }

    [LibraryImport("mfplat.dll")]
    private static partial int MFStartup(int version, int flags);

    [LibraryImport("mfplat.dll")]
    private static partial int MFCreateMediaType(out nint mediaType);

    [LibraryImport("mfreadwrite.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MFCreateSourceReaderFromURL(string url, nint attributes, out nint reader);
}
