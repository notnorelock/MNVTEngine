// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using System.Collections.Concurrent;

namespace MNVTEngine.Reactivity;

/// <summary>Batching, untracked reads, cleanups and scopes on the calling thread's runtime.</summary>
public static class Reactive
{
    /// <summary>The owner new nodes are attached to right now (null: none).</summary>
    public static ReactiveOwner? CurrentOwner => ReactiveRuntime.Current.CurrentOwner;

    public static ReactiveDiagnostics Diagnostics => ReactiveRuntime.Current.Diagnostics;

    /// <summary>
    /// Runs <paramref name="action"/> with effects deferred to its end: dependents of several writes run once, after
    /// the last write. Batches nest; the outermost one flushes.
    /// </summary>
    public static void Batch(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var rt = ReactiveRuntime.Current;
        rt.CheckThread();
        rt.BeginBatch();
        try
        {
            action();
        }
        finally
        {
            rt.EndBatch();
        }
    }

    /// <summary>Reads without recording dependencies.</summary>
    public static T Untrack<T>(Func<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var rt = ReactiveRuntime.Current;
        var saved = rt.CurrentObserver;
        rt.CurrentObserver = null;
        try
        {
            return read();
        }
        finally
        {
            rt.CurrentObserver = saved;
        }
    }

    /// <summary>Runs <paramref name="action"/> without recording dependencies.</summary>
    public static void Untrack(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var rt = ReactiveRuntime.Current;
        var saved = rt.CurrentObserver;
        rt.CurrentObserver = null;
        try
        {
            action();
        }
        finally
        {
            rt.CurrentObserver = saved;
        }
    }

    /// <summary>
    /// Registers a cleanup with the current owner: inside an effect it runs before the next run and on disposal;
    /// inside a scope when the scope is disposed. Returns false (and does nothing) with no owner.
    /// </summary>
    public static bool OnCleanup(Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        var rt = ReactiveRuntime.Current;
        rt.CheckThread();
        if (rt.CurrentOwner is null)
        {
            return false;
        }

        rt.CurrentOwner.AddCleanup(cleanup);
        return true;
    }

    /// <summary>A new scope, owned by the current owner unless <paramref name="detached"/>.</summary>
    public static ReactiveScope CreateScope(bool detached = false)
    {
        var rt = ReactiveRuntime.Current;
        rt.CheckThread();
        return new ReactiveScope(rt, detached ? null : rt.CurrentOwner);
    }
}

/// <summary>
/// Brings work from other threads (network callbacks, tasks) onto the reactive thread. Anything may
/// <see cref="Post"/>; the owner (a UI context) drains the queue once per frame, before layout and rendering, and the
/// posted actions run inside one batch.
/// </summary>
public sealed class ReactiveDispatcher
{
    private readonly ConcurrentQueue<Action> _queue = new();

    public int Pending => _queue.Count;

    /// <summary>Queues <paramref name="action"/> to run on the reactive thread. Thread-safe.</summary>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _queue.Enqueue(action);
    }

    /// <summary>
    /// Runs up to <paramref name="max"/> queued actions on the calling thread in one batch. Actions posted while draining
    /// wait for the next drain, so a self-reposting action cannot stall a frame. Returns how many ran.
    /// </summary>
    public int Drain(int max = int.MaxValue)
    {
        // Checked first: the batch below captures locals, and the closure is allocated on entry to the method that
        // declares it, so an empty queue must not reach it.
        return _queue.IsEmpty || max <= 0 ? 0 : DrainQueued(max);
    }

    private int DrainQueued(int max)
    {
        var budget = Math.Min(max, _queue.Count);
        var ran = 0;
        Reactive.Batch(() =>
        {
            while (ran < budget && _queue.TryDequeue(out var action))
            {
                ran++;
                action();
            }
        });
        return ran;
    }
}
