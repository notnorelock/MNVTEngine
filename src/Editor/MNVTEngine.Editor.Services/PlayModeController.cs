using Microsoft.Extensions.Logging;
using MNVTEngine.Core.Timing;
using MNVTEngine.Scripting.Missions;
using MNVTEngine.Serialization;

namespace MNVTEngine.Editor.Services;

/// <summary>The running game saved for a C# code reload: scene JSON written with the old types, plus mission states.</summary>
public sealed record PlaySnapshot(string Json, string Name, IReadOnlyDictionary<Guid, MissionSaveData> Missions);

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
    private bool _quitRequested;
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

    /// <summary>Creates the diagnostics of a play session (set by the editor; null runs without diagnostics).</summary>
    public Func<Runtime.RuntimeDiagnostics>? CreateDiagnostics { get; set; }

    /// <summary>Metrics, budgets, debug drawing and console commands of the running game; null while editing.</summary>
    public Runtime.RuntimeDiagnostics? Diagnostics { get; private set; }

    /// <summary>True when the last <see cref="Tick"/> advanced the game (not paused); the host then ends the diagnostics frame after rendering.</summary>
    public bool AdvancedThisFrame { get; private set; }

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
        _quitRequested = false;
        Application.QuitRequested += OnQuitRequested;
        Input.ReleaseAll();
        Diagnostics = CreateDiagnostics?.Invoke();
        Diagnostics?.Install(); // before the scene starts, so Start() can already draw and register commands
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

        Application.QuitRequested -= OnQuitRequested;
        PlayScene?.Unload();
        PlayScene = null;
        Diagnostics?.Uninstall();
        Diagnostics = null;
        AdvancedThisFrame = false;
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
        AdvancedThisFrame = false;
        if (PlayScene is null)
        {
            return;
        }

        if (State == PlayState.Paused && !_stepRequested)
        {
            return;
        }

        AdvancedThisFrame = true;
        Diagnostics?.BeginFrame();
        using var sample = Core.Diagnostics.Profiler.Sample("PlayMode");
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
            _clock.TimeScale = PlayScene.TimeScale; // Time.TimeScale set by game code (pause menus, slow motion)
            time = _clock.Advance(elapsedSeconds);
            steps = _fixed.Accumulate(time.DeltaTime);
        }

        // The UI goes first and takes the keyboard and mouse it uses away from gameplay, as in the standalone runtime.
        if (UI.UISystem.For(PlayScene) is { } ui && ViewportSize is { X: > 0, Y: > 0 })
        {
            ui.Update(Input, ViewportSize, 1f, time.UnscaledDeltaTime);
        }

        for (var i = 0; i < steps; i++)
        {
            PlayScene.FixedUpdate(_fixed.StepSeconds);
        }

        PlayScene.Update(time);
        if (_quitRequested)
        {
            Stop(); // after the frame: game code asked to quit
            return;
        }

        if (_pendingScene is { } path)
        {
            _pendingScene = null;
            LoadPlayScene(path);
        }
    }

    /// <summary>Saves the running game before game code is swapped (null when not playing).</summary>
    public PlaySnapshot? Capture() => PlayScene is { } scene
        ? new PlaySnapshot(
            session.Serializer.Serialize(scene),
            scene.Name,
            scene.FindObjectsOfType<MissionRunner>().Where(r => r.Missions is not null).ToDictionary(r => r.Id, r => r.Missions!.Save()))
        : null;

    /// <summary>
    /// Continues the game from a snapshot after game code was reloaded. Serialized fields, transforms, runtime-spawned
    /// objects, the clock and mission progress survive; components run Awake/Start again; private fields and statics reset.
    /// </summary>
    public void Restore(PlaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (PlayScene is null)
        {
            return;
        }

        var result = session.Serializer.Deserialize(snapshot.Json, snapshot.Name);
        foreach (var diagnostic in result.Diagnostics)
        {
            LogCopyDiagnostic(_logger, diagnostic.ToString());
        }

        foreach (var runner in result.Scene.FindObjectsOfType<MissionRunner>())
        {
            if (snapshot.Missions.TryGetValue(runner.Id, out var missions))
            {
                runner.ResumeOnStart(missions);
            }
        }

        // The copy is a new scene: re-mark what was DontDestroyOnLoad (the flag is not saved in scene files).
        var persistent = PlayScene.PersistentObjects.Select(g => g.Id).ToHashSet();
        foreach (var root in result.Scene.RootObjects.Where(r => persistent.Contains(r.Id)))
        {
            root.DontDestroyOnLoad();
        }

        PlayScene.Unload();
        Input.ReleaseAll();
        Start(result.Scene);
        LogCodeReloaded(_logger);
    }

    /// <summary>Hot-reloads running mission scripts whose source changed; returns how many were swapped in.</summary>
    public int ReloadMissionScripts(IEnumerable<Guid> changedScripts)
    {
        var changed = changedScripts.ToHashSet();
        return PlayScene is null ? 0 : PlayScene.FindObjectsOfType<MissionRunner>().Count(r => changed.Contains(r.Script.Guid) && r.Reload());
    }

    /// <summary>Services <see cref="SceneManager.LoadScene"/> inside Play Mode; only the play copy changes.</summary>
    public void RequestLoad(string path)
    {
        session.Project.ResolveAsset(path);
        _pendingScene = path;
    }

    public void RequestLoad(AssetRef scene) =>
        RequestLoad(session.Assets.GetAssetType(scene.Guid) == Serialization.AssetTypes.Scene && session.Assets.TryGetPath(scene.Guid, out var path)
            ? path
            : throw new ArgumentException($"{scene} is not a scene asset in this project.", nameof(scene)));

    private void LoadPlayScene(string path)
    {
        try
        {
            var result = session.Serializer.Deserialize(File.ReadAllText(session.Project.ResolveAsset(path)), path);
            Start(result.Scene, carryFrom: PlayScene);
        }
        catch (Exception ex) when (ex is SerializationException or IOException or ArgumentException)
        {
            LogLoadFailed(_logger, ex, path);
        }
    }

    private void OnQuitRequested() => _quitRequested = true;

    /// <summary>Audio output for Play Mode (owned by the editor); null plays nothing.</summary>
    public Audio.IAudioDevice? Audio { get; set; }

    /// <summary>Fonts, clipboard and assets for the game UI of play scenes (owned by the editor); null attaches no UI.</summary>
    public UI.UIServices? UIServices { get; set; }

    /// <summary>Size of the Game view in pixels: the game UI lays out to it.</summary>
    public Mathematics.Vector2 ViewportSize { get; set; }

    /// <param name="carryFrom">The scene being replaced: its DontDestroyOnLoad objects move in, then it is unloaded.</param>
    private void Start(Scene scene, Scene? carryFrom = null)
    {
        scene.Logger = loggerFactory.CreateLogger($"Scene.{scene.Name}");
        scene.Input = Input;
        scene.Loader = this;
        scene.Prefabs = session.Prefabs;
        scene.TextAssets = session.Prefabs;
        scene.ModelAnimations = session.ModelAnimations;
        scene.MeshData = session.ModelAnimations;
        if (Audio is { } audio)
        {
            AudioSystem.Attach(scene, audio, clip => session.Assets.ReadAllBytes(clip.Guid));
        }

        if (UIServices is { } ui)
        {
            UI.UISystem.Attach(scene, ui);
        }

        carryFrom?.MovePersistentObjectsTo(scene);
        carryFrom?.Unload();
        PlayScene = scene;
        scene.Start();
    }

    [LoggerMessage(EventId = 4000, Level = LogLevel.Information, Message = "Play Mode: '{Scene}'")]
    private static partial void LogEntered(ILogger logger, string scene);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "Play Mode stopped; edit scene restored")]
    private static partial void LogExited(ILogger logger);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Warning, Message = "Play copy: {Diagnostic}")]
    private static partial void LogCopyDiagnostic(ILogger logger, string diagnostic);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Information, Message = "Play Mode continues with the reloaded game code")]
    private static partial void LogCodeReloaded(ILogger logger);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Error, Message = "Play Mode could not load scene '{Scene}'")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception, string scene);
}
