using Microsoft.Extensions.Logging;
using MNVTEngine.Core.Timing;
using MNVTEngine.Serialization;

namespace MNVTEngine.Editor.Services;

public enum PlayState
{
    Editing,
    Playing,
    Paused,
}

/// <summary>
/// Runs a copy of the edit scene. The copy is made by saving and re-loading through the scene serializer,
/// so it is exactly what a standalone build would load, shares no objects with the edit scene, and keeps
/// object ids (selection carries over). Stop discards the copy; the edit scene is never touched.
/// </summary>
public sealed partial class PlayModeController(EditorSession session, ILoggerFactory loggerFactory) : ISceneLoader
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("PlayMode");
    private GameClock _clock = new();
    private FixedTimestep _fixed = new(session.Project.FixedTimestep);
    private string? _pendingScene;
    private bool _stepRequested;

    public PlayState State { get; private set; } = PlayState.Editing;

    public bool IsPlaying => State != PlayState.Editing;

    public Scene? PlayScene { get; private set; }

    /// <summary>The scene panels should display: the play copy while playing, otherwise the edit scene.</summary>
    public Scene ActiveScene => PlayScene ?? session.Scene;

    /// <summary>Input seen by the running game. The editor feeds it only while the Game view has focus.</summary>
    public InputState Input { get; } = new();

    /// <summary>Time of the running game (starts at zero on Play).</summary>
    public FrameTime Time => _clock.Current;

    /// <exception cref="InvalidOperationException">Already playing.</exception>
    public void Play()
    {
        if (IsPlaying)
        {
            throw new InvalidOperationException("Already in Play Mode.");
        }

        var copy = session.Serializer.Deserialize(session.Serializer.Serialize(session.Scene), session.ScenePath ?? session.Scene.Name);
        foreach (var diagnostic in copy.Diagnostics)
        {
            LogCopyDiagnostic(_logger, diagnostic.ToString());
        }

        _clock = new GameClock();
        _fixed = new FixedTimestep(session.Project.FixedTimestep);
        _pendingScene = null;
        Input.ReleaseAll();
        Start(copy.Scene);
        State = PlayState.Playing;
        LogEntered(_logger, copy.Scene.Name);
    }

    public void Stop()
    {
        if (!IsPlaying)
        {
            return;
        }

        PlayScene?.Unload();
        PlayScene = null;
        State = PlayState.Editing;
        LogExited(_logger);
    }

    public void TogglePause()
    {
        if (IsPlaying)
        {
            State = State == PlayState.Paused ? PlayState.Playing : PlayState.Paused;
        }
    }

    /// <summary>While paused, advances exactly one fixed step and one frame on the next <see cref="Tick"/>.</summary>
    public void Step()
    {
        if (State == PlayState.Paused)
        {
            _stepRequested = true;
        }
    }

    /// <summary>Advances the running game by measured real time. Call once per editor frame.</summary>
    public void Tick(double elapsedSeconds)
    {
        if (PlayScene is null)
        {
            return;
        }

        if (State == PlayState.Paused && !_stepRequested)
        {
            return;
        }

        FrameTime time;
        int steps;
        if (_stepRequested)
        {
            _stepRequested = false;
            time = _clock.Advance(_fixed.StepSeconds);
            steps = 1;
        }
        else
        {
            time = _clock.Advance(elapsedSeconds);
            steps = _fixed.Accumulate(time.DeltaTime);
        }

        for (var i = 0; i < steps; i++)
        {
            PlayScene.FixedUpdate(_fixed.StepSeconds);
        }

        PlayScene.Update(time);

        if (_pendingScene is { } path)
        {
            _pendingScene = null;
            LoadPlayScene(path);
        }
    }

    /// <summary>Services <see cref="SceneManager.LoadScene"/> inside Play Mode; only the play copy changes.</summary>
    public void RequestLoad(string path)
    {
        session.Project.ResolveAsset(path);
        _pendingScene = path;
    }

    private void LoadPlayScene(string path)
    {
        try
        {
            var result = session.Serializer.Deserialize(File.ReadAllText(session.Project.ResolveAsset(path)), path);
            PlayScene?.Unload();
            Start(result.Scene);
        }
        catch (Exception ex) when (ex is SerializationException or IOException or ArgumentException)
        {
            LogLoadFailed(_logger, ex, path);
        }
    }

    private void Start(Scene scene)
    {
        scene.Logger = loggerFactory.CreateLogger($"Scene.{scene.Name}");
        scene.Input = Input;
        scene.Loader = this;
        PlayScene = scene;
        scene.Start();
    }

    [LoggerMessage(EventId = 4000, Level = LogLevel.Information, Message = "Play Mode: '{Scene}'")]
    private static partial void LogEntered(ILogger logger, string scene);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "Play Mode stopped; edit scene restored")]
    private static partial void LogExited(ILogger logger);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Warning, Message = "Play copy: {Diagnostic}")]
    private static partial void LogCopyDiagnostic(ILogger logger, string diagnostic);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Error, Message = "Play Mode could not load scene '{Scene}'")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception, string scene);
}
