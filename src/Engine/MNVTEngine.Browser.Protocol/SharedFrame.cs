using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace MNVTEngine.BrowserHosting;

/// <summary>
/// One browser's pixels in shared memory: a header, then <c>width * height</c> RGBA pixels (top row first, straight
/// alpha). The host writes dirty rectangles and grows the shared dirty rectangle; the game copies that rectangle out
/// and clears it. A named mutex guards the copies; the game only ever tries it (never waits), so a busy host makes the
/// game skip a frame, and frames painted before the game reads them merge into one rectangle. Each resize creates a new
/// mapping (a new generation), so readers never see a mapping change size.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SharedFrame : IDisposable
{
    /// <summary>width, height, dirty x, y, width, height, frames painted (int64).</summary>
    public const int HeaderBytes = 64;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly Mutex _lock;
    private readonly unsafe byte* _base;

    private unsafe SharedFrame(MemoryMappedFile file, Mutex mutex, int width, int height, string name)
    {
        (_file, _lock, Width, Height, Name) = (file, mutex, width, height, name);
        _view = file.CreateViewAccessor(0, Bytes(width, height));
        byte* pointer = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _base = pointer + _view.PointerOffset;
    }

    public string Name { get; }

    public int Width { get; }

    public int Height { get; }

    public static long Bytes(int width, int height) => HeaderBytes + ((long)width * height * 4);

    /// <summary>The name of a browser's frame mapping for one size.</summary>
    public static string NameFor(string session, int browser, int generation) => $"mnvt-browser-{session}-{browser}-{generation}";

    /// <summary>The host: creates the mapping (all pixels transparent black, nothing dirty).</summary>
    public static SharedFrame Create(string name, int width, int height)
    {
        var file = MemoryMappedFile.CreateNew(name, Bytes(width, height), MemoryMappedFileAccess.ReadWrite);
        var mutex = new Mutex(false, name + "-lock");
        var frame = new SharedFrame(file, mutex, width, height, name);
        frame.WriteHeader(0, 0, 0, 0, 0);
        return frame;
    }

    /// <summary>The game: opens the host's mapping; null when it does not exist (yet).</summary>
    public static SharedFrame? Open(string name, int width, int height)
    {
        try
        {
            var file = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
            if (!Mutex.TryOpenExisting(name + "-lock", out var mutex))
            {
                file.Dispose();
                return null;
            }

            return new SharedFrame(file, mutex, width, height, name);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The host: copies a dirty rectangle of a BGRA (premultiplied) paint buffer, converted to RGBA, and grows the
    /// shared dirty rectangle. <paramref name="unpremultiply"/> converts to straight alpha (transparent browsers).
    /// </summary>
    public unsafe void Write(nint bgra, int sourceWidth, int x, int y, int width, int height, bool unpremultiply)
    {
        if (width <= 0 || height <= 0 || x < 0 || y < 0 || x + width > Math.Min(Width, sourceWidth) || y + height > Height)
        {
            return;
        }

        Acquire(Timeout.Infinite);
        try
        {
            var pixels = (uint*)(_base + HeaderBytes);
            var source = (uint*)bgra;
            for (var row = y; row < y + height; row++)
            {
                var from = source + ((long)row * sourceWidth) + x;
                var to = pixels + ((long)row * Width) + x;
                for (var i = 0; i < width; i++)
                {
                    var p = from[i]; // bytes B, G, R, A: 0xAARRGGBB
                    var swizzled = (p & 0xFF00FF00u) | ((p >> 16) & 0xFFu) | ((p & 0xFFu) << 16);
                    to[i] = unpremultiply ? Unpremultiply(swizzled) : swizzled;
                }
            }

            var header = (int*)_base;
            var (dx, dy, dw, dh) = (header[2], header[3], header[4], header[5]);
            if (dw > 0 && dh > 0)
            {
                var (minX, minY) = (Math.Min(dx, x), Math.Min(dy, y));
                (dw, dh) = (Math.Max(dx + dw, x + width) - minX, Math.Max(dy + dh, y + height) - minY);
                (dx, dy) = (minX, minY);
            }
            else
            {
                (dx, dy, dw, dh) = (x, y, width, height);
            }

            WriteHeader(dx, dy, dw, dh, *(long*)(_base + 24) + 1);
        }
        finally
        {
            _lock.ReleaseMutex();
        }
    }

    /// <summary>
    /// The game: copies the dirty rectangle (tightly packed RGBA rows) into <paramref name="destination"/> and clears
    /// it. False when nothing changed or the host is writing right now (try again next frame).
    /// </summary>
    /// <param name="destination">Grown when too small; reused across frames.</param>
    public unsafe bool TryTake(ref byte[] destination, out (int X, int Y, int Width, int Height) dirty, out long frames)
    {
        dirty = default;
        frames = 0;
        if (!Acquire(0))
        {
            return false;
        }

        try
        {
            var header = (int*)_base;
            frames = *(long*)(_base + 24);
            dirty = (header[2], header[3], header[4], header[5]);
            if (dirty.Width <= 0 || dirty.Height <= 0)
            {
                return false;
            }

            var bytes = dirty.Width * dirty.Height * 4;
            if (destination.Length < bytes)
            {
                destination = new byte[Math.Max(bytes, destination.Length * 2)];
            }

            fixed (byte* target = destination)
            {
                var pixels = _base + HeaderBytes;
                var rowBytes = dirty.Width * 4;
                for (var row = 0; row < dirty.Height; row++)
                {
                    Buffer.MemoryCopy(pixels + ((((long)(dirty.Y + row) * Width) + dirty.X) * 4), target + ((long)row * rowBytes), rowBytes, rowBytes);
                }
            }

            WriteHeader(0, 0, 0, 0, frames);
            return true;
        }
        finally
        {
            _lock.ReleaseMutex();
        }
    }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
        _lock.Dispose();
    }

    private bool Acquire(int milliseconds)
    {
        try
        {
            return _lock.WaitOne(milliseconds);
        }
        catch (AbandonedMutexException)
        {
            return true; // the other process died holding it: the mutex is ours now, the pixels may be partial
        }
    }

    private unsafe void WriteHeader(int x, int y, int width, int height, long frames)
    {
        var header = (int*)_base;
        (header[0], header[1], header[2], header[3], header[4], header[5]) = (Width, Height, x, y, width, height);
        *(long*)(_base + 24) = frames;
    }

    private static uint Unpremultiply(uint rgba)
    {
        var a = rgba >> 24;
        if (a is 0 or 255)
        {
            return rgba;
        }

        uint Channel(int shift) => Math.Min(255u, ((rgba >> shift) & 0xFFu) * 255u / a) << shift;
        return Channel(0) | Channel(8) | Channel(16) | (a << 24);
    }
}
