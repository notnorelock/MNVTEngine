using Microsoft.Extensions.Logging;
using MNVTEngine.Assets;
using MNVTEngine.Core.Timing;
using MNVTEngine.Graphics;
using MNVTEngine.Platform;
using MNVTEngine.Runtime.Loading;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;

namespace MNVTEngine.Runtime;

/// <summary>Device factory supplied by the host; keeps the runtime independent of a specific graphics backend.</summary>
public delegate IGraphicsDevice GraphicsDeviceFactory(GameWindow window, ILogger logger);

public sealed record GameRuntimeOptions
{
    /// <summary>Close after this many rendered frames (automated runs and build validation).</summary>
    public int? ExitAfterFrames { get; init; }

    /// <summary>PNG written from the last frame before an automatic exit.</summary>
    public string? ScreenshotPath { get; init; }

    /// <summary>Frame profiler bracketed by the runtime (the host makes it <see cref="Core.Diagnostics.Profiler.Current"/>); null when not profiling.</summary>
    public Core.Diagnostics.FrameProfiler? Profiler { get; init; }


    /// <summary>Content of an exported game: assets, index and compiled scripts come from its packages instead of the project folder.</summary>
    public GameContent? Content { get; init; }

    /// <summary>Metrics, budgets, debug drawing and console commands; null in Release builds without diagnostics.</summary>
    public RuntimeDiagnostics? Diagnostics { get; init; }

    /// <summary>In-game debug UI (development builds); requires <see cref="Diagnostics"/>.</summary>
    public IRuntimeOverlay? Overlay { get; init; }

    /// <summary>TrueType font of the debug UI; null for the built-in one.</summary>
    public string? DebugFontPath { get; init; }

    /// <summary>Skin meshes on the GPU (default); false skins them on the CPU (comparison, debugging).</summary>
    public bool GpuSkinning { get; init; } = true;

    /// <summary>Draw copies of a mesh and material with instanced draws (default); false draws each by itself.</summary>
    public bool GpuInstancing { get; init; } = true;

    /// <summary>Importers for a project played from its sources (the build tooling's full set); exports read imported content.</summary>
    public IReadOnlyList<IAssetImporter>? Importers { get; init; }

    /// <summary>The project's started engine modules, in startup order; those with scene passes draw the scenes.</summary>
    public IReadOnlyList<IEngineModule> Modules { get; init; } = [];

    /// <summary>Log entries shown in the debug console.</summary>
    public Core.Logging.InMemoryLoggerProvider? DebugLogs { get; init; }

    /// <summary>Debug UI window layout file.</summary>
    public string? DebugLayoutFile { get; init; }

    /// <summary>Console commands run once the debug UI is loaded (automation, e.g. "window profiler on").</summary>
    public IReadOnlyList<string> DebugCommands { get; init; } = [];
}

/// <summary>
/// Runs a game project without the editor: loads the startup scene, drives its lifecycle from the window
/// loop, renders it, and services <see cref="SceneManager.LoadScene"/>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Resources are created in IGameLoop.Load and released in IGameLoop.Unload, while the window and graphics device exist.")]
public sealed partial class GameRuntime(
    GameProject project,
    ComponentRegistry registry,
    GraphicsDeviceFactory createDevice,
    ILoggerFactory loggerFactory,
    GameRuntimeOptions? options = null) : IGameLoop, ISceneLoader
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("Runtime");
    private readonly GameRuntimeOptions _options = options ?? new GameRuntimeOptions();
    private GameWindow _window = null!;
    private IGraphicsDevice _device = null!;
    private Renderer2D _renderer = null!;
    private StartupCanvas _startup = null!;
    private IReadOnlyList<IScreenLayer> _screenLayers = [];
    private Graphics.Text.Font _uiFont = null!;
    private DebugTextRenderer? _debugText;
    private Texture2D? _engineLogo;
    private ResourceCache _resources = null!;
    private SceneRenderer _sceneRenderer = null!;
    private AssetDatabase _assets = null!;
    private SceneSerializer _serializer = null!;
    private PrefabProvider _prefabs = null!;
    private string? _pendingScene;
    private int _frames;
    private bool _frameStarted;

    public Scene? CurrentScene { get; private set; }

    /// <summary>0 = clean exit; 1 = GPU resources leaked.</summary>
    public int ExitCode { get; private set; }

    public void Load(GameWindow window)
    {
        _window = window;
        _device = createDevice(window, loggerFactory.CreateLogger("Graphics"));
        _renderer = new Renderer2D(_device);
        _uiFont = Graphics.Text.Font.LoadDefault();
        Application.QuitRequested += OnQuitRequested;
        _startup = new StartupCanvas(_renderer);
        var waiting = new List<string>();
        if (_options.Content is { } extra)
        {
            foreach (var result in extra.MountExtraContent(project.Content, Path.GetDirectoryName(Path.GetFullPath(extra.Directory))!))
            {
                LogExtraContent(_logger, result.Status == ExtraContentStatus.Failed ? LogLevel.Warning : LogLevel.Information, result.Name, result.Status, result.Detail ?? "");
                if (result.Status == ExtraContentStatus.WaitingForKey)
                {
                    waiting.Add(Path.GetFileName(result.Name));
                }
            }
        }

        _assets = _options.Content is { } content ? AssetDatabase.OpenPackaged(content.Files)
            : AssetDatabase.OpenReadOnly(project.AssetsDirectory, _options.Importers, artifactsLibrary: Path.Combine(project.RootDirectory, "Library"));
        foreach (var diagnostic in _assets.Diagnostics)
        {
            LogAssetDiagnostic(_logger, diagnostic.Path, diagnostic.Message);
        }

        _serializer = new SceneSerializer(registry, _assets);
        _prefabs = new PrefabProvider(_serializer, _assets, loggerFactory.CreateLogger("Prefabs"));
        if (_options.Content is { } packaged)
        {
            DownloadableContent.SetHost(new DownloadableContentHost(packaged, _assets, waiting, _logger));
        }

        _resources = new ResourceCache(_device, _assets, loggerFactory.CreateLogger("Resources"));
        if (_options.Content is not null && project.Streaming.Textures)
        {
            _resources.EnableTextureStreaming(new TextureStreamingOptions(project.Streaming.TextureBudgetMB * 1024L * 1024L, UnusedFrames));
        }
        _sceneRenderer = new SceneRenderer(_renderer, _resources, _logger,
            ModuleIntegration.CreatePasses(_options.Modules, _renderer, _resources, loggerFactory, new RenderSettings(_options.GpuSkinning, _options.GpuInstancing)));
        _screenLayers = ModuleIntegration.CreateScreenLayers(_options.Modules, new ScreenServices(window, _renderer, _resources, _assets, _uiFont));
        Application.SetHostWindow(new RuntimeWindow(window, _device, _assets));
        LogStarting(_logger, project.Name, project.StartupScene);

        if (_options.Diagnostics is { } diagnostics)
        {
            diagnostics.Attach(new DiagnosticsTarget(_device, () => CurrentScene, _resources, _assets, _sceneRenderer, _options.Modules));
            ModuleIntegration.AddDiagnostics(_options.Modules, diagnostics.Collector, diagnostics.Commands, () => CurrentScene);
            diagnostics.Install();
            _options.Overlay?.Load(new RuntimeOverlayContext(
                _device, _renderer, diagnostics, window.Input,
                () => new OverlayDisplay(window.WindowWidth, window.WindowHeight, window.WindowWidth > 0 ? (float)window.FramebufferWidth / window.WindowWidth : 1f),
                () => CurrentScene,
                _options.DebugFontPath,
                _options.DebugLogs,
                _options.DebugLayoutFile));
            foreach (var line in _options.DebugCommands)
            {
                var result = diagnostics.Commands.Execute(line);
                LogDebugCommand(_logger, result.Success ? LogLevel.Information : LogLevel.Warning, line, result.Output);
            }
        }
        else
        {
            _options.Profiler?.BeginFrame();
        }

        // Loads over the next frames, behind the splash or loading screen. A broken startup scene is fatal;
        // later scene switches fall back to the current scene.
        StartPresentation();
    }

    public void FixedUpdate(float fixedDeltaTime)
    {
        StartFrame();
        using var sample = Core.Diagnostics.Profiler.Sample("FixedUpdate");
        CurrentScene?.FixedUpdate(fixedDeltaTime);
    }

    /// <summary>streaming.unusedSeconds in frames (at 60 frames per second: frames are what the device counts).</summary>
    private long UnusedFrames => (long)(project.Streaming.UnusedSeconds * 60);

    public void Update(FrameTime time)
    {
        StartFrame();
        using var sample = Core.Diagnostics.Profiler.Sample("Update");
        _resources.Update();
        if (_unloadUnusedAt is { } at && _device.FrameNumber >= at)
        {
            _unloadUnusedAt = null;
            _resources.UnloadUnused(UnusedFrames); // what the previous scene used and this one does not
        }

        _window.Clock.TimeScale = CurrentScene?.TimeScale ?? 1f; // the window's loop scales the next frame and its fixed steps
        if (UpdateLoading(time))
        {
            if (_pendingScene is { } queued && !_loadIsStartup)
            {
                _pendingScene = null;
                BeginLoad(queued, startup: false); // a newer request replaces the load in progress
            }

            return; // gameplay is frozen while a scene loads or the splash shows
        }

        if (CurrentScene is { } scene)
        {
            // Before gameplay: input a screen layer takes (focused text field, pointer over UI) reads as released for scripts.
            foreach (var layer in _screenLayers)
            {
                layer.Update(scene, time.UnscaledDeltaTime);
            }
        }

        CurrentScene?.Update(time);
        if (_pendingScene is { } path)
        {
            _pendingScene = null;
            BeginLoad(path, startup: false);
        }
    }

    public void Render(FrameTime time, float interpolation)
    {
        StartFrame();
        var gpu = _options.Diagnostics?.GpuTimer;
        if (CurrentScene is not null && !PresentationCoversScene)
        {
            using var sample = Core.Diagnostics.Profiler.Sample("Render");
            using var gpuScope = new GpuScope(gpu, "Scene");
            _sceneRenderer.Render(CurrentScene, _window.FramebufferWidth, _window.FramebufferHeight);
            foreach (var layer in _screenLayers)
            {
                layer.Render(CurrentScene, _window.FramebufferWidth, _window.FramebufferHeight);
            }
        }

        RenderPresentation();

        // Frames run FixedUpdate* → Update → Render: a frame ends after rendering.
        if (_options.Diagnostics is { } diagnostics)
        {
            var view = CurrentScene?.MainCamera is { } main ? SceneRenderer.CreateView(main, _window.FramebufferWidth, _window.FramebufferHeight) : null;
            _device.SetRenderTarget(null);
            (_debugText ??= new DebugTextRenderer(_renderer)).Render(diagnostics.DrawBuffer.Commands, view, _window.FramebufferWidth, _window.FramebufferHeight);
            diagnostics.SceneRendered();
            if (_options.Overlay is { } overlay)
            {
                using var sample = Core.Diagnostics.Profiler.Sample("Debug Overlay");
                using var gpuScope = new GpuScope(gpu, "Debug Overlay");
                overlay.Render(CurrentScene?.MainCamera is { } camera ? SceneRenderer.CreateView(camera, _window.FramebufferWidth, _window.FramebufferHeight) : null, null);
            }

            diagnostics.EndFrame(time);
            _frameStarted = false;
        }
        else
        {
            _options.Profiler?.EndFrame();
            _options.Profiler?.BeginFrame();
        }

        if (!IsLoading && _splash is null && _loadingView is null)
        {
            _frames++; // --frames counts frames of the running game, not of loading or splash screens (fade-out included)
        }

        var exiting = _options.ExitAfterFrames is { } limit && _frames >= limit;
        if (exiting && _options.ScreenshotPath is { } path)
        {
            SaveScreenshot(path); // before presenting: the back buffer is gone afterwards
        }

        _device.Present();
        if (exiting)
        {
            var statistics = _sceneRenderer.Statistics;
            int Stat(string name) => statistics.GetValueOrDefault(name);
            var (sprites, culled, meshes, meshesCulled) = (Stat(RenderStatistics.SpritesDrawn), Stat(RenderStatistics.SpritesCulled), Stat(RenderStatistics.MeshesDrawn), Stat(RenderStatistics.MeshesCulled));
            var (skinned, instanced, draws) = (Stat(RenderStatistics.GpuSkinnedMeshes), Stat(RenderStatistics.InstancedMeshes), Stat(RenderStatistics.MeshDrawCalls));
            LogAutoExit(_logger, _frames, sprites, culled, meshes, meshesCulled, skinned, instanced, draws);
            _window.Close();
        }
    }

    public void Resize(int width, int height) => _device.ResizeBackBuffer(width, height);

    public void Unload()
    {
        if (_device is null)
        {
            return; // the graphics device could not be created; Load created nothing else
        }

        StopPresentation();
        Application.QuitRequested -= OnQuitRequested;
        Application.SetHostWindow(null);
        DownloadableContent.SetHost(null);

        _options.Overlay?.Dispose();
        _options.Diagnostics?.Uninstall();
        CurrentScene?.Unload();
        _sceneRenderer?.Dispose();
        foreach (var layer in _screenLayers)
        {
            layer.Dispose();
        }

        _startup?.Dispose();
        _uiFont?.Dispose();
        _debugText?.Dispose();
        _engineLogo?.Dispose();
        _resources?.Dispose();
        _renderer?.Dispose();
        if (_device.Resources.LiveCount > 0)
        {
            ExitCode = 1;
            LogLeaks(_logger, _device.Resources.LiveCount);
        }

        _device.Dispose();
    }

    private void OnQuitRequested() => _window.Close();

    /// <summary>Queues a scene switch for the end of the current frame.</summary>
    /// <exception cref="ArgumentException">The path escapes the Assets folder.</exception>
    public void RequestLoad(string path)
    {
        project.ResolveAsset(path);
        _pendingScene = path;
    }

    /// <exception cref="ArgumentException">The reference is not a scene asset of this project.</exception>
    public void RequestLoad(AssetRef scene) =>
        RequestLoad(_assets.GetAssetType(scene.Guid) == AssetTypes.Scene && _assets.TryGetPath(scene.Guid, out var path)
            ? path
            : throw new ArgumentException($"{scene} is not a scene asset in this project.", nameof(scene)));

    /// <summary>The first callback of a frame starts diagnostics and lets the overlay take its input before gameplay reads it.</summary>
    private void StartFrame()
    {
        if (_frameStarted || _options.Diagnostics is null)
        {
            return;
        }

        _frameStarted = true;
        _options.Diagnostics.BeginFrame();
        _options.Overlay?.BeginFrame();
    }

    private void SwitchTo(Scene next)
    {
        next.Logger = loggerFactory.CreateLogger($"Scene.{next.Name}");
        next.Input = _window.Input;
        next.Loader = this;
        next.Prefabs = _prefabs;
        next.TextAssets = _prefabs;
        next.AssetReader = new AssetDatabaseReader(_assets);
        ModuleIntegration.SceneLoaded(_options.Modules, next);
        foreach (var layer in _screenLayers)
        {
            layer.SceneLoaded(next);
        }

        // DontDestroyOnLoad objects move in once the new scene's services exist, before the old scene is torn down.
        CurrentScene?.MovePersistentObjectsTo(next);
        CurrentScene?.Unload();
        CurrentScene = next;
        next.Start();
        LogSceneStarted(_logger, next.Name, next.GameObjects.Count);
    }

    private void SaveScreenshot(string path)
    {
        _device.SetRenderTarget(null);
        var pixels = new byte[_device.BackBufferWidth * _device.BackBufferHeight * 4];
        _device.ReadPixels(pixels);
        using var stream = File.Create(path);
        new StbImageWriteSharp.ImageWriter().WritePng(pixels, _device.BackBufferWidth, _device.BackBufferHeight, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, stream);
    }

    [LoggerMessage(EventId = 3107, Message = "> {Command}: {Output}")]
    private static partial void LogDebugCommand(ILogger logger, LogLevel level, string command, string output);

    [LoggerMessage(EventId = 3106, Level = LogLevel.Warning, Message = "Asset {Path}: {Message}")]
    private static partial void LogAssetDiagnostic(ILogger logger, string path, string message);

    [LoggerMessage(EventId = 3100, Level = LogLevel.Information, Message = "Starting '{Project}' with scene '{Scene}'")]
    private static partial void LogStarting(ILogger logger, string project, string scene);

    [LoggerMessage(EventId = 3101, Level = LogLevel.Information, Message = "Scene '{Scene}' running with {Count} objects")]
    private static partial void LogSceneStarted(ILogger logger, string scene, int count);

    [LoggerMessage(EventId = 3102, Message = "{File}: {Path}: {Message}")]
    private static partial void LogSceneDiagnostic(ILogger logger, LogLevel level, string file, string path, string message);

    [LoggerMessage(EventId = 3103, Level = LogLevel.Error, Message = "Could not load scene '{Scene}'; staying in the current scene")]
    private static partial void LogSceneSwitchFailed(ILogger logger, Exception exception, string scene);

    [LoggerMessage(EventId = 3104, Level = LogLevel.Information, Message = "Exiting after {Frames} frames ({Drawn} sprites drawn, {Culled} culled; {Meshes} meshes drawn, {MeshesCulled} culled, {GpuSkinned} GPU-skinned, {Instanced} instanced in {DrawCalls} draws)")]
    private static partial void LogAutoExit(ILogger logger, int frames, int drawn, int culled, int meshes, int meshesCulled, int gpuSkinned, int instanced, int drawCalls);

    [LoggerMessage(EventId = 3109, Message = "Extra content {Name}: {Status} {Detail}")]
    private static partial void LogExtraContent(ILogger logger, LogLevel level, string name, ExtraContentStatus status, string detail);

    [LoggerMessage(EventId = 3105, Level = LogLevel.Error, Message = "{Count} GPU resources leaked")]
    private static partial void LogLeaks(ILogger logger, int count);
}

/// <summary>Mounts DLC with server-provided keys for <see cref="DownloadableContent"/>, and merges its asset index.</summary>
internal sealed partial class DownloadableContentHost(GameContent content, AssetDatabase assets, List<string> waiting, ILogger logger) : IDownloadableContentHost
{
    public IReadOnlyList<string> WaitingForKey => [.. waiting];

    public bool Mount(string fileName, ReadOnlySpan<byte> key, out string? problem)
    {
        IO.Packages.PackageEncryptionKey packageKey;
        try
        {
            packageKey = IO.Packages.PackageEncryptionKey.FromBytes(key);
        }
        catch (ArgumentException ex)
        {
            problem = ex.Message;
            return false;
        }

        var result = content.MountDlc(fileName, packageKey);
        if (result.Status != ExtraContentStatus.Mounted)
        {
            problem = result.Detail;
            return false;
        }

        try
        {
            assets.MergeExtraIndexes();
        }
        catch (InvalidDataException ex)
        {
            problem = ex.Message;
            return false;
        }

        waiting.RemoveAll(w => string.Equals(w, fileName, StringComparison.OrdinalIgnoreCase));
        LogMounted(logger, fileName);
        problem = null;
        return true;
    }

    [LoggerMessage(EventId = 3110, Level = LogLevel.Information, Message = "DLC {File} mounted with a provided key")]
    private static partial void LogMounted(ILogger logger, string file);
}

/// <summary>Assets for scene services (audio clips), through the asset database and its virtual file system.</summary>
public sealed class AssetDatabaseReader(AssetDatabase assets) : IAssetReader
{
    public byte[] ReadAllBytes(AssetRef asset) => assets.ReadAllBytes(asset.Guid);

    public Stream OpenRead(AssetRef asset) => assets.OpenRead(asset.Guid);

    public string? GetImportSetting(AssetRef asset, string name) =>
        assets.GetSettings(asset.Guid)[name] is { Type: not Newtonsoft.Json.Linq.JTokenType.Null } value ? value.ToString() : null;
}
