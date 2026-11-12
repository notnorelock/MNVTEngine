using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace MNVTEngine.BrowserHosting;

/// <summary>
/// One browser's page audio in shared memory: a ring of interleaved 16-bit PCM frames the host writes as Chromium
/// produces them and the game reads once a frame into its mixer. One writer, one reader, no lock: the writer publishes
/// the frame count after the samples. When the game falls more than the ring behind it skips ahead (the oldest audio is
/// lost, playback continues).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SharedAudio : IDisposable
{
    /// <summary>channels, sample rate, capacity in frames (int32 each); frames written (int64 at 16).</summary>
    public const int HeaderBytes = 64;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly unsafe byte* _base;
    private long _read;

    private unsafe SharedAudio(MemoryMappedFile file, long bytes, string name)
    {
        (_file, Name) = (file, name);
        _view = file.CreateViewAccessor(0, bytes);
        byte* pointer = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _base = pointer + _view.PointerOffset;
    }

    public string Name { get; }

    public unsafe int Channels => ((int*)_base)[0];

    public unsafe int SampleRate => ((int*)_base)[1];

    public unsafe int CapacityFrames => ((int*)_base)[2];

    private unsafe long* Written => (long*)(_base + 16);

    private unsafe short* Samples => (short*)(_base + HeaderBytes);

    public static string NameFor(string session, int browser, int generation) => $"mnvt-browser-{session}-{browser}-audio-{generation}";

    /// <summary>The host: a ring of <paramref name="seconds"/> of audio.</summary>
    public static unsafe SharedAudio Create(string name, int channels, int sampleRate, float seconds = 1f)
    {
        var capacity = Math.Max(1024, (int)(sampleRate * seconds));
        var bytes = HeaderBytes + ((long)capacity * channels * sizeof(short));
        var audio = new SharedAudio(MemoryMappedFile.CreateNew(name, bytes, MemoryMappedFileAccess.ReadWrite), bytes, name);
        var header = (int*)audio._base;
        (header[0], header[1], header[2]) = (channels, sampleRate, capacity);
        *audio.Written = 0;
        return audio;
    }

    /// <summary>The game: opens the host's ring; null when it does not exist (any more).</summary>
    public static unsafe SharedAudio? Open(string name)
    {
        try
        {
            var file = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
            int[] header = new int[3];
            using (var probe = file.CreateViewAccessor(0, HeaderBytes))
            {
                probe.ReadArray(0, header, 0, 3);
            }

            var audio = new SharedAudio(file, HeaderBytes + ((long)header[2] * header[0] * sizeof(short)), name);
            audio._read = Volatile.Read(ref *audio.Written); // start at "now": no stale audio
            return audio;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The host: appends CEF's planar float frames (one pointer per channel) as interleaved 16-bit PCM.</summary>
    public unsafe void Write(nint planar, int frames)
    {
        var channels = Channels;
        var capacity = CapacityFrames;
        var planes = (float**)planar;
        var written = *Written;
        for (var i = 0; i < frames; i++)
        {
            var slot = Samples + ((written + i) % capacity * channels);
            for (var c = 0; c < channels; c++)
            {
                slot[c] = (short)(Math.Clamp(planes[c][i], -1f, 1f) * short.MaxValue);
            }
        }

        Volatile.Write(ref *Written, written + frames); // after the samples: the reader never sees unwritten frames
    }

    /// <summary>The game: copies the frames written since the last call (up to the buffer) as interleaved samples.</summary>
    /// <returns>Frames copied.</returns>
    public unsafe int Read(Span<short> interleaved)
    {
        var channels = Channels;
        var capacity = CapacityFrames;
        var written = Volatile.Read(ref *Written);
        if (written - _read > capacity)
        {
            _read = written - capacity; // the game fell a whole ring behind: skip the lost audio
        }

        var frames = (int)Math.Min(written - _read, interleaved.Length / channels);
        for (var i = 0; i < frames; i++)
        {
            new ReadOnlySpan<short>(Samples + ((_read + i) % capacity * channels), channels).CopyTo(interleaved[(i * channels)..]);
        }

        _read += frames;
        return frames;
    }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}
