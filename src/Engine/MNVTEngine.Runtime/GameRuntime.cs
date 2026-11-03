using Microsoft.Extensions.Logging;
using MNVTEngine.Assets;
using MNVTEngine.Core.Timing;
using MNVTEngine.Graphics;
using MNVTEngine.Platform;
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
}

/// <summary>
/// Runs a game project without the editor: loads the startup scene, drives its lifecycle from the window
/// loop, renders it, and services <see cref="SceneManager.LoadScene"/>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Resources are created in IGameLoop.Load and released in IGameLoop.Unload, while the GL context is current.")]
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
    private ResourceCache _resources = null!;
    private SceneRenderer _sceneRenderer = null!;
    private AssetDatabase _assets = null!;
    private SceneSerializer _serializer = null!;
    private PrefabProvider _prefabs = null!;
    private string? _pendingScene;
    private int _frames;

    public Scene? CurrentScene { get; private set; }

    /// <summary>0 = clean exit; 1 = GPU resources leaked.</summary>
    public int ExitCode { get; private set; }

    public void Load(GameWindow window)
    {
        _window = window;
        _device = createDevice(window, loggerFactory.CreateLogger("Graphics"));
        _renderer = new Renderer2D(_device);
        _assets = AssetDatabase.OpenReadOnly(project.AssetsDirectory);
        foreach (var diagnostic in _assets.Diagnostics)
        {
            LogAssetDiagnostic(_logger, diagnostic.Path, diagnostic.Message);
        }

        _serializer = new SceneSerializer(registry, _assets);
        _prefabs = new PrefabProvider(_serializer, _assets, loggerFactory.CreateLogger("Prefabs"));
        _resources = new ResourceCache(_device, _assets, loggerFactory.CreateLogger("Resources"));
        _sceneRenderer = new SceneRenderer(_renderer, _resources, _logger);
        LogStarting(_logger, project.Name, project.StartupScene);

        // A broken startup scene is fatal; later scene switches fall back to the current scene.
        SwitchTo(LoadScene(project.StartupScene));
    }

    public void FixedUpdate(float fixedDeltaTime) => CurrentScene?.FixedUpdate(fixedDeltaTime);

    public void Update(FrameTime time)
    {
        CurrentScene?.Update(time);
        if (_pendingScene is { } path)
        {
            _pendingScene = null;
            try
            {
                SwitchTo(LoadScene(path));
            }
            catch (SerializationException ex)
            {
                LogSceneSwitchFailed(_logger, ex, path);
            }
        }
    }

    public void Render(FrameTime time, float interpolation)
    {
        if (CurrentScene is not null)
        {
            _sceneRenderer.Render(CurrentScene, _window.FramebufferWidth, _window.FramebufferHeight);
        }

        _frames++;
        if (_options.ExitAfterFrames is { } limit && _frames >= limit)
        {
            if (_options.ScreenshotPath is { } path)
            {
                SaveScreenshot(path);
            }

            LogAutoExit(_logger, _frames, _sceneRenderer.SpritesDrawn, _sceneRenderer.SpritesCulled);
            _window.Close();
        }
    }

    public void Resize(int width, int height) => _device.ResizeBackBuffer(width, height);

    public void Unload()
    {
        CurrentScene?.Unload();
        _resources.Dispose();
        _renderer.Dispose();
        if (_device.Resources.LiveCount > 0)
        {
            ExitCode = 1;
            LogLeaks(_logger, _device.Resources.LiveCount);
        }

        _device.Dispose();
    }

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

    private Scene LoadScene(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(project.ResolveAsset(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new SerializationException(path, ex.Message, [], ex);
        }

        var result = _serializer.Deserialize(json, path);
        foreach (var diagnostic in result.Diagnostics)
        {
            var level = diagnostic.Severity switch
            {
                DiagnosticSeverity.Error => LogLevel.Error,
                DiagnosticSeverity.Warning => LogLevel.Warning,
                _ => LogLevel.Information,
            };
            LogSceneDiagnostic(_logger, level, path, diagnostic.Path, diagnostic.Message);
        }

        return result.Scene;
    }

    private void SwitchTo(Scene next)
    {
        CurrentScene?.Unload();
        next.Logger = loggerFactory.CreateLogger($"Scene.{next.Name}");
        next.Input = _window.Input;
        next.Loader = this;
        next.Prefabs = _prefabs;
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

    [LoggerMessage(EventId = 3104, Level = LogLevel.Information, Message = "Exiting after {Frames} frames ({Drawn} sprites drawn, {Culled} culled)")]
    private static partial void LogAutoExit(ILogger logger, int frames, int drawn, int culled);

    [LoggerMessage(EventId = 3105, Level = LogLevel.Error, Message = "{Count} GPU resources leaked")]
    private static partial void LogLeaks(ILogger logger, int count);
}
