// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using System.Globalization;
using System.Reflection;
using MNVTEngine.Reactivity;
using MNVTEngine.UI.Widgets;

namespace MNVTEngine.UI;

/// <summary>
/// Named values for layout documents. An element's <c>bind</c> block in a <c>.mnvtui</c> file maps its properties to
/// names (<c>"bind": { "text": "playerName", "visible": "menuOpen" }</c>); game code provides the names here:
/// <code>
/// var data = GetComponent&lt;UIDocument&gt;().Data;
/// data.Set("playerName", name);                 // Signal&lt;string&gt;: two-way for text fields
/// data.Set("menuOpen", () => !paused.Value);    // computed, one-way
/// </code>
/// Names can be set before or after the layout loads, and replaced later; a property whose name is not set keeps its
/// authored value. Values are converted to the property's type (numbers, text, booleans, enums). A writable
/// <see cref="Signal{T}"/> bound to the value of a text field, checkbox, toggle, slider or dropdown also receives the
/// user's edits.
/// </summary>
public sealed class UIData
{
    private readonly Dictionary<string, Source> _sources = new(StringComparer.Ordinal);
    private readonly Signal<int> _version = Signal.Create(0);
    private readonly HashSet<UIElement> _bound = [];

    public IReadOnlyCollection<string> Names => _sources.Keys;

    /// <summary>A signal: read by bound properties, and written by edits of bound inputs.</summary>
    public UIData Set<T>(string name, Signal<T> signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return Add(name, new Source(() => signal.Value, typeof(T), value => signal.Value = (T)value!));
    }

    /// <summary>A read-only reactive value.</summary>
    public UIData Set<T>(string name, IReadable<T> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Add(name, new Source(() => value.Value, typeof(T), null));
    }

    /// <summary>A computed value: bound properties follow the signals it reads.</summary>
    public UIData Set<T>(string name, Func<T> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Add(name, new Source(() => value(), typeof(T), null));
    }

    public bool Remove(string name)
    {
        if (!_sources.Remove(name))
        {
            return false;
        }

        _version.Value++;
        return true;
    }

    /// <summary>
    /// Binds the <see cref="UIElement.DataBindings"/> of <paramref name="root"/> and its descendants (once per element;
    /// <see cref="UIDocument"/> does this for its layout). Bindings stop when the elements are disposed.
    /// </summary>
    /// <exception cref="InvalidOperationException">An element binds a property that does not exist.</exception>
    public void Apply(UIElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (var element in root.Descendants())
        {
            if (element.HasDataBindings && _bound.Add(element))
            {
                element.OnCleanup(() => _bound.Remove(element));
                foreach (var (property, name) in element.DataBindings)
                {
                    var info = UILayoutSerializer.BindableProperty(element.GetType(), property)
                        ?? throw new InvalidOperationException($"{element.GetType().Name} '{element.Name}' binds '{property}', which is not a settable property.");
                    Bind(element, info, name);
                }
            }
        }
    }

    private UIData Add(string name, Source source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _sources[name] = source;
        _version.Value++;
        return this;
    }

    private void Bind(UIElement element, PropertyInfo info, string name)
    {
        element.Effect(
            () =>
            {
                _ = _version.Value;
                if (!_sources.TryGetValue(name, out var source))
                {
                    return;
                }

                var value = Convert(source.Read(), info.PropertyType, name);
                Reactive.Untrack(() =>
                {
                    if (!Equals(info.GetValue(element), value))
                    {
                        info.SetValue(element, value);
                    }
                });
            },
            "bind " + name);

        void Write(object? edited)
        {
            if (_sources.TryGetValue(name, out var source) && source.Write is { } write)
            {
                write(Convert(edited, source.Type, name));
            }
        }

        switch (element)
        {
            case TextInputBase field when info.Name == nameof(TextInputBase.Text):
                field.TextChanged += t => Write(t);
                break;
            case Checkbox box when info.Name == nameof(Checkbox.Checked):
                box.CheckedChanged += v => Write(v);
                break;
            case Slider slider when info.Name == nameof(Slider.Value):
                slider.ValueChanged += v => Write(v);
                break;
            case Dropdown dropdown when info.Name == nameof(Dropdown.SelectedIndex):
                dropdown.SelectionChanged += v => Write(v);
                break;
        }
    }

    private static object? Convert(object? value, Type target, string name)
    {
        var type = Nullable.GetUnderlyingType(target) ?? target;
        if (value is null)
        {
            return type == typeof(string) ? "" : !target.IsValueType || type != target ? null : Activator.CreateInstance(target);
        }

        if (type.IsInstanceOfType(value))
        {
            return value;
        }

        try
        {
            return type == typeof(string) ? System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
                : type.IsEnum ? Enum.Parse(type, value.ToString()!, ignoreCase: true)
                : System.Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidOperationException($"UI data '{name}': {value.GetType().Name} '{value}' cannot be converted to {type.Name}.", ex);
        }
    }

    private sealed record Source(Func<object?> Read, Type Type, Action<object?>? Write);
}
