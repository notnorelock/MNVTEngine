using Microsoft.Extensions.Logging;
using MNVTEngine.Core.Timing;
using MNVTEngine.Mathematics;
using MNVTEngine.Runtime.Loading;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;

namespace MNVTEngine.Runtime;

/// <summary>
/// Scene loading over several frames, with the splash and loading screens. While a scene loads, the current scene is
/// frozen; the window keeps processing events and presenting the presentation screens. A failed startup load ends
/// the game (exit code 2); a failed later load keeps the current scene.
/// </summary>
public sealed partial class GameRuntime : ILoadingScreenHost
{
    private static readonly TimeSpan LoadBudget = TimeSpan.FromMilliseconds(6);
    private readonly List<ManualLoadingScreen> _manualScreens = [];
    private SceneLoadOperation? _load;
    private bool _loadIsStartup;
    private SplashScreenView? _splash;
    private LoadingScreenView? _loadingView;
    private StartupAssets _startupAssets = null!;

    /// <summary>True while a scene is loading (the current scene is frozen).</summary>
    public bool IsLoading => _load is not null;

    /// <summary>Progress of the current scene load; null when idle or while the amount of work is unknown.</summary>
    public float? LoadProgress => _load is { IsIndeterminate: false } op ? op.Progress : null;

    public ILoadingScreenHandle Show(LoadingScreenOptions options)
    {
        var screen = new ManualLoadingScreen(new LoadingScreenView(options.Clone(), _startupAssets));
        _manualScreens.Add(screen);
        return screen;
    }

    private void StartPresentation()
    {
        _startupAssets = new StartupAssets(_uiFont, TryTexture, TryFont);
        LoadingScreen.Options = project.LoadingScreen.Enabled ? OptionsFrom(project.LoadingScreen) : null;
        LoadingScreen.SetHost(this);
        if (project.SplashScreen.Enabled)
        {
            _splash = new SplashScreenView(project.SplashScreen, _startupAssets, Ref(project.SplashScreen.Logo), Ref(project.SplashScreen.BackgroundImage));
        }

        BeginLoad(project.StartupScene, startup: true);
    }

    private void StopPresentation()
    {
        _load?.Dispose();
        _load = null;
        _splash?.Dispose();
        _loadingView?.Dispose();
        foreach (var screen in _manualScreens)
        {
            screen.View.Dispose();
        }

        _manualScreens.Clear();
        LoadingScreen.SetHost(null);
    }

    private void BeginLoad(string path, bool startup)
    {
        _load?.Cancel();
        _load?.Dispose();
        _load = new SceneLoadOperation(path, _assets, _resources, DeserializeScene);
        _loadIsStartup = startup;
        if (_loadingView is null && _splash is null && LoadingScreen.Options is { } options)
        {
            _loadingView = new LoadingScreenView(options.Clone(), _startupAssets);
        }

        if (_loadingView is not null)
        {
            _loadingView.Closing = false;
        }
    }

    /// <summary>Advances loading and the presentation screens; returns true while gameplay must stay frozen.</summary>
    private bool UpdateLoading(FrameTime time)
    {
        if (_load is { } op)
        {
            op.Step(LoadBudget);
            if (op.IsDone)
            {
                foreach (var error in op.TextureErrors)
                {
                    LogTextureDecodeFailed(_logger, error, op.Path);
                }

                _load = null;
                op.Dispose();
                if (op.Result is { } scene)
                {
                    SwitchTo(scene);
                }
                else if (op.Error is { } error && _loadIsStartup)
                {
                    throw error as SerializationException ?? new SerializationException(op.Path, error.Message, [], error);
                }
                else if (op.Error is { } failed)
                {
                    LogSceneSwitchFailed(_logger, failed, op.Path);
                }

                if (_loadingView is not null)
                {
                    _loadingView.Closing = true;
                }
            }
        }

        var size = new Vector2(_window.WindowWidth, _window.WindowHeight);
        var scale = _window.WindowWidth > 0 ? (float)_window.FramebufferWidth / _window.WindowWidth : 1f;
        var dt = time.UnscaledDeltaTime;
        var progress = _load is { IsIndeterminate: false } current ? current.Progress : (float?)null;
        if (_splash is { } splash)
        {
            var input = _window.Input;
            var skip = input.WasMouseButtonPressed(MouseButton.Left) || input.WasKeyPressed(Key.Space) || input.WasKeyPressed(Key.Enter) || input.WasKeyPressed(Key.Escape);
            splash.Update(_load is null, _load is null ? 1f : progress, skip, size, scale, dt);
            if (!splash.IsVisible)
            {
                splash.Dispose();
                _splash = null;
                if (_load is not null && _loadingView is null && LoadingScreen.Options is { } options)
                {
                    _loadingView = new LoadingScreenView(options.Clone(), _startupAssets); // loading outlasted the splash
                }
            }
        }

        if (_loadingView is { } view)
        {
            view.Update(_load is null ? 1f : progress, _load?.CurrentTask, size, scale, dt);
            if (!view.IsVisible)
            {
                view.Dispose();
                _loadingView = null;
            }
        }

        for (var i = _manualScreens.Count - 1; i >= 0; i--)
        {
            var manual = _manualScreens[i];
            manual.View.Update(manual.Progress, manual.Task, size, scale, dt);
            if (!manual.View.IsVisible)
            {
                manual.View.Dispose();
                _manualScreens.RemoveAt(i);
            }
        }

        return _load is not null || _splash is not null;
    }

    /// <summary>Splash, loading and manual screens, over the scene and its UI.</summary>
    private void RenderPresentation()
    {
        if (_splash is null && _loadingView is null && _manualScreens.Count == 0)
        {
            return;
        }

        _device.SetRenderTarget(null);
        _loadingView?.Render(_ui, _window.FramebufferWidth, _window.FramebufferHeight);
        foreach (var manual in _manualScreens)
        {
            manual.View.Render(_ui, _window.FramebufferWidth, _window.FramebufferHeight);
        }

        _splash?.Render(_ui, _window.FramebufferWidth, _window.FramebufferHeight);
    }

    /// <summary>
    /// True when the scene must not be drawn: while a scene loads (the game view stays hidden until its assets are
    /// loaded, whether or not a loading screen is enabled) or while the splash covers the screen.
    /// </summary>
    private bool PresentationCoversScene => _load is not null || _splash is { IsOpaque: true };

    private Scene DeserializeScene(string json, string path)
    {
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

    private AssetRef Ref(string? path) => path is { Length: > 0 } && _assets.TryGetGuid(path, out var guid) ? new AssetRef(guid) : AssetRef.None;

    private Graphics.Texture2D? TryTexture(AssetRef asset) => asset.IsNone ? null : _resources.GetTexture(asset);

    private Graphics.Text.Font? TryFont(AssetRef asset)
    {
        try
        {
            return Graphics.Text.Font.Load(_assets.ReadAllBytes(asset.Guid), _assets.Get(asset.Guid)?.Path ?? asset.ToString());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or ArgumentException)
        {
            LogTextureDecodeFailed(_logger, ex, asset.ToString());
            return null;
        }
    }

    private LoadingScreenOptions OptionsFrom(LoadingScreenSettings settings) => new()
    {
        Title = settings.Title,
        Background = settings.Background,
        Foreground = settings.Foreground,
        Accent = settings.Accent,
        Logo = Ref(settings.Logo),
        Font = Ref(settings.Font),
        ShowProgress = settings.ShowProgress,
        ShowPercentage = settings.ShowPercentage,
        Tips = [.. settings.Tips],
        TipSeconds = settings.TipSeconds,
    };

    [LoggerMessage(EventId = 3108, Level = LogLevel.Warning, Message = "Loading {Path}: an asset could not be decoded and shows as missing")]
    private static partial void LogTextureDecodeFailed(ILogger logger, Exception exception, string path);

    private sealed class ManualLoadingScreen(LoadingScreenView view) : ILoadingScreenHandle
    {
        public LoadingScreenView View { get; } = view;

        public float? Progress { get; private set; }

        public string? Task { get; private set; }

        public void Report(float? progress, string? task = null) => (Progress, Task) = (progress is { } p ? Math.Clamp(p, 0f, 1f) : null, task);

        public void Dispose() => View.Closing = true;
    }
}
