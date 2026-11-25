// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

namespace MNVTEngine.Reactivity;

/// <summary>A reactive value that can be read (and tracked) and read without tracking.</summary>
public interface IReadable<out T>
{
    /// <summary>The value; reading it inside a computed value or effect makes it a dependency.</summary>
    T Value { get; }

    /// <summary>The value without becoming a dependency.</summary>
    T Peek();
}

/// <summary>
/// A typed reactive value. Reading <see cref="Value"/> inside a computed value or effect records a dependency; writing
/// a value the comparer says differs marks dependents stale and, outside a batch, runs the effects that depend on it
/// before the setter returns. Writing an equal value does nothing. The default comparer is
/// <see cref="EqualityComparer{T}.Default"/>. Signals are not owned: they live as long as something references them.
/// </summary>
public sealed class Signal<T> : IReadable<T>
{
    private readonly SignalNode _node;
    private readonly IEqualityComparer<T> _comparer;
    private T _value;

    public Signal(T initial, IEqualityComparer<T>? comparer = null)
    {
        _node = new SignalNode(ReactiveRuntime.Current);
        _comparer = comparer ?? EqualityComparer<T>.Default;
        _value = initial;
    }

    /// <summary>An optional name shown in diagnostics.</summary>
    public string? Name { get; set; }

    public T Value
    {
        get
        {
            _node.Runtime.CheckThread();
            _node.Runtime.Track(_node);
            return _value;
        }

        set => Set(value);
    }

    /// <summary>How many observers currently depend on this signal (diagnostics and leak tests).</summary>
    public int ObserverCount => _node.Observers.Count;

    public T Peek() => _value;

    /// <summary>Writes a value; returns whether it differed (and dependents were notified).</summary>
    public bool Set(T value)
    {
        var rt = _node.Runtime;
        rt.CheckThread();
        if (_comparer.Equals(_value, value))
        {
            return false;
        }

        _value = value;
        rt.Diagnostics.SignalWrites++;
        _node.NotifyObservers(NodeState.Dirty);
        rt.FlushIfIdle();
        return true;
    }

    public bool Update(Func<T, T> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return Set(change(_value));
    }

    /// <summary>Notifies dependents although the value is the same object (a list changed in place).</summary>
    public void NotifyChanged()
    {
        var rt = _node.Runtime;
        rt.CheckThread();
        rt.Diagnostics.SignalWrites++;
        _node.NotifyObservers(NodeState.Dirty);
        rt.FlushIfIdle();
    }

    public override string ToString() => $"Signal({Name ?? typeof(T).Name}: {_value})";

    private sealed class SignalNode(ReactiveRuntime runtime) : ReactiveNode(runtime);
}

/// <summary>Factory helpers so call sites read <c>Signal.Create("")</c>.</summary>
public static class Signal
{
    public static Signal<T> Create<T>(T initial, IEqualityComparer<T>? comparer = null) => new(initial, comparer);
}
