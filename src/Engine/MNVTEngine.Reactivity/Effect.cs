// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

namespace MNVTEngine.Reactivity;

/// <summary>
/// A reactive side effect. It runs once when created, tracking what it reads, and again whenever one of those
/// dependencies really changes: immediately after the write, or once at the end of the batch the write happened in.
/// Before each rerun (and when disposed) it disposes what its previous run created and runs the cleanups registered
/// with <see cref="Reactive.OnCleanup"/>. Queued effects run in the order they were invalidated; an effect queued while
/// the queue runs runs in the next round, and endless re-triggering stops after
/// <see cref="ReactiveRuntime.MaxFlushRounds"/> rounds with a <see cref="ReactiveLoopException"/>. A disposed effect
/// never runs again.
/// </summary>
public sealed class Effect : IDisposable
{
    private readonly EffectNode _node;

    private Effect(EffectNode node) => _node = node;

    public bool Disposed => _node.Disposed;

    /// <summary>How many times the body has run.</summary>
    public int RunCount => _node.Runs;

    /// <summary>How many sources the last run read.</summary>
    public int SourceCount => _node.Sources.Count;

    /// <summary>Creates and runs an effect owned by the current scope (or effect).</summary>
    public static Effect Create(Action body, string? name = null) => CreateOwned(ReactiveRuntime.Current.CurrentOwner, body, name);

    /// <summary>Creates and runs an effect owned by <paramref name="owner"/> (null: unowned).</summary>
    public static Effect CreateOwned(ReactiveOwner? owner, Action body, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        var rt = ReactiveRuntime.Current;
        rt.CheckThread();
        var node = new EffectNode(rt, owner, body) { Name = name };
        var effect = new Effect(node);

        // The first run is a batch: what it writes runs after it has subscribed to its sources.
        rt.BeginBatch();
        try
        {
            node.UpdateIfNecessary();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            rt.ReportImmediate(ex);
        }
        finally
        {
            rt.EndBatch();
        }

        return effect;
    }

    public void Dispose() => _node.Dispose();
}

internal sealed class EffectNode(ReactiveRuntime runtime, ReactiveOwner? parent, Action body) : ObserverNode(runtime, parent)
{
    internal bool Queued;
    internal int Runs;

    internal override bool IsEffect => true;

    protected override void Update()
    {
        Runs++;
        Runtime.Diagnostics.EffectRuns++;
        var frame = BeginRun();
        try
        {
            body();
        }
        finally
        {
            EndRun(frame);
        }
    }
}
