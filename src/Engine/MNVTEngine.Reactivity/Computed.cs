// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using System.Runtime.ExceptionServices;

namespace MNVTEngine.Reactivity;

/// <summary>
/// A cached value derived from other reactive values. It recomputes lazily: only when it is read after one of its
/// sources really changed. Its dependencies are whatever the last evaluation read, so a branch that is no longer taken
/// stops being a dependency. If it recomputes to a value equal to the previous one, its own dependents are not rerun.
/// An exception thrown by the function is cached and rethrown to every reader until a source changes. Owned by the
/// scope or effect it was created in.
/// </summary>
public sealed class Computed<T> : IReadable<T>, IDisposable
{
    private readonly Node _node;

    public Computed(Func<T> compute, IEqualityComparer<T>? comparer = null, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(compute);
        var rt = ReactiveRuntime.Current;
        rt.CheckThread();
        _node = new Node(rt, rt.CurrentOwner, compute, comparer ?? EqualityComparer<T>.Default) { Name = name };
    }

    public T Value
    {
        get
        {
            var rt = _node.Runtime;
            rt.CheckThread();
            rt.Track(_node.Node);
            return _node.Read();
        }
    }

    public bool Disposed => _node.Disposed;

    /// <summary>How many observers currently depend on this value (diagnostics and leak tests).</summary>
    public int ObserverCount => _node.Node.Observers.Count;

    /// <summary>How many sources the last evaluation read.</summary>
    public int SourceCount => _node.Sources.Count;

    public T Peek()
    {
        _node.Runtime.CheckThread();
        return _node.Read();
    }

    public void Dispose() => _node.Dispose();

    public override string ToString() => $"Computed({_node.Name ?? typeof(T).Name})";

    private sealed class Node(ReactiveRuntime runtime, ReactiveOwner? parent, Func<T> compute, IEqualityComparer<T> comparer)
        : ComputedNodeBase(runtime, parent)
    {
        private T _value = default!;
        private ExceptionDispatchInfo? _error;
        private bool _hasValue;
        private bool _computing;

        internal T Read()
        {
            if (_computing)
            {
                throw new ReactiveCycleException($"Computed value '{Name ?? typeof(T).Name}' depends on itself.");
            }

            if (!Disposed)
            {
                UpdateIfNecessary();
            }

            _error?.Throw();
            return _value;
        }

        protected override void Update()
        {
            var previous = _value;
            bool hadValue = _hasValue, hadError = _error is not null;
            T next = default!;
            ExceptionDispatchInfo? error = null;
            _computing = true;
            Runtime.Diagnostics.ComputedEvaluations++;
            var frame = BeginRun();
            try
            {
                next = compute();
            }
            catch (ReactiveCycleException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                error = ExceptionDispatchInfo.Capture(ex); // cached: every reader gets it until a source changes
            }
            finally
            {
                EndRun(frame);
                _computing = false;
            }

            _error = error;
            bool changed;
            if (error is not null)
            {
                changed = true;
            }
            else
            {
                _value = next;
                _hasValue = true;
                changed = !hadValue || hadError || !comparer.Equals(previous, next);
            }

            if (!changed)
            {
                return;
            }

            // Direct dependents were marked Check by the write that reached us; promote them. A Clean dependent is the
            // observer reading us right now (it gets the new value), so it is left alone: marking it would run it twice.
            var observers = Node.Observers;
            for (var i = 0; i < observers.Count; i++)
            {
                if (observers[i].State == NodeState.Check)
                {
                    observers[i].State = NodeState.Dirty;
                }
            }
        }
    }
}

/// <summary>Factory helpers so call sites read <c>Computed.Create(() => ...)</c>.</summary>
public static class Computed
{
    public static Computed<T> Create<T>(Func<T> compute, IEqualityComparer<T>? comparer = null, string? name = null) =>
        new(compute, comparer, name);
}
