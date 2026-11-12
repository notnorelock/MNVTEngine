using System.Diagnostics;
using MNVTEngine.Core.Timing;
using MNVTEngine.ECS;
using MNVTEngine.Graphics;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;

namespace MNVTEngine.Browser;

/// <summary>
/// The "browser" engine module: one <see cref="BrowserService"/> for the game (the browser host starts with the first
/// browser), the system that delivers its events each frame, and the pass that copies pages into textures.
/// </summary>
public sealed class BrowserModule : IEngineModule, ISceneIntegration, IRenderIntegration, IDiagnosticsIntegration
{
    private BrowserService? _service;
    private Assets.AssetDatabase? _assets;

    public string Id => "browser";

    /// <summary>The game's browsers; null until the first scene loads.</summary>
    public BrowserService? Service => _service;

    public void Register(ComponentRegistry components)
    {
    }

    public void SceneLoaded(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        _service ??= new BrowserService(scene.Logger) { ResourceReader = ReadWebFile };
        scene.SetService(_service);
        if (scene.Systems.Get<BrowserSystem>() is null)
        {
            scene.Systems.Add(new BrowserSystem(_service));
        }
    }

    public IScenePass CreatePass(RenderServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _assets ??= services.Resources.Assets;
        return new BrowserTexturePass(services.Resources, () => _service);
    }

    public IEnumerable<Diagnostics.IMetricsProvider> CreateMetricsProviders(Func<Scene?> scene) => [new BrowserMetrics(() => _service)];

    public void RegisterCommands(Diagnostics.DebugCommandRegistry commands, Func<Scene?> scene)
    {
        ArgumentNullException.ThrowIfNull(commands);
        commands.Register("browsers", "The browser host and every embedded browser: state, page, texture, paints and uploads.", _ => Describe());
        commands.Register("browser.reload", "Reloads an embedded browser (recovers it after a crash).", "<id>", args =>
            args is [var text] && int.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var id) && _service?.Browsers.FirstOrDefault(b => b.Id == id) is { } browser
                ? Reload(browser)
                : new Diagnostics.DebugCommandResult(false, "Usage: browser.reload <id> (see `browsers`)."));

        static Diagnostics.DebugCommandResult Reload(BrowserInstance browser)
        {
            browser.Reload(ignoreCache: true);
            return new Diagnostics.DebugCommandResult(true, $"Reloading browser {browser.Id}.");
        }
    }

    private Diagnostics.DebugCommandResult Describe()
    {
        if (_service is not { } service)
        {
            return new Diagnostics.DebugCommandResult(true, "No browsers: no scene has loaded yet.");
        }

        var text = new System.Text.StringBuilder(System.FormattableString.Invariant($"Browser host: {service.HostState} (Chromium {service.ChromiumVersion ?? "-"}, process {service.HostProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}){(service.HostError is { } error ? ": " + error : "")}"));
        foreach (var browser in service.Browsers)
        {
            text.Append(System.FormattableString.Invariant($"\n  {browser.Id}: {browser.State} {browser.Url} '{browser.Title}' {browser.TextureWidth}x{browser.TextureHeight}, {browser.FramesPainted} paints, {browser.FramesUploaded} uploads, {browser.Options.Profile}"));
        }

        return new Diagnostics.DebugCommandResult(true, text.ToString());
    }

    public void Shutdown()
    {
        _service?.Dispose();
        _service = null;
    }

    /// <summary>mnvt://game/ reads: only paths under Web/ reach here (<see cref="BrowserHosting.BrowserSecurityPolicy.TryGetGameAssetPath"/>).</summary>
    private byte[]? ReadWebFile(string path)
    {
        if (!path.StartsWith(BrowserHosting.BrowserSecurityPolicy.GameWebRoot, StringComparison.Ordinal))
        {
            return null;
        }

        var assets = _assets ?? throw new InvalidOperationException("The browser module has no asset database yet (no renderer was created).");
        return assets.TryGetGuid(path, out var guid) ? assets.ReadAllBytes(guid) : null;
    }

    private sealed class BrowserSystem(BrowserService service) : GameSystem
    {
        public override void Update(World world, FrameTime time) => service.Update();
    }

    private sealed class BrowserMetrics(Func<BrowserService?> service) : Diagnostics.IMetricsProvider
    {
        private long _lastPainted;

        public string Name => "Browser";

        public void Collect(Diagnostics.MetricsSnapshot metrics)
        {
            if (service() is not { } browsers)
            {
                return;
            }

            long painted = 0, textureBytes = 0;
            foreach (var browser in browsers.Browsers)
            {
                painted += browser.FramesPainted;
                textureBytes += (long)browser.TextureWidth * browser.TextureHeight * 4;
            }

            metrics.Set(Diagnostics.Metrics.BrowserInstances, browsers.Browsers.Count);
            metrics.Set(Diagnostics.Metrics.BrowserFramesPainted, Math.Max(0, painted - _lastPainted));
            _lastPainted = painted;
            metrics.Set(Diagnostics.Metrics.BrowserFramesUploaded, browsers.LastUploads.Uploads);
            metrics.Set(Diagnostics.Metrics.BrowserUploadBytes, browsers.LastUploads.Bytes);
            metrics.Set(Diagnostics.Metrics.BrowserUploadTime, browsers.LastUploads.Milliseconds);
            metrics.Set(Diagnostics.Metrics.BrowserTextureMemory, textureBytes);
            if (browsers.HostProcessId is { } id && HostWorkingSet(id) is { } bytes)
            {
                metrics.Set(Diagnostics.Metrics.BrowserHostMemory, bytes);
            }

            metrics.SetUnsupported(Diagnostics.Metrics.BrowserGpuZeroCopy);
        }

        private static long? HostWorkingSet(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return process.WorkingSet64;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return null; // the host exited: the metric reads as unavailable, not 0
            }
        }
    }
}

/// <summary>Copies each browser's changed pixels into its texture, once a frame, before anything draws.</summary>
internal sealed class BrowserTexturePass(ResourceCache resources, Func<BrowserService?> service) : IScenePass
{
    public int Order => 0;

    public void Draw(ScenePassContext context)
    {
        if (service() is not { } browsers)
        {
            return;
        }

        if (!resources.TryGetExtension<BrowserTextures>(out var textures))
        {
            textures = new BrowserTextures(resources);
            resources.AddExtension(textures);
        }

        textures.Update(browsers);
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// The browser textures of one resource cache (shared by every pass drawing with it).
/// shortcut: pixels are taken once, so with two resource caches only the first updating sees changes; give each cache its own frame copy if a host ever needs two.
/// </summary>
internal sealed class BrowserTextures(ResourceCache resources) : IResourceExtension
{
    private readonly Dictionary<BrowserInstance, (Texture2D Texture, int Generation)> _textures = [];
    private byte[] _buffer = new byte[1024 * 1024];

    public void Update(BrowserService browsers)
    {
        var started = Stopwatch.GetTimestamp();
        var (uploads, bytes) = (0, 0L);
        foreach (var gone in _textures.Keys.Where(b => b.State == BrowserState.Closed).ToList())
        {
            Remove(gone);
        }

        foreach (var browser in browsers.Browsers)
        {
            if (!browser.TryTakeFrame(ref _buffer, out var dirty))
            {
                continue;
            }

            if (!_textures.TryGetValue(browser, out var entry) || entry.Generation != browser.SurfaceGeneration)
            {
                Remove(browser);
                var blank = new byte[browser.TextureWidth * browser.TextureHeight * 4];
                entry = (resources.Device.CreateTexture(browser.TextureWidth, browser.TextureHeight, blank, SamplerState.LinearClamp, $"Browser {browser.Id}"), browser.SurfaceGeneration);
                _textures[browser] = entry;
                resources.SetRuntimeTexture(browser.TextureId, entry.Texture);
            }

            if (dirty.X + dirty.Width <= entry.Texture.Width && dirty.Y + dirty.Height <= entry.Texture.Height)
            {
                var y = dirty.Y;
                if (browser.FlipVertically)
                {
                    FlipRows(_buffer, dirty.Width * 4, dirty.Height);
                    y = entry.Texture.Height - dirty.Y - dirty.Height;
                }

                resources.Device.UpdateTexture(entry.Texture, dirty.X, y, dirty.Width, dirty.Height, _buffer);
                browser.FramesUploaded++;
                uploads++;
                bytes += (long)dirty.Width * dirty.Height * 4;
            }
        }

        if (uploads > 0 || browsers.LastUploads.Uploads > 0)
        {
            browsers.LastUploads = (uploads, bytes, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    public void Evict(IReadOnlyCollection<Guid> changed)
    {
    }

    public void Dispose()
    {
        foreach (var browser in _textures.Keys.ToList())
        {
            Remove(browser);
        }
    }

    private static void FlipRows(byte[] pixels, int rowBytes, int rows)
    {
        var span = pixels.AsSpan(0, rowBytes * rows);
        Span<byte> swap = rowBytes <= 16 * 1024 ? stackalloc byte[rowBytes] : new byte[rowBytes];
        for (int top = 0, bottom = rows - 1; top < bottom; top++, bottom--)
        {
            var a = span.Slice(top * rowBytes, rowBytes);
            var b = span.Slice(bottom * rowBytes, rowBytes);
            a.CopyTo(swap);
            b.CopyTo(a);
            swap.CopyTo(b);
        }
    }

    private void Remove(BrowserInstance browser)
    {
        if (_textures.Remove(browser, out var entry))
        {
            resources.SetRuntimeTexture(browser.TextureId, null);
            entry.Texture.Dispose();
        }
    }
}
