// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using System.Collections.Concurrent;

namespace MNVTEngine;

/// <summary>
/// A scene's game-thread synchronization context. It is current while the scene runs code (lifecycle callbacks,
/// UI events, physics callbacks), so an <c>await</c> in game code resumes on the game thread: the continuation is
/// queued from whatever thread finished the work and runs at the start of the scene's next <see cref="Scene.Update"/>.
/// <see cref="Scene.Post"/> uses the same queue. Once a scene hands its work to the next one (persistent objects moving
/// on), later posts to it are forwarded there too: whatever captured the context (a socket's events, a timer, a
/// background loop of a DontDestroyOnLoad object) keeps reaching the game thread instead of a queue nobody runs.
/// </summary>
internal sealed class SceneSynchronizationContext : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
    private volatile SceneSynchronizationContext? _forward;

    public int Pending => _queue.Count;

    /// <summary>Queues work for the next update; safe from any thread.</summary>
    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        var target = this;
        while (target._forward is { } next)
        {
            target = next;
        }

        target._queue.Enqueue((d, state));
    }

    // Send keeps the base behaviour (runs on the calling thread): blocking another thread on the game loop deadlocks
    // as soon as the game thread waits for that thread.
    public override SynchronizationContext CreateCopy() => this;

    public bool TryTake(out (SendOrPostCallback Callback, object? State) work) => _queue.TryDequeue(out work);

    /// <summary>Hands the pending work to another scene (DontDestroyOnLoad: the objects that awaited move there).</summary>
    public void MoveTo(SceneSynchronizationContext target)
    {
        if (target == this)
        {
            return;
        }

        target._forward = null; // the scene taking over runs its own queue: no cycles
        _forward = target;
        while (_queue.TryDequeue(out var work))
        {
            target._queue.Enqueue(work);
        }
    }

    /// <summary>The scene runs again (started after an unload): its posts are its own once more.</summary>
    public void Resume() => _forward = null;

    /// <summary>Drops pending work: the scene unloaded, and continuations must not keep game code (hot reload) alive.</summary>
    public void Clear() => _queue.Clear();
}
