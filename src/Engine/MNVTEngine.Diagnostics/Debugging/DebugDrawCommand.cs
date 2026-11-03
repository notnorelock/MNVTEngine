using MNVTEngine.Mathematics;

namespace MNVTEngine.Diagnostics;

/// <summary>World space: scene units, through the camera. Screen space: pixels of the game view, origin top-left.</summary>
public enum DebugSpace
{
    World,
    Screen,
}

public enum DebugShape
{
    Line,
    Rect,
    Circle,
    Point,
    Text,
}

public enum TextAlignment
{
    Left,
    Center,
    Right,
}

/// <summary>Handle to a timed or persistent debug draw, for <see cref="Debug.Remove"/>.</summary>
public readonly record struct DebugDrawHandle(long Id)
{
    public static DebugDrawHandle None => default;
}

/// <summary>
/// One queued debug draw. <see cref="A"/>/<see cref="B"/>: line ends, rect min/max, circle or text position.
/// <see cref="Size"/>: circle radius, point size in pixels, or font size in pixels.
/// </summary>
public struct DebugDrawCommand
{
    public DebugShape Shape { get; set; }
    public DebugSpace Space { get; set; }
    public Vector2 A { get; set; }
    public Vector2 B { get; set; }
    public float Size { get; set; }
    public float Thickness { get; set; }
    public Color Color { get; set; }
    public bool Filled { get; set; }
    public string? Text { get; set; }
    public TextAlignment Alignment { get; set; }

    /// <summary>Background color behind text; transparent for none.</summary>
    public Color Background { get; set; }

    /// <summary>Seconds left; 0 = this frame only; +∞ = until removed.</summary>
    public float Remaining { get; set; }

    public long Id { get; set; }
}

/// <summary>
/// Fixed-capacity queue of debug draws. Commands live in one array that is compacted in place at the end of each frame
/// (one-frame draws removed, timers counted down), so drawing allocates nothing beyond the caller's strings. When full,
/// further draws are dropped and counted in <see cref="Dropped"/> (the "debug draw commands" budget reports it).
/// </summary>
public sealed class DebugCommandBuffer
{
    private DebugDrawCommand[] _commands;
    private long _nextId = 1;

    public DebugCommandBuffer(int capacity = 10_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
        _commands = new DebugDrawCommand[Math.Min(capacity, 256)];
    }

    public int Capacity { get; private set; }

    public int Count { get; private set; }

    /// <summary>Draws rejected this frame because the buffer was full.</summary>
    public int Dropped { get; private set; }

    /// <summary>Highest <see cref="Count"/> reached; with <see cref="Dropped"/> it feeds the debug draw budget.</summary>
    public int Requested => Count + Dropped;

    public ReadOnlySpan<DebugDrawCommand> Commands => _commands.AsSpan(0, Count);

    /// <summary>Changes the capacity; existing commands beyond it are discarded.</summary>
    public void Resize(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
        Count = Math.Min(Count, capacity);
        if (_commands.Length > capacity)
        {
            Array.Resize(ref _commands, capacity);
        }
    }

    public DebugDrawHandle Add(in DebugDrawCommand command)
    {
        if (Count == Capacity)
        {
            Dropped++;
            return DebugDrawHandle.None;
        }

        if (Count == _commands.Length)
        {
            Array.Resize(ref _commands, Math.Min(Capacity, _commands.Length * 2));
        }

        ref var slot = ref _commands[Count++];
        slot = command;
        slot.Id = command.Remaining > 0 ? _nextId++ : 0;
        return new DebugDrawHandle(slot.Id);
    }

    public bool Remove(DebugDrawHandle handle)
    {
        if (handle.Id == 0)
        {
            return false;
        }

        for (var i = 0; i < Count; i++)
        {
            if (_commands[i].Id == handle.Id)
            {
                _commands[i] = _commands[--Count];
                _commands[Count] = default;
                return true;
            }
        }

        return false;
    }

    /// <summary>Removes timed and persistent draws (one-frame draws are removed every frame anyway).</summary>
    public void Clear()
    {
        Array.Clear(_commands, 0, Count);
        Count = 0;
    }

    /// <summary>Called by the host after the frame was drawn: drops one-frame draws and counts timers down.</summary>
    public void EndFrame(float deltaSeconds)
    {
        var kept = 0;
        for (var i = 0; i < Count; i++)
        {
            ref var command = ref _commands[i];
            if (command.Remaining <= 0)
            {
                continue;
            }

            if (!float.IsPositiveInfinity(command.Remaining))
            {
                command.Remaining -= deltaSeconds;
                if (command.Remaining <= 0)
                {
                    continue;
                }
            }

            _commands[kept++] = command;
        }

        Array.Clear(_commands, kept, Count - kept);
        Count = kept;
        Dropped = 0;
    }
}
