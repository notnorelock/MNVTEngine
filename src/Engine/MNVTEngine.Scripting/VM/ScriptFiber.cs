using System.Text;

namespace MNVTEngine.Scripting.VM;

public enum FiberState
{
    /// <summary>Runnable now (or at the next tick after a yield).</summary>
    Ready,
    Sleeping,
    WaitingForEvent,

    /// <summary>Stopped by the debugger; continue with <see cref="ScriptVM.Resume"/>.</summary>
    Paused,
    Finished,
    Faulted,
    Cancelled,
}

public sealed record StackFrameInfo(string Function, string Source, int Line)
{
    public override string ToString() => $"at {Function} ({Source}:{Line})";
}

/// <summary>An uncaught script error with the script call stack at the point it was raised.</summary>
public sealed record ScriptError(string Message, IReadOnlyList<StackFrameInfo> StackTrace, Exception? HostException = null)
{
    public override string ToString()
    {
        var builder = new StringBuilder(Message);
        foreach (var frame in StackTrace)
        {
            builder.Append("\n  ").Append(frame);
        }

        return builder.ToString();
    }
}

/// <summary>A coroutine: its own value stack and call frames, suspended at waits and resumed by the VM's tick.</summary>
public sealed class ScriptFiber
{
    internal ScriptFiber(ScriptVM vm, int id, int function, object? context)
    {
        Vm = vm;
        Id = id;
        Function = function;
        Context = context;
    }

    public int Id { get; }

    /// <summary>Entry function index.</summary>
    public int Function { get; }

    public string Name => Vm.Script.Functions[Function].Name;

    /// <summary>Host data (e.g. owning mission and objective); inherited by fibers this one spawns.</summary>
    public object? Context { get; }

    public FiberState State { get; internal set; } = FiberState.Ready;

    public bool IsAlive => State is FiberState.Ready or FiberState.Sleeping or FiberState.WaitingForEvent or FiberState.Paused;

    public ScriptError? Error { get; internal set; }

    /// <summary>Value returned by the entry function once finished.</summary>
    public ScriptValue Result { get; internal set; }

    /// <summary>Event this fiber waits for, when <see cref="State"/> is WaitingForEvent.</summary>
    public string? WaitingEvent { get; internal set; }

    public double WakeTime { get; internal set; }

    internal ScriptVM Vm { get; }

    internal ScriptValue[] Stack { get; set; } = new ScriptValue[32];

    internal int Sp { get; set; }

    internal List<Frame> Frames { get; } = [];

    internal List<TryHandler> Handlers { get; } = [];

    internal long WakeTick { get; set; }

    internal int ResumesThisTick { get; set; }

    /// <summary>Statement the debugger already stopped at, so resuming does not stop again immediately.</summary>
    internal (int Frame, int Pc)? DebuggerResumePoint { get; set; }

    /// <summary>Stops the fiber; it is removed at the end of the tick and runs no further code.</summary>
    public void Cancel()
    {
        if (IsAlive)
        {
            State = FiberState.Cancelled;
        }
    }

    public IReadOnlyList<StackFrameInfo> GetStackTrace()
    {
        var trace = new List<StackFrameInfo>();
        for (var i = Frames.Count - 1; i >= 0; i--)
        {
            var frame = Frames[i];
            var function = Vm.Script.Functions[frame.Function];
            var pc = i == Frames.Count - 1 ? frame.Pc : Math.Max(0, frame.Pc - 1);
            trace.Add(new StackFrameInfo(function.Name, Vm.Script.SourceName, function.LineAt(pc)));
        }

        return trace;
    }

    /// <summary>Named locals of a frame (0 = innermost), for debuggers.</summary>
    public IReadOnlyList<(string Name, ScriptValue Value)> GetLocals(int frameFromTop = 0)
    {
        if (frameFromTop < 0 || frameFromTop >= Frames.Count)
        {
            return [];
        }

        var frame = Frames[Frames.Count - 1 - frameFromTop];
        var function = Vm.Script.Functions[frame.Function];
        var result = new List<(string, ScriptValue)>();
        for (var slot = 0; slot < function.LocalCount; slot++)
        {
            if (!function.LocalNames[slot].StartsWith('$'))
            {
                result.Add((function.LocalNames[slot], Stack[frame.Base + slot]));
            }
        }

        return result;
    }

    internal struct Frame(int function, int @base)
    {
        public int Function = function;
        public int Pc;
        public int Base = @base;
    }

    internal readonly record struct TryHandler(int Frame, int Pc, int Sp);
}

public enum DebugAction
{
    Continue,
    Pause,
}

/// <summary>Called before each statement while attached to a VM (<see cref="ScriptVM.Debugger"/>).</summary>
public interface IScriptDebugger
{
    DebugAction OnStatement(ScriptFiber fiber, int line);
}

/// <summary>Pauses fibers at the given source lines; <see cref="ScriptVM.Resume"/> continues them.</summary>
public sealed class BreakpointDebugger : IScriptDebugger
{
    public HashSet<int> Breakpoints { get; } = [];

    public List<(ScriptFiber Fiber, int Line)> Hits { get; } = [];

    /// <summary>Records every executed line (an execution trace) when enabled.</summary>
    public List<int>? Trace { get; set; }

    public DebugAction OnStatement(ScriptFiber fiber, int line)
    {
        ArgumentNullException.ThrowIfNull(fiber);
        Trace?.Add(line);
        if (!Breakpoints.Contains(line))
        {
            return DebugAction.Continue;
        }

        Hits.Add((fiber, line));
        return DebugAction.Pause;
    }
}
