using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MNVTEngine.Assets;
using MNVTEngine.Graphics;

namespace MNVTEngine.Runtime;

/// <param name="BudgetBytes">Full-resolution streamed textures beyond this are released (least recently drawn first); 0 = only under memory pressure.</param>
/// <param name="UnusedFrames">A texture not drawn for this many frames may lose its full image, and is unloaded by <see cref="ResourceCache.UnloadUnused"/>.</param>
public sealed record TextureStreamingOptions(long BudgetBytes, long UnusedFrames);

/// <summary>What texture streaming and asset loading did (diagnostics).</summary>
/// <param name="Proxies">Streamed textures showing their proxy now.</param>
/// <param name="FullResolution">Streamed textures showing their full image.</param>
/// <param name="FullBytes">GPU memory of the full and reduced images of streamed textures (with mipmaps; proxies not counted).</param>
/// <param name="Pending">Full images being read and decoded in the background.</param>
/// <param name="Downgrades">Full images released (budget, memory pressure) since start.</param>
/// <param name="Unloaded">Textures unloaded as unused since start.</param>
/// <param name="AverageLoad">Mean duration of the last 32 texture loads (read, decode, upload); null before any.</param>
/// <param name="MaxLoad">Longest of those.</param>
/// <param name="LastSceneLoad">Duration of the last scene load, from request to start; null before any.</param>
/// <param name="Reduced">Streamed textures showing a reduced level (drawn small enough not to need the full image).</param>
public readonly record struct AssetLoadStatistics(
    int Proxies, int FullResolution, long FullBytes, int Pending, long Downgrades, long Unloaded, TimeSpan? AverageLoad, TimeSpan? MaxLoad, TimeSpan? LastSceneLoad, int Reduced = 0);

/// <summary>
/// Texture streaming: in exported games a large texture first shows its build-time proxy (<see cref="TextureProxy"/>).
/// Renderers report how large they draw it (<see cref="Texture2D.ReportScreenScale"/>), and <see cref="Update"/> loads
/// the smallest level that is still sharp at that size: the full image, a reduced level (1/2, 1/4, ...), or the proxy
/// alone. Levels are read and decoded on a worker and swapped in on the graphics thread; the <see cref="Texture2D"/>
/// object stays the same throughout (<see cref="IGraphicsDevice.ReplaceTexture"/>), so materials and models holding it
/// simply draw sharper or softer. A texture drawn without a size report needs its full image. Over the budget, or when
/// the system or GPU runs short of memory, textures not drawn for a while go back to their proxy; drawing one again
/// streams it in again.
/// </summary>
public sealed partial class ResourceCache
{
    private const int LoadTimeWindow = 32;
    private readonly Dictionary<Guid, StreamedTexture> _streamed = [];
    private readonly Dictionary<Guid, long> _loadedFrame = [];
    private readonly Queue<TimeSpan> _loadTimes = new();
    private TextureStreamingOptions? _streaming;
    private long _downgrades;
    private long _unloaded;
    private long _gpuShortUntil;
    private bool _pressure;
    private TimeSpan? _lastSceneLoad;

    /// <summary>Turns texture streaming on (exported games; needs proxies, which builds write, and a backend that can replace textures).</summary>
    public void EnableTextureStreaming(TextureStreamingOptions options) => _streaming = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>True when the texture would be streamed: scene loading leaves its decoding to the stream.</summary>
    public bool CanStream(Guid guid) =>
        _streaming is not null && _device.SupportsTextureStreaming && _assets.Content.FileExists(AssetDatabase.ArtifactEntry(guid, TextureProxy.ArtifactName));

    public AssetLoadStatistics LoadStatistics
    {
        get
        {
            var proxies = _streamed.Values.Count(s => s.Resident == s.ProxyLevel);
            var full = _streamed.Values.Count(s => s.Resident == 0);
            return new AssetLoadStatistics(
                proxies, full, _streamed.Values.Sum(s => s.Bytes(s.Resident)), _streamed.Values.Count(s => s.Pending is not null), _downgrades, _unloaded,
                _loadTimes.Count == 0 ? null : TimeSpan.FromTicks((long)_loadTimes.Average(t => t.Ticks)), _loadTimes.Count == 0 ? null : _loadTimes.Max(), _lastSceneLoad,
                _streamed.Count - proxies - full);
        }
    }

    /// <summary>Records how long a scene load took (from request to start), for diagnostics.</summary>
    public void RecordSceneLoad(TimeSpan duration) => _lastSceneLoad = duration;

    /// <summary>
    /// Once per frame on the graphics thread: swaps in levels that finished loading, loads the level each drawn texture
    /// needs at its drawn size (textures showing only their proxy are checked every frame, the others every 15), and
    /// releases images over the budget or under memory pressure.
    /// </summary>
    public void Update()
    {
        if (_streaming is not { } options || _streamed.Count == 0)
        {
            return;
        }

        var frame = _device.FrameNumber;
        foreach (var (guid, stream) in _streamed)
        {
            if (stream.Pending is { IsCompleted: true } pending)
            {
                Complete(guid, stream, pending);
            }
        }

        var full = frame % 15 == 0;
        if (full)
        {
            _pressure = UnderMemoryPressure(frame);
        }

        var pressure = _pressure;
        var used = _streamed.Values.Sum(s => s.Bytes(s.Resident));
        foreach (var (guid, stream) in _streamed)
        {
            if (stream.Pending is not null || (!full && stream.Resident != stream.ProxyLevel) || NeededLevel(stream, frame) is not { } needed || needed == stream.Resident)
            {
                continue;
            }

            if (needed > stream.Resident)
            {
                // Drawn smaller than its image: the proxy is swapped in at once, a reduced level after it loads.
                if (needed == stream.ProxyLevel)
                {
                    used -= ShowProxy(stream);
                }
                else
                {
                    Start(guid, stream, needed);
                }
            }
            else if (!pressure && (options.BudgetBytes == 0 || used - stream.Bytes(stream.Resident) + stream.Bytes(needed) <= options.BudgetBytes))
            {
                Start(guid, stream, needed); // sharper (unless that would break the budget or memory is short)
                used += stream.Bytes(needed) - stream.Bytes(stream.Resident);
            }
        }

        if (!full || (!pressure && (options.BudgetBytes == 0 || used <= options.BudgetBytes)))
        {
            return;
        }

        // Release images, least recently drawn first, of textures not drawn for a while; ones in use stay as sharp as they are drawn.
        foreach (var stream in _streamed.Values.Where(s => s.Resident != s.ProxyLevel && LastUse(s) < frame - options.UnusedFrames).OrderBy(LastUse))
        {
            if (!pressure && used <= options.BudgetBytes)
            {
                break;
            }

            used -= ShowProxy(stream);
            _downgrades++;
        }
    }

    /// <summary>
    /// The level a texture drawn in the last frames needs: the proxy when its drawn size fits the proxy, else the smallest
    /// level at least as large as drawn, or the full image when no draw reported its size. Null when it was not drawn.
    /// </summary>
    private static int? NeededLevel(StreamedTexture stream, long frame)
    {
        var texture = stream.Texture;
        if (texture.LastDrawnFrame < frame - (2 * Texture2D.ScreenScaleWindow) || texture.LastDrawnFrame == 0)
        {
            return null;
        }

        if (texture.RequiredScale(frame) is not { } scale || scale >= 1f)
        {
            return 0;
        }

        if (scale * Math.Max(texture.Width, texture.Height) <= Math.Max(stream.Proxy.Width, stream.Proxy.Height))
        {
            return stream.ProxyLevel;
        }

        return Math.Clamp((int)MathF.Floor(MathF.Log2(1f / scale)), 0, stream.Levels);
    }

    /// <summary>Shows the proxy (kept in memory, so at once); returns the bytes released.</summary>
    private long ShowProxy(StreamedTexture stream)
    {
        var released = stream.Bytes(stream.Resident);
        _device.ReplaceTexture(stream.Texture, stream.Proxy.Width, stream.Proxy.Height, stream.Proxy.Pixels);
        stream.Resident = stream.ProxyLevel;
        return released;
    }

    /// <summary>
    /// Unloads cached textures not drawn (or loaded) during the last <paramref name="unusedFrames"/> frames, with the
    /// materials and models that use them; they load again when used. Hosts call it some time after a scene change.
    /// Does nothing on backends that do not track drawing.
    /// </summary>
    /// <returns>Textures unloaded.</returns>
    public int UnloadUnused(long unusedFrames)
    {
        var frame = _device.FrameNumber;
        if (frame == 0)
        {
            return 0;
        }

        var unused = _textures.Where(t => t.Value != _missing && Math.Max(t.Value.LastDrawnFrame, _loadedFrame.GetValueOrDefault(t.Key)) < frame - unusedFrames).Select(t => t.Key).ToList();
        if (unused.Count > 0)
        {
            Evict(unused);
            _unloaded += unused.Count;
        }

        return unused.Count;
    }

    private Texture2D? TryStartStreaming(Guid guid)
    {
        if (!CanStream(guid) || _assets.ReadArtifact(guid, TextureProxy.ArtifactName) is not { } artifact)
        {
            return null;
        }

        var (fullWidth, fullHeight, proxy) = TextureProxy.Read(artifact);
        var settings = _assets.GetTextureSettings(guid);
        var path = _assets.Get(guid)?.Path ?? guid.ToString();
        var texture = _device.CreateStreamingTexture(fullWidth, fullHeight, proxy.Width, proxy.Height, proxy.Pixels, settings.ToSampler(), path);
        texture.TrackScreenScale = true;

        // Builds before reduced levels existed stored only the proxy: such textures switch between it and the full image.
        var levels = _assets.Content.FileExists(AssetDatabase.ArtifactEntry(guid, TextureProxy.LevelArtifactName(1))) ? TextureProxy.ReducedLevels(fullWidth, fullHeight) : 0;
        _streamed[guid] = new StreamedTexture(guid, texture, proxy, levels, settings.ToSampler().GenerateMipmaps);
        return texture; // the first draws report the size it needs
    }

    /// <summary>Starts reading and decoding <paramref name="level"/> (0 = the asset itself, else a reduced level) on a worker.</summary>
    private void Start(Guid guid, StreamedTexture stream, int level)
    {
        var path = _assets.Get(guid)?.Path ?? guid.ToString();
        var svgScale = _assets.GetTextureSettings(guid).SvgScale;
        stream.Started = Stopwatch.GetTimestamp();
        stream.PendingLevel = level;
        stream.Pending = level == 0
            ? Task.Run(() => Image.Decode(_assets.ReadAllBytes(guid), path, svgScale))
            : Task.Run(() => Image.Decode(_assets.ReadArtifact(guid, TextureProxy.LevelArtifactName(level)) ?? throw new FileNotFoundException($"Level {level} of '{path}' is missing."), $"{path} (level {level})"));
    }

    private void Complete(Guid guid, StreamedTexture stream, Task<Image> pending)
    {
        stream.Pending = null;
        if (pending.IsCompletedSuccessfully)
        {
            var image = pending.Result;
            _device.ReplaceTexture(stream.Texture, image.Width, image.Height, image.Pixels);
            stream.Resident = stream.PendingLevel;
            _loadedFrame[guid] = _device.FrameNumber;
            RecordLoad(Stopwatch.GetElapsedTime(stream.Started));
        }
        else if (pending.Exception?.InnerException is { } error && !stream.Failed)
        {
            stream.Failed = true; // keeps showing the proxy; logged once
            LogStreamFailed(_logger, error, _assets.Get(guid)?.Path ?? guid.ToString());
        }
    }

    private void RecordLoad(TimeSpan duration)
    {
        _loadTimes.Enqueue(duration);
        if (_loadTimes.Count > LoadTimeWindow)
        {
            _loadTimes.Dequeue();
        }
    }

    private long LastUse(StreamedTexture stream) => Math.Max(stream.Texture.LastDrawnFrame, _loadedFrame.GetValueOrDefault(stream.Guid));

    /// <summary>The system is short of memory (GC's high-load threshold), or the GPU has less than 128 MB left in its budget (sampled every few seconds).</summary>
    private bool UnderMemoryPressure(long frame)
    {
        var memory = GC.GetGCMemoryInfo();
        if (memory.HighMemoryLoadThresholdBytes > 0 && memory.MemoryLoadBytes >= memory.HighMemoryLoadThresholdBytes)
        {
            return true;
        }

        if (frame % 240 == 0)
        {
            var gpu = _device.QueryGpuMemory();
            _gpuShortUntil = gpu.Supported && gpu.AvailableBytes is < 128L * 1024 * 1024 ? frame + 240 : 0;
        }

        return frame < _gpuShortUntil;
    }

    private void ForgetStream(Guid guid)
    {
        _streamed.Remove(guid); // a pending load finishes unobserved; its texture was disposed by the caller
        _loadedFrame.Remove(guid);
    }

    /// <summary>One streamed texture. Levels: 0 = full image, 1..<see cref="Levels"/> = reduced, <see cref="ProxyLevel"/> = the proxy.</summary>
    private sealed class StreamedTexture(Guid guid, Texture2D texture, Image proxy, int levels, bool mipmaps)
    {
        public Guid Guid { get; } = guid;

        public Texture2D Texture { get; } = texture;

        public Image Proxy { get; } = proxy;

        public int Levels { get; } = levels;

        public int ProxyLevel => Levels + 1;

        public int Resident { get; set; } = levels + 1;

        public int PendingLevel { get; set; }

        public Task<Image>? Pending { get; set; }

        /// <summary>GPU bytes of a level (with mipmaps); the proxy counts as 0.</summary>
        public long Bytes(int level)
        {
            if (level == ProxyLevel)
            {
                return 0;
            }

            var (width, height) = TextureProxy.LevelSize(Texture.Width, Texture.Height, level);
            return (long)width * height * 4 * (mipmaps ? 4 : 3) / 3;
        }

        public long Started { get; set; }

        public bool Failed { get; set; }
    }

    [LoggerMessage(EventId = 3004, Level = LogLevel.Error, Message = "Texture '{Asset}' could not be streamed in; it keeps showing its low-resolution proxy")]
    private static partial void LogStreamFailed(ILogger logger, Exception exception, string asset);
}
