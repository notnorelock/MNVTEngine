using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Diagnostics;

/// <summary>
/// Debug drawing and logging for game and engine code.
/// <list type="bullet">
/// <item>Drawing: geometry defaults to world space (scene units through the camera); <c>DrawText</c>/<c>DrawScreenText</c>
/// use screen pixels (origin top-left), <c>DrawWorldText</c>/<c>DrawLabel</c> follow a world position. <c>duration</c> 0
/// draws for one frame, seconds keep it that long, <see cref="Persistent"/> keeps it until <see cref="Remove"/>.</item>
/// <item>Draws go to the buffer the host installed (<see cref="Buffer"/>). Without one, in Release builds, or with
/// drawing switched off, every call returns immediately.</item>
/// <item>Logging: <c>Log</c>/<c>LogWarning</c>/<c>LogError</c>/<c>LogException</c> use the logger named after the calling class
/// (or <c>category</c>) and record class, method, file and line.</item>
/// </list>
/// The scene is 2D without a depth buffer: debug draws are always drawn on top of the scene, world space first.
/// </summary>
public static class Debug
{
    public const float Persistent = float.PositiveInfinity;

    private static readonly ConcurrentDictionary<string, GameLog> Loggers = new(StringComparer.Ordinal);

    /// <summary>Set by the host (runtime, editor Play Mode); null disables drawing.</summary>
    public static DebugCommandBuffer? Buffer { get; set; }

    /// <summary>Console commands of the running game; null when no debug console exists (Release builds).</summary>
    public static DebugCommandRegistry? Commands { get; set; }

    /// <summary>Toggled with the debug drawing key (F4 by default).</summary>
    public static bool DrawingEnabled { get; set; } = true;

    public static bool IsDrawing => DrawingEnabled && Buffer is not null;

    public static DebugDrawHandle DrawLine(Vector2 from, Vector2 to, Color color, float duration = 0f, float thickness = 1f, DebugSpace space = DebugSpace.World) =>
        Add(new DebugDrawCommand { Shape = DebugShape.Line, Space = space, A = from, B = to, Color = color, Thickness = thickness, Remaining = duration });

    public static DebugDrawHandle DrawRay(Vector2 origin, Vector2 direction, Color color, float duration = 0f, float thickness = 1f, DebugSpace space = DebugSpace.World) =>
        DrawLine(origin, origin + direction, color, duration, thickness, space);

    public static DebugDrawHandle DrawRect(Rect rect, Color color, float duration = 0f, bool filled = false, float thickness = 1f, DebugSpace space = DebugSpace.World) =>
        Add(new DebugDrawCommand { Shape = DebugShape.Rect, Space = space, A = rect.Min, B = rect.Max, Color = color, Filled = filled, Thickness = thickness, Remaining = duration });

    /// <summary>The XY extent of 3D bounds (or a 2D bounding box).</summary>
    public static DebugDrawHandle DrawBounds(Bounds bounds, Color color, float duration = 0f, DebugSpace space = DebugSpace.World) =>
        DrawRect(new Rect(bounds.Min.X, bounds.Min.Y, bounds.Size.X, bounds.Size.Y), color, duration, false, 1f, space);

    public static DebugDrawHandle DrawBounds(Rect bounds, Color color, float duration = 0f, DebugSpace space = DebugSpace.World) =>
        DrawRect(bounds, color, duration, false, 1f, space);

    public static DebugDrawHandle DrawCircle(Vector2 center, float radius, Color color, float duration = 0f, bool filled = false, float thickness = 1f, DebugSpace space = DebugSpace.World) =>
        Add(new DebugDrawCommand { Shape = DebugShape.Circle, Space = space, A = center, Size = radius, Color = color, Filled = filled, Thickness = thickness, Remaining = duration });

    /// <param name="size">Diameter in pixels, whatever the space.</param>
    public static DebugDrawHandle DrawPoint(Vector2 position, Color color, float size = 6f, float duration = 0f, DebugSpace space = DebugSpace.World) =>
        Add(new DebugDrawCommand { Shape = DebugShape.Point, Space = space, A = position, Size = size, Color = color, Remaining = duration });

    /// <summary>Lines of a <paramref name="columns"/> × <paramref name="rows"/> grid starting at <paramref name="origin"/> (one command per line).</summary>
    public static void DrawGrid(Vector2 origin, Vector2 cellSize, int columns, int rows, Color color, float duration = 0f, DebugSpace space = DebugSpace.World)
    {
        if (!IsDrawing || columns <= 0 || rows <= 0)
        {
            return;
        }

        var size = new Vector2(cellSize.X * columns, cellSize.Y * rows);
        for (var x = 0; x <= columns; x++)
        {
            var at = origin + new Vector2(cellSize.X * x, 0);
            DrawLine(at, at + new Vector2(0, size.Y), color, duration, 1f, space);
        }

        for (var y = 0; y <= rows; y++)
        {
            var at = origin + new Vector2(0, cellSize.Y * y);
            DrawLine(at, at + new Vector2(size.X, 0), color, duration, 1f, space);
        }
    }

    /// <summary>Text at a screen position in pixels (origin top-left).</summary>
    public static DebugDrawHandle DrawText(string text, Vector2 screenPosition, Color color, float fontSize = 16f, TextAlignment alignment = TextAlignment.Left, Color background = default, float duration = 0f) =>
        DrawScreenText(text, screenPosition, color, fontSize, alignment, background, duration);

    public static DebugDrawHandle DrawScreenText(string text, Vector2 screenPosition, Color color, float fontSize = 16f, TextAlignment alignment = TextAlignment.Left, Color background = default, float duration = 0f) =>
        AddText(text, DebugSpace.Screen, screenPosition, color, fontSize, alignment, background, duration);

    /// <summary>Text anchored at a world position; its size stays in pixels.</summary>
    public static DebugDrawHandle DrawWorldText(string text, Vector2 worldPosition, Color color, float fontSize = 16f, TextAlignment alignment = TextAlignment.Center, Color background = default, float duration = 0f) =>
        AddText(text, DebugSpace.World, worldPosition, color, fontSize, alignment, background, duration);

    /// <summary>A centered, readable tag above a world position (dark background).</summary>
    public static DebugDrawHandle DrawLabel(string text, Vector2 worldPosition, Color color, float duration = 0f) =>
        AddText(text, DebugSpace.World, worldPosition, color, 14f, TextAlignment.Center, new Color(0f, 0f, 0f, 0.6f), duration);

    public static bool Remove(DebugDrawHandle handle) => Buffer?.Remove(handle) ?? false;

    /// <summary>Removes every timed and persistent draw.</summary>
    public static void ClearPersistent() => Buffer?.Clear();

    public static void Log(object? message, string? category = null, [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Write(LogLevel.Information, message, null, category, member, file, line);

    public static void LogWarning(object? message, string? category = null, [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Write(LogLevel.Warning, message, null, category, member, file, line);

    public static void LogError(object? message, string? category = null, [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Write(LogLevel.Error, message, null, category, member, file, line);

    public static void LogException(Exception exception, string? category = null, [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Write(LogLevel.Error, exception.Message, exception, category, member, file, line);
    }

    private static DebugDrawHandle AddText(string text, DebugSpace space, Vector2 position, Color color, float fontSize, TextAlignment alignment, Color background, float duration) =>
        string.IsNullOrEmpty(text)
            ? DebugDrawHandle.None
            : Add(new DebugDrawCommand { Shape = DebugShape.Text, Space = space, A = position, Text = text, Color = color, Size = fontSize, Alignment = alignment, Background = background, Remaining = duration });

    private static DebugDrawHandle Add(in DebugDrawCommand command) =>
        DrawingEnabled && Buffer is { } buffer && !float.IsNaN(command.Remaining) ? buffer.Add(command) : DebugDrawHandle.None;

    private static void Write(LogLevel level, object? message, Exception? exception, string? category, string member, string file, int line)
    {
        Type? caller = null;
        foreach (var frame in new StackTrace(1, fNeedFileInfo: false).GetFrames())
        {
            if (frame.GetMethod()?.DeclaringType is { } type && type != typeof(Debug))
            {
                caller = type; // first frame outside this facade (inlining-safe)
                break;
            }
        }

        var name = category ?? caller?.FullName ?? "Game";
        var log = Loggers.GetOrAdd(name, static n => GameLog.For(n));
        log.Write(level, message?.ToString() ?? "null", exception, caller, member, file, line);
    }
}
