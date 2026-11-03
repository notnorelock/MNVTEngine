using MNVTEngine.Core.Timing;
using MNVTEngine.Mathematics;

namespace MNVTEngine;

/// <summary>
/// Per-thread pointer to the scene currently executing callbacks. It backs the static <see cref="Time"/> and
/// <see cref="Input"/> facades so gameplay code reads like <c>Time.DeltaTime</c> while independent scenes
/// (editor, tests) on different threads never see each other's state.
/// </summary>
internal static class SceneContext
{
    [ThreadStatic]
    private static Scene? _current;

    public static Scene Current => _current
        ?? throw new InvalidOperationException("Time and Input are only available inside scene callbacks (Awake, Start, Update, ...).");

    public static Scene? Enter(Scene scene)
    {
        var previous = _current;
        _current = scene;
        return previous;
    }

    public static void Exit(Scene? previous) => _current = previous;
}

/// <summary>Frame timing for gameplay code. Inside FixedUpdate, <see cref="DeltaTime"/> is the fixed step.</summary>
public static class Time
{
    /// <summary>Seconds since the previous frame (or the fixed step inside FixedUpdate), after time scale.</summary>
    public static float DeltaTime => SceneContext.Current.InFixedUpdate ? SceneContext.Current.FixedDeltaTime : SceneContext.Current.FrameTime.DeltaTime;

    public static float UnscaledDeltaTime => SceneContext.Current.FrameTime.UnscaledDeltaTime;

    public static float FixedDeltaTime => SceneContext.Current.FixedDeltaTime;

    /// <summary>Scaled seconds since the scene started running.</summary>
    public static float TimeSinceStart => (float)SceneContext.Current.FrameTime.TotalTime;

    public static long FrameCount => SceneContext.Current.FrameTime.FrameCount;

    internal static FrameTime Frame => SceneContext.Current.FrameTime;
}

/// <summary>Keyboard and mouse state for gameplay code. Reads nothing (all keys up) when the scene has no input source.</summary>
public static class Input
{
    private static InputState State => SceneContext.Current.Input ?? EmptyInput;

    private static InputState EmptyInput { get; } = new();

    /// <summary>Digital axis in [-1, 1]: "Horizontal" (A/D, arrows), "Vertical" (S/W, arrows), or custom axes.</summary>
    public static float GetAxis(string name) => State.GetAxis(name);

    public static bool GetKey(Key key) => State.IsKeyDown(key);

    public static bool GetKeyDown(Key key) => State.WasKeyPressed(key);

    public static bool GetKeyUp(Key key) => State.WasKeyReleased(key);

    public static bool GetMouseButton(MouseButton button) => State.IsMouseButtonDown(button);

    public static bool GetMouseButtonDown(MouseButton button) => State.WasMouseButtonPressed(button);

    public static bool GetMouseButtonUp(MouseButton button) => State.WasMouseButtonReleased(button);

    /// <summary>Cursor position in window pixels, origin top-left. Convert with <see cref="Camera.ScreenToWorld"/>.</summary>
    public static Vector2 MousePosition => State.MousePosition;

    public static Vector2 ScrollDelta => State.ScrollDelta;
}

/// <summary>Scene switching for gameplay code.</summary>
public static class SceneManager
{
    /// <summary>The scene whose callbacks are running on this thread.</summary>
    /// <exception cref="InvalidOperationException">Called outside scene callbacks.</exception>
    public static Scene ActiveScene => SceneContext.Current;

    /// <summary>Replaces the running scene at the end of the current frame. Path is relative to the project's Assets folder.</summary>
    /// <exception cref="InvalidOperationException">The scene is not hosted by a runtime that can load scenes.</exception>
    public static void LoadScene(string path) => Loader.RequestLoad(path);

    /// <summary>Replaces the running scene at the end of the current frame.</summary>
    public static void LoadScene(AssetRef scene) => Loader.RequestLoad(scene);

    private static ISceneLoader Loader =>
        SceneContext.Current.Loader ?? throw new InvalidOperationException("This scene is not hosted by a runtime that can load scenes.");
}

/// <summary>Implemented by the runtime host to service <see cref="SceneManager"/>.</summary>
public interface ISceneLoader
{
    void RequestLoad(string path);

    void RequestLoad(AssetRef scene);
}
