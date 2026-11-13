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
/// <param name="FullBytes">GPU memory of those full images (with mipmaps).</param>
/// <param name="Pending">Full images being read and decoded in the background.</param>
/// <param name="Downgrades">Full images released (budget, memory pressure) since start.</param>
/// <param name="Unloaded">Textures unloaded as unused since start.</param>
/// <param name="AverageLoad">Mean duration of the last 32 texture loads (read, decode, upload); null before any.</param>
/// <param name="MaxLoad">Longest of those.</param>
/// <param name="LastSceneLoad">Duration of the last scene load, from request to start; null before any.</param>
public readonly record struct AssetLoadStatistics(
    int Proxies, int FullResolution, long FullBytes, int Pending, long Downgrades, long Unloaded, TimeSpan? AverageLoad, TimeSpan? MaxLoad, TimeSpan? LastSceneLoad);

/// <summary>
/// Texture streaming: in exported games a large texture first shows its build-time proxy (<see cref="TextureProxy"/>),
/// while its full image is read and decoded on a worker; <see cref="Update"/> swaps it in on the graphics thread. The
/// <see cref="Texture2D"/> object stays the same throughout (<see cref="IGraphicsDevice.ReplaceTexture"/>), so materials
/// and models holding it simply draw sharper. Over the budget, or when the system or GPU runs short of memory, full
/// images not drawn for a while go back to their proxy; drawing one again streams it in again.
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
            var full = _streamed.Values.Where(s => s.Full).ToList();
            return new AssetLoadStatistics(
                _streamed.Count - full.Count, full.Count, full.Sum(s => s.FullBytes), _streamed.Values.Count(s => s.Pending is not null), _downgrades, _unloaded,
                _loadTimes.Count == 0 ? null : TimeSpan.FromTicks((long)_loadTimes.Average(t => t.Ticks)), _loadTimes.Count == 0 ? null : _loadTimes.Max(), _lastSceneLoad);
        }
    }

    /// <summary>Records how long a scene load took (from request to start), for diagnostics.</summary>
    public void RecordSceneLoad(TimeSpan duration) => _lastSceneLoad = duration;

    /// <summary>
    /// Once per frame on the graphics thread: swaps in full images that finished loading, streams in textures drawn
    /// again, and releases full images over the budget or under memory pressure.
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

        if (frame % 15 != 0)
        {
            return;
        }

        var pressure = UnderMemoryPressure(frame);
        var used = _streamed.Values.Where(s => s.Full).Sum(s => s.FullBytes);
        foreach (var (guid, stream) in _streamed)
        {
            // Drawn in the last frames while showing its proxy: stream it in (unless that would break the budget or memory is short).
            if (!stream.Full && stream.Pending is null && stream.Texture.LastDrawnFrame >= frame - 2 && !pressure
                && (options.BudgetBytes == 0 || used + stream.EstimatedFullBytes <= options.BudgetBytes))
            {
                StartFull(guid, stream);
                used += stream.EstimatedFullBytes;
            }
        }

        if (!pressure && (options.BudgetBytes == 0 || used <= options.BudgetBytes))
        {
            return;
        }

        // Release full images, least recently drawn first, of textures not drawn for a while; ones in use stay sharp.
        foreach (var stream in _streamed.Values.Where(s => s.Full && LastUse(s) < frame - options.UnusedFrames).OrderBy(LastUse))
        {
            if (!pressure && used <= options.BudgetBytes)
            {
                break;
            }

            _device.ReplaceTexture(stream.Texture, stream.Proxy.Width, stream.Proxy.Height, stream.Proxy.Pixels);
            used -= stream.FullBytes;
            (stream.Full, stream.FullBytes) = (false, 0);
            _downgrades++;
        }
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
        var stream = new StreamedTexture(guid, texture, proxy, (long)fullWidth * fullHeight * 4 * (settings.ToSampler().GenerateMipmaps ? 4 : 3) / 3);
        _streamed[guid] = stream;
        StartFull(guid, stream);
        return texture;
    }

    private void StartFull(Guid guid, StreamedTexture stream)
    {
        var path = _assets.Get(guid)?.Path ?? guid.ToString();
        var svgScale = _assets.GetTextureSettings(guid).SvgScale;
        stream.Started = Stopwatch.GetTimestamp();
        stream.Pending = Task.Run(() => Image.Decode(_assets.ReadAllBytes(guid), path, svgScale));
    }

    private void Complete(Guid guid, StreamedTexture stream, Task<Image> pending)
    {
        stream.Pending = null;
        if (pending.IsCompletedSuccessfully)
        {
            var image = pending.Result;
            _device.ReplaceTexture(stream.Texture, image.Width, image.Height, image.Pixels);
            (stream.Full, stream.FullBytes) = (true, stream.EstimatedFullBytes);
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

    private sealed class StreamedTexture(Guid guid, Texture2D texture, Image proxy, long estimatedFullBytes)
    {
        public Guid Guid { get; } = guid;

        public Texture2D Texture { get; } = texture;

        public Image Proxy { get; } = proxy;

        public long EstimatedFullBytes { get; } = estimatedFullBytes;

        public bool Full { get; set; }

        public long FullBytes { get; set; }

        public Task<Image>? Pending { get; set; }

        public long Started { get; set; }

        public bool Failed { get; set; }
    }

    [LoggerMessage(EventId = 3004, Level = LogLevel.Error, Message = "Texture '{Asset}' could not be streamed in; it keeps showing its low-resolution proxy")]
    private static partial void LogStreamFailed(ILogger logger, Exception exception, string asset);
}
