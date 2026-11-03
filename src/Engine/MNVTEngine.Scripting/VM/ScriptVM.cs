using MNVTEngine.Scripting.Bytecode;

namespace MNVTEngine.Scripting.VM;

/// <summary>Context of a host function call. Argument accessors produce script-catchable errors on wrong types.</summary>
public readonly ref struct NativeCall(ScriptVM vm, ScriptFiber fiber, string name, ReadOnlySpan<ScriptValue> arguments)
{
    public ScriptVM Vm { get; } = vm;

    public ScriptFiber Fiber { get; } = fiber;

    public string Name { get; } = name;

    public ReadOnlySpan<ScriptValue> Arguments { get; } = arguments;

    public int Count => Arguments.Length;

    public ScriptValue this[int index] => index < Arguments.Length ? Arguments[index] : ScriptValue.Null;

    public double Number(int index) => this[index].AsNumber($"argument {index + 1} of {Name}");

    public string String(int index) => this[index].AsString($"argument {index + 1} of {Name}");

    public bool Bool(int index) => this[index].AsBool($"argument {index + 1} of {Name}");
}

public delegate ScriptValue NativeHandler(NativeCall call);

public sealed record NativeFunction(string Name, NativeHandler Handler);

public sealed record ScriptLimits
{
    /// <summary>Instructions a fiber may run before it must wait; exceeding it faults the fiber (runaway loop protection).</summary>
    public int InstructionsPerSlice { get; init; } = 100_000;

    public int MaxCallDepth { get; init; } = 200;

    public int MaxStackSize { get; init; } = 10_000;

    public int MaxFibers { get; init; } = 1_000;

    /// <summary>How often one fiber may be resumed within a tick (by events it waits for).</summary>
    public int MaxResumesPerTick { get; init; } = 16;
}

/// <summary>The script refers to host functions the VM was not given.</summary>
public sealed class ScriptLinkException(IReadOnlyList<string> missing)
    : Exception($"Script uses functions this host does not provide: {string.Join(", ", missing)}.")
{
    public IReadOnlyList<string> Missing { get; } = missing;
}

/// <summary>
/// Runs a <see cref="CompiledScript"/>. All execution happens inside <see cref="Tick"/> (and <see cref="Initialize"/>), on the
/// caller's thread, in bounded slices: a fiber runs until it waits, finishes, fails, or uses up its instruction budget,
/// so script code can never block the game loop.
/// </summary>
public sealed partial class ScriptVM
{
    private readonly NativeHandler[] _natives;
    private readonly List<ScriptFiber> _fibers = [];
    private Dictionary<int, int>[]? _lineStarts;
    private int _nextFiberId = 1;

    public ScriptVM(CompiledScript script, IEnumerable<NativeFunction>? natives = null, ScriptLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        Script = script;
        Limits = limits ?? new ScriptLimits();
        Globals = new ScriptValue[script.Globals.Length];

        var available = Builtins.Create().Concat(natives ?? []).ToDictionary(n => n.Name, n => n.Handler, StringComparer.Ordinal);
        var missing = script.NativeNames.Where(n => !available.ContainsKey(n)).ToList();
        if (missing.Count > 0)
        {
            throw new ScriptLinkException(missing);
        }

        _natives = [.. script.NativeNames.Select(n => available[n])];
    }

    public CompiledScript Script { get; }

    public ScriptLimits Limits { get; }

    public ScriptValue[] Globals { get; }

    /// <summary>Script time in seconds (sum of tick deltas).</summary>
    public double Time { get; private set; }

    public long TickCount { get; private set; }

    public IReadOnlyList<ScriptFiber> Fibers => _fibers;

    public IScriptDebugger? Debugger { get; set; }

    /// <summary>Raised when a fiber stops with an uncaught error.</summary>
    public event Action<ScriptFiber>? FiberFaulted;

    /// <summary>Raised by <see cref="RaiseEvent"/> after built-in handling; hosts use it for their own event handlers.</summary>
    public event Action<string, ScriptValue>? EventRaised;

    /// <summary>Runs global initializers, then spawns the top-level <c>on start</c> handler.</summary>
    /// <exception cref="ScriptRuntimeException">An initializer failed or tried to wait.</exception>
    /// <param name="runStartHandlers">False when state is restored afterwards (hot reload): 'on start' must not run again.</param>
    public void Initialize(bool runStartHandlers = true)
    {
        var init = CreateFiber(Script.InitFunction, [], null);
        Run(init);
        _fibers.Remove(init);
        if (init.State == FiberState.Faulted)
        {
            throw new ScriptRuntimeException($"Global initialization failed: {init.Error}");
        }

        if (init.State != FiberState.Finished)
        {
            throw new ScriptRuntimeException("Global initializers cannot wait.");
        }

        if (runStartHandlers && Script.StartFunction >= 0)
        {
            Spawn(Script.StartFunction, [], null);
        }
    }

    public ScriptValue GetGlobal(string name)
    {
        var slot = Script.FindGlobal(name);
        return slot >= 0 ? Globals[slot] : throw new KeyNotFoundException($"Script has no global '{name}'.");
    }

    public void SetGlobal(string name, ScriptValue value)
    {
        var slot = Script.FindGlobal(name);
        Globals[slot >= 0 ? slot : throw new KeyNotFoundException($"Script has no global '{name}'.")] = value;
    }

    /// <summary>Starts a function as a new fiber; it runs during the next <see cref="Tick"/>.</summary>
    /// <exception cref="InvalidOperationException">Wrong argument count, or the fiber limit is reached.</exception>
    public ScriptFiber Spawn(int function, ScriptValue[] arguments, object? context)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var target = Script.Functions[function];
        if (arguments.Length != target.ParameterCount)
        {
            throw new InvalidOperationException($"'{target.Name}' takes {target.ParameterCount} argument(s), got {arguments.Length}.");
        }

        if (_fibers.Count(f => f.IsAlive) >= Limits.MaxFibers)
        {
            throw new InvalidOperationException($"Fiber limit of {Limits.MaxFibers} reached.");
        }

        return CreateFiber(function, arguments, context);
    }

    public ScriptFiber Spawn(string functionName, params ScriptValue[] arguments)
    {
        var index = Script.FindFunction(functionName);
        return Spawn(index >= 0 ? index : throw new KeyNotFoundException($"Script has no function '{functionName}'."), arguments, null);
    }

    /// <summary>
    /// Wakes fibers waiting for <paramref name="name"/> (they receive <paramref name="payload"/>) and starts top-level
    /// <c>on event</c> handlers. Woken and started fibers run within the current or next tick.
    /// </summary>
    public void RaiseEvent(string name, ScriptValue payload = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        foreach (var fiber in _fibers.Where(f => f.State == FiberState.WaitingForEvent && f.WaitingEvent == name).ToList())
        {
            Push(fiber, payload);
            fiber.WaitingEvent = null;
            fiber.State = FiberState.Ready;
            fiber.WakeTick = TickCount;
        }

        foreach (var handler in Script.EventHandlers.Where(h => h.Mission is null && h.EventName == name))
        {
            StartHandler(handler.Function, payload, null);
        }

        EventRaised?.Invoke(name, payload);
    }

    /// <summary>Spawns an event handler function, passing the payload if it declares a parameter.</summary>
    public ScriptFiber StartHandler(int function, ScriptValue payload, object? context) =>
        Spawn(function, Script.Functions[function].ParameterCount == 1 ? [payload] : [], context);

    /// <summary>Advances time and runs every fiber that is due, each in bounded slices. Never blocks.</summary>
    public void Tick(double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds), deltaSeconds, "Delta must be finite and non-negative.");
        }

        Time += deltaSeconds;
        TickCount++;
        foreach (var fiber in _fibers)
        {
            fiber.ResumesThisTick = 0;
        }

        // Fibers woken during the tick (events, spawns) run in the same tick, within the per-fiber resume limit.
        bool progressed;
        do
        {
            progressed = false;
            foreach (var fiber in _fibers.ToArray())
            {
                if (fiber.State == FiberState.Sleeping && fiber.WakeTime <= Time)
                {
                    fiber.State = FiberState.Ready;
                    fiber.WakeTick = TickCount;
                }

                if (fiber.State == FiberState.Ready && fiber.WakeTick <= TickCount && fiber.ResumesThisTick < Limits.MaxResumesPerTick)
                {
                    fiber.ResumesThisTick++;
                    Run(fiber);
                    progressed = true;
                }
            }
        }
        while (progressed);

        _fibers.RemoveAll(f => !f.IsAlive);
    }

    /// <summary>Continues a fiber paused by the debugger.</summary>
    public void Resume(ScriptFiber fiber)
    {
        ArgumentNullException.ThrowIfNull(fiber);
        if (fiber.State == FiberState.Paused)
        {
            fiber.State = FiberState.Ready;
            fiber.WakeTick = TickCount;
        }
    }

    public int Cancel(Func<ScriptFiber, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var cancelled = 0;
        foreach (var fiber in _fibers.Where(f => f.IsAlive && predicate(f)))
        {
            fiber.Cancel();
            cancelled++;
        }

        return cancelled;
    }

    private ScriptFiber CreateFiber(int function, ScriptValue[] arguments, object? context)
    {
        var fiber = new ScriptFiber(this, _nextFiberId++, function, context) { WakeTick = TickCount };
        foreach (var argument in arguments)
        {
            Push(fiber, argument);
        }

        EnterFunction(fiber, function, arguments.Length);
        _fibers.Add(fiber);
        return fiber;
    }

    private void EnterFunction(ScriptFiber fiber, int function, int argumentCount)
    {
        if (fiber.Frames.Count >= Limits.MaxCallDepth)
        {
            throw new ScriptRuntimeException($"Call depth limit of {Limits.MaxCallDepth} exceeded (runaway recursion?).");
        }

        var target = Script.Functions[function];
        var @base = fiber.Sp - argumentCount;
        EnsureStack(fiber, @base + target.LocalCount + 16);
        for (var slot = argumentCount; slot < target.LocalCount; slot++)
        {
            fiber.Stack[@base + slot] = ScriptValue.Null;
        }

        fiber.Sp = @base + target.LocalCount;
        fiber.Frames.Add(new ScriptFiber.Frame(function, @base));
    }

    private void EnsureStack(ScriptFiber fiber, int size)
    {
        if (size > Limits.MaxStackSize)
        {
            throw new ScriptRuntimeException($"Stack limit of {Limits.MaxStackSize} values exceeded.");
        }

        if (size > fiber.Stack.Length)
        {
            var stack = fiber.Stack;
            Array.Resize(ref stack, Math.Min(Limits.MaxStackSize, Math.Max(size, stack.Length * 2)));
            fiber.Stack = stack;
        }
    }

    private void Push(ScriptFiber fiber, ScriptValue value)
    {
        EnsureStack(fiber, fiber.Sp + 1);
        fiber.Stack[fiber.Sp++] = value;
    }

    private static ScriptValue Pop(ScriptFiber fiber) => fiber.Stack[--fiber.Sp];

    private void Fault(ScriptFiber fiber, string message, Exception? hostException)
    {
        fiber.Error = new ScriptError(message, fiber.GetStackTrace(), hostException);
        fiber.State = FiberState.Faulted;
        FiberFaulted?.Invoke(fiber);
    }

    /// <summary>Unwinds to the innermost try handler (pushing the error value for 'catch'), or faults the fiber.</summary>
    private void Raise(ScriptFiber fiber, ScriptValue error, Exception? hostException)
    {
        if (fiber.Handlers.Count == 0)
        {
            Fault(fiber, error.ToString(), hostException);
            return;
        }

        var handler = fiber.Handlers[^1];
        fiber.Handlers.RemoveAt(fiber.Handlers.Count - 1);
        fiber.Frames.RemoveRange(handler.Frame + 1, fiber.Frames.Count - handler.Frame - 1);
        fiber.Sp = handler.Sp;
        Push(fiber, error);
        var frame = fiber.Frames[^1];
        frame.Pc = handler.Pc;
        fiber.Frames[^1] = frame;
    }

    private int? StatementLine(int function, int pc)
    {
        _lineStarts ??= [.. Script.Functions.Select(f => f.Lines.GroupBy(l => l.Pc).ToDictionary(g => g.Key, g => g.Last().Line))];
        return _lineStarts[function].TryGetValue(pc, out var line) ? line : null;
    }
}
