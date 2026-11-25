// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using MNVTEngine.Reactivity;

namespace MNVTEngine.UI;

/// <summary>
/// Bindings connect reactive values to element properties. A binding is an effect owned by the element's
/// <see cref="UIElement.Scope"/>: it reads its source tracked, applies the value untracked (so what the setter touches
/// never becomes a dependency), reruns only when the source changes, and stops when the element is disposed. A binding
/// allocates once, when it is made; reruns allocate nothing.
/// </summary>
public static class Bindings
{
    /// <summary>Applies <paramref name="read"/>'s value to <paramref name="element"/> now and whenever it changes.</summary>
    public static T Bind<T, TValue>(this T element, Func<TValue> read, Action<T, TValue> apply, string? name = null)
        where T : UIElement
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(apply);
        var binding = new Binding<T, TValue>(element, read, apply);
        Reactivity.Effect.CreateOwned(element.Scope, binding.Run, name ?? element.Name);
        return element;
    }

    /// <summary>Runs <paramref name="body"/> as an effect owned by the element (for effects that are not one property).</summary>
    public static T Effect<T>(this T element, Action body, string? name = null)
        where T : UIElement
    {
        ArgumentNullException.ThrowIfNull(element);
        Reactivity.Effect.CreateOwned(element.Scope, body, name ?? element.Name);
        return element;
    }

    private sealed class Binding<T, TValue>
        where T : UIElement
    {
        private readonly T _element;
        private readonly Func<TValue> _read;
        private readonly Action<T, TValue> _apply;
        private readonly Action _applyCurrent;
        private TValue _value = default!;

        internal Binding(T element, Func<TValue> read, Action<T, TValue> apply)
        {
            (_element, _read, _apply) = (element, read, apply);
            _applyCurrent = ApplyCurrent; // cached so a rerun allocates nothing
        }

        internal void Run()
        {
            _value = _read();
            Reactive.Untrack(_applyCurrent);
        }

        private void ApplyCurrent() => _apply(_element, _value);
    }
}
