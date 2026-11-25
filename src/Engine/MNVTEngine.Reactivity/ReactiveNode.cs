// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

namespace MNVTEngine.Reactivity;

internal enum NodeState : byte
{
    Clean = 0,

    /// <summary>A source further up may have changed: check the sources before deciding.</summary>
    Check = 1,

    /// <summary>A direct source changed: recompute.</summary>
    Dirty = 2,
}

/// <summary>
/// Owns what was created while it was current: child owners (effects, computed values, scopes) and cleanup callbacks.
/// Disposing an owner disposes all of it, which is how a removed element's bindings stop.
/// </summary>
public abstract class ReactiveOwner : IDisposable
{
    private List<ReactiveOwner>? _children;
    private List<Action>? _cleanups;

    internal ReactiveOwner(ReactiveRuntime runtime, ReactiveOwner? parent)
    {
        Runtime = runtime;
        if (parent is { Disposed: false })
        {
            Parent = parent;
            (parent._children ??= []).Add(this);
        }
    }

    public bool Disposed { get; private set; }

    internal ReactiveRuntime Runtime { get; }

    internal ReactiveOwner? Parent { get; set; }

    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Runtime.CheckThread();
        Disposed = true;
        OnDispose();
        DisposeOwned();
        if (Parent is not null)
        {
            Parent._children?.Remove(this);
            Parent = null;
        }

        GC.SuppressFinalize(this);
    }

    internal void AddCleanup(Action cleanup) => (_cleanups ??= []).Add(cleanup);

    /// <summary>Disposes children and runs cleanups (newest first), keeping this owner usable.</summary>
    internal void DisposeOwned()
    {
        if (_children is { Count: > 0 })
        {
            var owned = _children.ToArray();
            _children.Clear();
            for (var i = owned.Length - 1; i >= 0; i--)
            {
                owned[i].Parent = null;
                owned[i].Dispose();
            }
        }

        if (_cleanups is { Count: > 0 })
        {
            var pending = _cleanups.ToArray();
            _cleanups.Clear();
            List<Exception>? errors = null;
            for (var i = pending.Length - 1; i >= 0; i--)
            {
                try
                {
                    pending[i]();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Every cleanup runs even when one fails; the failures are reported below.
                    (errors ??= []).Add(ex);
                }
            }

            if (errors is not null)
            {
                Runtime.ReportImmediate(errors.Count == 1 ? errors[0] : new AggregateException(errors));
            }
        }
    }

    internal virtual void OnDispose()
    {
    }
}

/// <summary>A plain owner: what is created while it runs is disposed with it.</summary>
public sealed class ReactiveScope : ReactiveOwner
{
    internal ReactiveScope(ReactiveRuntime runtime, ReactiveOwner? parent)
        : base(runtime, parent)
    {
    }

    /// <summary>Runs <paramref name="action"/> with this scope owning what it creates (untracked).</summary>
    public void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Runtime.CheckThread();
        ObjectDisposedException.ThrowIf(Disposed, this);
        var savedOwner = Runtime.CurrentOwner;
        var savedObserver = Runtime.CurrentObserver;
        Runtime.CurrentOwner = this;
        Runtime.CurrentObserver = null;
        try
        {
            action();
        }
        finally
        {
            Runtime.CurrentOwner = savedOwner;
            Runtime.CurrentObserver = savedObserver;
        }
    }

    public T Run<T>(Func<T> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        T result = default!;
        Run(() => { result = build(); }); // a block body: an expression lambda would pick this overload again
        return result;
    }

    /// <summary>Registers a cleanup that runs when this scope is disposed.</summary>
    public void OnCleanup(Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        AddCleanup(cleanup);
    }
}

/// <summary>Anything that can be read reactively: a signal, a computed value or an observer's source.</summary>
internal abstract class ReactiveNode(ReactiveRuntime runtime)
{
    // Deduplicates reads within one observer run.
    internal ObserverNode? LastReader;
    internal int LastReadStamp;

    // Scratch for reconciling an observer's sources.
    internal int Mark;

    internal ReactiveRuntime Runtime { get; } = runtime;

    /// <summary>Observers that read this node in their last run.</summary>
    internal List<ObserverNode> Observers { get; } = [];

    /// <summary>
    /// Removes an observer, searching from the end: teardown disposes newest first, so the observer is usually last,
    /// and a signal shared by many bindings stays cheap to unsubscribe.
    /// </summary>
    internal void RemoveObserver(ObserverNode observer)
    {
        var list = Observers;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i] != observer)
            {
                continue;
            }

            var last = list.Count - 1;
            if (i != last)
            {
                list[i] = list[last]; // order among observers carries no meaning here
            }

            list.RemoveAt(last);
            return;
        }
    }

    /// <summary>Marks observers stale after this node's value changed.</summary>
    internal void NotifyObservers(NodeState state)
    {
        var list = Observers;
        for (var i = 0; i < list.Count; i++)
        {
            list[i].Stale(state);
        }
    }
}

/// <summary>A node that reads others: a computed value or an effect. It is also an owner.</summary>
internal abstract class ObserverNode(ReactiveRuntime runtime, ReactiveOwner? parent) : ReactiveOwner(runtime, parent)
{
    internal NodeState State = NodeState.Dirty;
    internal List<ReactiveNode> Sources = new(4);
    internal List<ReactiveNode> Tracking = [];
    internal int RunStamp;
    internal string? Name;

    internal abstract bool IsEffect { get; }

    /// <summary>Marks this observer at least <paramref name="state"/>; the first mark spreads Check downstream.</summary>
    internal void Stale(NodeState state)
    {
        if (State >= state || Disposed)
        {
            return;
        }

        var wasClean = State == NodeState.Clean;
        State = state;
        if (!wasClean)
        {
            return;
        }

        if (IsEffect)
        {
            Runtime.Enqueue((EffectNode)this);
        }
        else
        {
            ((ComputedNodeBase)this).Node.NotifyObservers(NodeState.Check);
        }
    }

    /// <summary>Brings this observer up to date: checks Check sources, recomputes if any changed.</summary>
    internal void UpdateIfNecessary()
    {
        if (State == NodeState.Check)
        {
            for (var i = 0; i < Sources.Count; i++)
            {
                if (Sources[i] is ComputedSource source)
                {
                    source.Owner.UpdateIfNecessary();
                }

                if (State == NodeState.Dirty)
                {
                    break;
                }
            }
        }

        // Update marks the node Clean before running, so a write it makes to its own sources during the run leaves it
        // Dirty (and queued) instead of being forgotten.
        if (State == NodeState.Dirty)
        {
            Update();
        }
        else
        {
            State = NodeState.Clean;
        }
    }

    internal override void OnDispose()
    {
        for (var i = 0; i < Sources.Count; i++)
        {
            Sources[i].RemoveObserver(this);
        }

        Sources.Clear();
    }

    protected abstract void Update();

    /// <summary>
    /// Starts a tracked run: disposes what the previous run owned, then makes this the current observer and owner.
    /// Always pair with <see cref="EndRun"/> in a finally block (a begin/end pair rather than a delegate, so a
    /// recompute allocates no closure).
    /// </summary>
    protected RunFrame BeginRun()
    {
        DisposeOwned();
        var rt = Runtime;
        var frame = new RunFrame(rt.CurrentObserver, rt.CurrentOwner, Tracking);
        Tracking = rt.RentTrackBuffer();
        RunStamp = rt.NextStamp();
        rt.CurrentObserver = this;
        rt.CurrentOwner = this;
        State = NodeState.Clean; // a write to one of our own sources during the run marks us again
        return frame;
    }

    protected void EndRun(in RunFrame frame)
    {
        var rt = Runtime;
        rt.CurrentObserver = frame.Observer;
        rt.CurrentOwner = frame.Owner;
        ReconcileSources();
        rt.ReturnTrackBuffer(Tracking);
        Tracking = frame.Tracking;
    }

    /// <summary>Subscribes to sources read this run and unsubscribes from those no longer read.</summary>
    private void ReconcileSources()
    {
        var stamp = Runtime.NextStamp();
        var now = Tracking;
        for (var i = 0; i < now.Count; i++)
        {
            now[i].Mark = stamp;
        }

        for (var i = 0; i < Sources.Count; i++)
        {
            var old = Sources[i];
            if (old.Mark == stamp)
            {
                old.Mark = -stamp; // still read, already subscribed
            }
            else
            {
                old.RemoveObserver(this);
            }
        }

        for (var i = 0; i < now.Count; i++)
        {
            if (now[i].Mark != -stamp)
            {
                now[i].Observers.Add(this);
            }
        }

        Sources.Clear();
        Sources.AddRange(now);
    }

    protected readonly record struct RunFrame(ObserverNode? Observer, ReactiveOwner? Owner, List<ReactiveNode> Tracking);
}

/// <summary>The source side of a computed value (computed values are both observer and source).</summary>
internal sealed class ComputedSource(ReactiveRuntime runtime, ComputedNodeBase owner) : ReactiveNode(runtime)
{
    internal ComputedNodeBase Owner { get; } = owner;
}

internal abstract class ComputedNodeBase : ObserverNode
{
    protected ComputedNodeBase(ReactiveRuntime runtime, ReactiveOwner? parent)
        : base(runtime, parent) => Node = new ComputedSource(runtime, this);

    internal ComputedSource Node { get; }

    internal override bool IsEffect => false;
}
