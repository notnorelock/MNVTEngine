using MNVTEngine.Scripting.VM;

namespace MNVTEngine.Editor.Services;

/// <summary>Where a script stopped: which runner, which coroutine, which source line.</summary>
public sealed record ScriptBreak(MissionRunner Runner, ScriptFiber Fiber, int Line)
{
    public Guid Script => Runner.Script.Guid;
}

/// <summary>
/// Debugging of <c>.mnvts</c> scripts in Play Mode (UI-free). Holds breakpoints per script asset and hooks every
/// <see cref="MissionRunner"/> VM of the playing scene. When a coroutine reaches a breakpoint it is paused and
/// <see cref="Break"/> describes where; the editor freezes Play Mode until <see cref="Continue"/> or <see cref="Step"/>.
/// Other coroutines are not interrupted mid-statement: the VM only pauses the one that hit the breakpoint, and the
/// editor stops ticking the scene while a break is shown.
/// </summary>
public sealed class ScriptDebugSession
{
    private readonly Dictionary<Guid, SortedSet<int>> _breakpoints = [];
    private ScriptFiber? _stepping;

    /// <summary>The current stop, or null while running.</summary>
    public ScriptBreak? Break { get; private set; }

    public IEnumerable<(Guid Script, int Line)> Breakpoints =>
        _breakpoints.SelectMany(p => p.Value.Select(line => (p.Key, line)));

    public bool HasBreakpoint(Guid script, int line) => _breakpoints.TryGetValue(script, out var lines) && lines.Contains(line);

    public void ToggleBreakpoint(Guid script, int line)
    {
        if (!_breakpoints.TryGetValue(script, out var lines))
        {
            _breakpoints[script] = lines = [];
        }

        if (!lines.Remove(line))
        {
            lines.Add(line);
        }
    }

    public void ClearBreakpoints() => _breakpoints.Clear();

    /// <summary>
    /// Hooks every mission runner of the scene that is not hooked yet. Call before each Play Mode tick: runners create
    /// their VM in their first update, and the hook must be in place before its first statement.
    /// </summary>
    public void Attach(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        foreach (var runner in scene.FindObjectsOfType<MissionRunner>())
        {
            if (runner.Debugger is not Hook)
            {
                runner.Debugger = new Hook(this, runner);
            }
        }
    }

    /// <summary>Resumes the stopped coroutine.</summary>
    public void Continue()
    {
        if (Break is { } stop)
        {
            Break = null;
            stop.Runner.Vm?.Resume(stop.Fiber);
        }
    }

    /// <summary>Resumes the stopped coroutine and stops again at its next statement.</summary>
    public void Step()
    {
        if (Break is { } stop)
        {
            _stepping = stop.Fiber;
            Continue();
        }
    }

    /// <summary>Play Mode ended: forget the stop (the scene and its VMs are gone); breakpoints stay.</summary>
    public void Reset()
    {
        Break = null;
        _stepping = null;
    }

    private DebugAction OnStatement(MissionRunner runner, ScriptFiber fiber, int line)
    {
        if (Break is not null)
        {
            return DebugAction.Continue; // one stop at a time; the editor freezes the scene while it is shown
        }

        var stepHere = ReferenceEquals(_stepping, fiber);
        if (!stepHere && !HasBreakpoint(runner.Script.Guid, line))
        {
            return DebugAction.Continue;
        }

        _stepping = null;
        Break = new ScriptBreak(runner, fiber, line);
        return DebugAction.Pause;
    }

    private sealed class Hook(ScriptDebugSession session, MissionRunner runner) : IScriptDebugger
    {
        public DebugAction OnStatement(ScriptFiber fiber, int line) => session.OnStatement(runner, fiber, line);
    }
}
