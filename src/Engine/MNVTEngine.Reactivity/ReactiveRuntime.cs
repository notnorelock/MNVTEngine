// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

namespace MNVTEngine.Reactivity;

/// <summary>
/// The reactive graph of one thread (the UI uses the game thread's). It knows which observer is running, which owner
/// new nodes belong to, the batch depth and the effects waiting to run. Every node remembers the runtime it was made on
/// and refuses to be touched from another thread: post work with a <see cref="ReactiveDispatcher"/> instead.
/// <para>
/// Invalidation is push-pull (as in SolidJS and the "Reactively" algorithm): a write marks direct observers Dirty and
/// everything downstream Check; effects reached are queued. Running an effect, or reading a computed value, first
/// brings Check sources up to date and recomputes only when one of them really changed, so a computed value that
/// recomputes to an equal result stops the update there.
/// </para>
/// </summary>
public sealed class ReactiveRuntime
{
    [ThreadStatic]
    private static ReactiveRuntime? t_current;

    private readonly List<Exception> _flushErrors = [];
    private readonly Stack<List<ReactiveNode>> _trackBuffers = new();
    private List<EffectNode> _queue = [];
    private List<EffectNode> _running = [];
    private int _batchDepth;
    private bool _flushing;
    private int _runStamp;

    private ReactiveRuntime() => ThreadId = Environment.CurrentManagedThreadId;

    /// <summary>The runtime of the calling thread, created on first use.</summary>
    public static ReactiveRuntime Current => t_current ??= new ReactiveRuntime();

    /// <summary>Effects that keep re-triggering each other stop after this many flush rounds.</summary>
    public int MaxFlushRounds { get; set; } = 100;

    /// <summary>
    /// Receives exceptions thrown by effects and cleanups. Null (the default) rethrows: during a flush, after every
    /// queued effect ran, as a <see cref="ReactiveEffectException"/>. UI hosts log them instead, so one broken binding
    /// cannot stop a frame.
    /// </summary>
    public Action<Exception>? ErrorHandler { get; set; }

    public ReactiveDiagnostics Diagnostics { get; } = new();

    /// <summary>True while a batch is open on this runtime.</summary>
    public bool InBatch => _batchDepth > 0;

    internal int ThreadId { get; }

    internal ObserverNode? CurrentObserver { get; set; }

    internal ReactiveOwner? CurrentOwner { get; set; }

    internal void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != ThreadId)
        {
            throw new InvalidOperationException(
                "Reactive state belongs to the thread that created it; post the change with a ReactiveDispatcher instead.");
        }
    }

    internal void BeginBatch() => _batchDepth++;

    internal void EndBatch()
    {
        if (--_batchDepth == 0)
        {
            Flush();
        }
    }

    internal void Track(ReactiveNode source)
    {
        var observer = CurrentObserver;
        if (observer is null || observer.Disposed)
        {
            return;
        }

        if (source.LastReader == observer && source.LastReadStamp == observer.RunStamp)
        {
            return; // read twice in one run
        }

        source.LastReader = observer;
        source.LastReadStamp = observer.RunStamp;
        observer.Tracking.Add(source);
    }

    internal List<ReactiveNode> RentTrackBuffer() => _trackBuffers.Count > 0 ? _trackBuffers.Pop() : new List<ReactiveNode>(8);

    internal void ReturnTrackBuffer(List<ReactiveNode> buffer)
    {
        buffer.Clear();
        _trackBuffers.Push(buffer);
    }

    internal int NextStamp() => ++_runStamp;

    internal void Enqueue(EffectNode effect)
    {
        if (effect.Queued || effect.Disposed)
        {
            return;
        }

        effect.Queued = true;
        _queue.Add(effect);
    }

    /// <summary>Runs queued effects unless a batch is open or a flush is already running.</summary>
    internal void FlushIfIdle()
    {
        if (_batchDepth == 0)
        {
            Flush();
        }
    }

    /// <summary>Reports an exception from an effect's first run or a cleanup (outside a flush).</summary>
    internal void ReportImmediate(Exception error)
    {
        Diagnostics.Errors++;
        if (ErrorHandler is { } handler)
        {
            handler(error);
            return;
        }

        throw new ReactiveEffectException([error]);
    }

    private void Flush()
    {
        if (_flushing || _queue.Count == 0)
        {
            return;
        }

        _flushing = true;
        var rounds = 0;
        try
        {
            while (_queue.Count > 0)
            {
                if (++rounds > MaxFlushRounds)
                {
                    var names = string.Join(", ", _queue.Take(5).Select(e => e.Name ?? "effect"));
                    foreach (var e in _queue)
                    {
                        e.Queued = false;
                        e.State = NodeState.Clean;
                    }

                    _queue.Clear();
                    Diagnostics.LoopsStopped++;
                    _flushErrors.Add(new ReactiveLoopException(
                        $"Effects kept re-triggering each other and were stopped after {MaxFlushRounds} rounds (still queued: {names})."));
                    break;
                }

                // Effects queued by this round run in the next one, in the order they were queued.
                (_running, _queue) = (_queue, _running);
                for (var i = 0; i < _running.Count; i++)
                {
                    var effect = _running[i];
                    effect.Queued = false;
                    if (effect.Disposed)
                    {
                        continue;
                    }

                    try
                    {
                        effect.UpdateIfNecessary();
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        _flushErrors.Add(ex); // the other effects still run; reported after the flush
                    }
                }

                _running.Clear();
            }
        }
        finally
        {
            _flushing = false;
        }

        if (_flushErrors.Count > 0)
        {
            var errors = _flushErrors.ToArray();
            _flushErrors.Clear();
            Report(errors);
        }
    }

    private void Report(Exception[] errors)
    {
        Diagnostics.Errors += errors.Length;
        if (ErrorHandler is { } handler)
        {
            foreach (var e in errors)
            {
                handler(e);
            }

            return;
        }

        throw new ReactiveEffectException(errors);
    }
}

/// <summary>Counts what the reactive graph did, for tests, inspectors and benchmarks.</summary>
public sealed class ReactiveDiagnostics
{
    public int SignalWrites { get; internal set; }

    public int ComputedEvaluations { get; internal set; }

    public int EffectRuns { get; internal set; }

    public int Errors { get; internal set; }

    public int LoopsStopped { get; internal set; }

    public void Reset() => SignalWrites = ComputedEvaluations = EffectRuns = Errors = LoopsStopped = 0;

    public override string ToString() =>
        $"writes {SignalWrites}, computed {ComputedEvaluations}, effects {EffectRuns}, errors {Errors}, loops stopped {LoopsStopped}";
}

/// <summary>Thrown when effects failed during a flush and no <see cref="ReactiveRuntime.ErrorHandler"/> is set.</summary>
public sealed class ReactiveEffectException : Exception
{
    public ReactiveEffectException()
        : this("An effect failed.")
    {
    }

    public ReactiveEffectException(string message)
        : base(message) => Errors = [];

    public ReactiveEffectException(string message, Exception innerException)
        : base(message, innerException) => Errors = [innerException];

    public ReactiveEffectException(IReadOnlyList<Exception> errors)
        : base(Describe(errors), errors[0]) => Errors = errors;

    public IReadOnlyList<Exception> Errors { get; }

    private static string Describe(IReadOnlyList<Exception> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentOutOfRangeException.ThrowIfZero(errors.Count);
        return errors.Count == 1 ? "An effect failed: " + errors[0].Message : $"{errors.Count} effects failed; first: {errors[0].Message}";
    }
}

/// <summary>A computed value that reads itself, directly or through others.</summary>
public sealed class ReactiveCycleException : Exception
{
    public ReactiveCycleException()
    {
    }

    public ReactiveCycleException(string message)
        : base(message)
    {
    }

    public ReactiveCycleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Effects that kept triggering each other until the flush gave up.</summary>
public sealed class ReactiveLoopException : Exception
{
    public ReactiveLoopException()
    {
    }

    public ReactiveLoopException(string message)
        : base(message)
    {
    }

    public ReactiveLoopException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
