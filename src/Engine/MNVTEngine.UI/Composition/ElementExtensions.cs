// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

using MNVTEngine.Graphics.Text;
using MNVTEngine.Mathematics;
using MNVTEngine.Reactivity;
using MNVTEngine.UI.Widgets;

namespace MNVTEngine.UI;

/// <summary>
/// Fluent configuration for elements built with <see cref="V"/> (or <c>new</c>). Each method returns the element with
/// its own type. Overloads taking a <see cref="Func{TResult}"/> or an <see cref="IReadable{T}"/> are bindings: they
/// follow the reactive values they read until the element is disposed.
/// </summary>
public static partial class ElementExtensions
{
    // Tree.

    public static T Children<T>(this T e, params UIElement?[] children)
        where T : UIElement
    {
        ArgumentNullException.ThrowIfNull(e);
        foreach (var child in children)
        {
            if (child is not null)
            {
                e.AddChild(child);
            }
        }

        return e;
    }

    public static T Child<T>(this T e, UIElement child)
        where T : UIElement => e.Children(child);

    public static T Named<T>(this T e, string name)
        where T : UIElement => Set(e, x => x.Name = name);

    public static T Class<T>(this T e, string styleClass)
        where T : UIElement => Set(e, x => x.StyleClass = string.IsNullOrEmpty(x.StyleClass) ? styleClass : x.StyleClass + " " + styleClass);

    /// <summary>Adds or removes a style class while <paramref name="when"/> is true.</summary>
    public static T Class<T>(this T e, string styleClass, Func<bool> when)
        where T : UIElement => e.Bind(when, (x, on) => x.SetClass(styleClass, on));

    // Layout.

    public static T Layout<T>(this T e, LayoutMode mode)
        where T : UIElement => Set(e, x => x.Layout = mode);

    public static T Width<T>(this T e, UIUnit width)
        where T : UIElement => Set(e, x => x.Width = width);

    public static T Height<T>(this T e, UIUnit height)
        where T : UIElement => Set(e, x => x.Height = height);

    public static T Size<T>(this T e, UIUnit width, UIUnit height)
        where T : UIElement => Set(e, x => (x.Width, x.Height) = (width, height));

    public static T MinWidth<T>(this T e, float value)
        where T : UIElement => Set(e, x => x.MinWidth = value);

    public static T MaxWidth<T>(this T e, float value)
        where T : UIElement => Set(e, x => x.MaxWidth = value);

    public static T MinHeight<T>(this T e, float value)
        where T : UIElement => Set(e, x => x.MinHeight = value);

    public static T MaxHeight<T>(this T e, float value)
        where T : UIElement => Set(e, x => x.MaxHeight = value);

    /// <summary>Fills the parent in both directions.</summary>
    public static T Fill<T>(this T e)
        where T : UIElement => Set(e, x => (x.Width, x.Height) = (UIUnit.Percent(100), UIUnit.Percent(100)));

    public static T FillWidth<T>(this T e)
        where T : UIElement => Set(e, x => x.Width = UIUnit.Percent(100));

    public static T FillHeight<T>(this T e)
        where T : UIElement => Set(e, x => x.Height = UIUnit.Percent(100));

    /// <summary>Takes a share of a row's or column's free space (like CSS <c>flex: 1</c>).</summary>
    public static T Grow<T>(this T e, float weight = 1f)
        where T : UIElement => Set(e, x => x.FlexGrow = weight);

    public static T Padding<T>(this T e, float all)
        where T : UIElement => Set(e, x => x.Padding = Thickness.All(all));

    public static T Padding<T>(this T e, float horizontal, float vertical)
        where T : UIElement => Set(e, x => x.Padding = Thickness.Symmetric(horizontal, vertical));

    public static T Padding<T>(this T e, float left, float top, float right, float bottom)
        where T : UIElement => Set(e, x => x.Padding = new Thickness(left, top, right, bottom));

    public static T Margin<T>(this T e, float all)
        where T : UIElement => Set(e, x => x.Margin = Thickness.All(all));

    public static T Margin<T>(this T e, float left, float top, float right, float bottom)
        where T : UIElement => Set(e, x => x.Margin = new Thickness(left, top, right, bottom));

    public static T Gap<T>(this T e, float gap)
        where T : UIElement => Set(e, x => x.Gap = gap);

    public static T AlignItems<T>(this T e, Align align)
        where T : UIElement => Set(e, x => x.AlignItems = align);

    public static T Justify<T>(this T e, Justify justify)
        where T : UIElement => Set(e, x => x.Justify = justify);

    public static T AlignSelf<T>(this T e, Align align)
        where T : UIElement => Set(e, x => x.AlignSelf = align);

    /// <summary>Centres the children on both axes.</summary>
    public static T Center<T>(this T e)
        where T : UIElement => Set(e, x => (x.AlignItems, x.Justify) = (global::MNVTEngine.UI.Align.Center, global::MNVTEngine.UI.Justify.Center));

    /// <summary>Places the element in an absolute-layout parent: <paramref name="anchor"/> of the parent, own <paramref name="pivot"/>.</summary>
    public static T Anchor<T>(this T e, Vector2 anchor, Vector2? pivot = null, float left = 0f, float top = 0f)
        where T : UIElement => Set(e, x => (x.Anchor, x.Pivot, x.Left, x.Top) = (anchor, pivot ?? anchor, left, top));

    /// <summary>A nine-sliced texture background (<see cref="UIElement.BackgroundAsset"/>): frame in texels, drawn at <paramref name="scale"/> pixels per texel.</summary>
    public static T BackgroundImage<T>(this T e, AssetRef texture, Thickness slice, float scale = 1f)
        where T : UIElement => Set(e, x => (x.BackgroundAsset, x.BackgroundSlice, x.BackgroundScale) = (texture, slice, scale));

    public static T Clip<T>(this T e, bool clip = true)
        where T : UIElement => Set(e, x => x.Overflow = clip ? Overflow.Clip : Overflow.Visible);

    public static T ZIndex<T>(this T e, int z)
        where T : UIElement => Set(e, x => x.ZIndex = z);

    // State.

    public static T Visible<T>(this T e, bool visible)
        where T : UIElement => Set(e, x => x.Visible = visible);

    public static T Visible<T>(this T e, Func<bool> visible)
        where T : UIElement => e.Bind(visible, (x, v) => x.Visible = v);

    public static T Visible<T>(this T e, IReadable<bool> visible)
        where T : UIElement => e.Bind(() => visible.Value, (x, v) => x.Visible = v);

    public static T Enabled<T>(this T e, bool enabled)
        where T : UIElement => Set(e, x => x.Enabled = enabled);

    public static T Enabled<T>(this T e, Func<bool> enabled)
        where T : UIElement => e.Bind(enabled, (x, v) => x.Enabled = v);

    public static T Enabled<T>(this T e, IReadable<bool> enabled)
        where T : UIElement => e.Bind(() => enabled.Value, (x, v) => x.Enabled = v);

    public static T Tooltip<T>(this T e, string tooltip)
        where T : UIElement => Set(e, x => x.Tooltip = tooltip);

    public static T Focusable<T>(this T e, bool focusable = true)
        where T : UIElement => Set(e, x => x.Focusable = focusable);

    /// <summary>Whether the element takes the pointer (null decides by type: interactive widgets and filled boxes do).</summary>
    public static T HitTest<T>(this T e, bool? takesPointer)
        where T : UIElement => Set(e, x => x.HitTestVisible = takesPointer);

    // Visuals.

    public static T Background<T>(this T e, Color color)
        where T : UIElement => Set(e, x => x.BackgroundColor = color);

    public static T Background<T>(this T e, Func<Color> color)
        where T : UIElement => e.Bind(color, (x, v) => x.BackgroundColor = v);

    /// <summary>A background from the design tokens, following token changes.</summary>
    public static T Background<T>(this T e, Func<DesignTokens, Color> color)
        where T : UIElement => e.Bind(() => color(Design.Tokens), (x, v) => x.BackgroundColor = v);

    /// <summary>A gradient background; null goes back to the plain one.</summary>
    public static T Background<T>(this T e, Gradient? gradient)
        where T : UIElement => Set(e, x => x.BackgroundGradient = gradient);

    /// <summary>A gradient background from the design tokens, following token changes.</summary>
    public static T Background<T>(this T e, Func<DesignTokens, Gradient?> gradient)
        where T : UIElement => e.Bind(() => gradient(Design.Tokens), (x, v) => x.BackgroundGradient = v);

    public static T Border<T>(this T e, Color color, float width = 1f)
        where T : UIElement => Set(e, x => (x.BorderColor, x.BorderWidth) = (color, width));

    public static T Radius<T>(this T e, float radius)
        where T : UIElement => Set(e, x => x.CornerRadius = radius);

    public static T Opacity<T>(this T e, float opacity)
        where T : UIElement => Set(e, x => x.Opacity = opacity);

    public static T Opacity<T>(this T e, Func<float> opacity)
        where T : UIElement => e.Bind(opacity, (x, v) => x.Opacity = v);

    public static T Foreground<T>(this T e, Color color)
        where T : UIElement => Set(e, x => x.TextColor = color);

    public static T Foreground<T>(this T e, Func<Color> color)
        where T : UIElement => e.Bind(color, (x, v) => x.TextColor = v);

    public static T Foreground<T>(this T e, Func<DesignTokens, Color> color)
        where T : UIElement => e.Bind(() => color(Design.Tokens), (x, v) => x.TextColor = v);

    public static T Font<T>(this T e, Font font, float? size = null)
        where T : UIElement => Set(e, x => (x.Font, x.FontSize) = (font, size ?? x.FontSize));

    public static T FontSize<T>(this T e, float size)
        where T : UIElement => Set(e, x => x.FontSize = size);

    // Events.

    /// <summary>
    /// Runs <paramref name="onClick"/> when the element is clicked (buttons: also Enter or Space). The element takes the
    /// pointer even without a background, unless <see cref="UIElement.HitTestVisible"/> was set.
    /// </summary>
    public static T OnClick<T>(this T e, Action onClick)
        where T : UIElement
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(onClick);
        if (e is Button button)
        {
            button.Clicked += onClick;
        }
        else
        {
            e.HitTestVisible ??= true; // a transparent row with a click handler must still be clickable
            e.PointerClicked += (_, args) =>
            {
                onClick();
                args.Handled = true;
            };
        }

        return e;
    }

    public static T OnKey<T>(this T e, Action<KeyEventArgs> onKey)
        where T : UIElement
    {
        ArgumentNullException.ThrowIfNull(e);
        e.KeyPressed += (_, args) => onKey(args);
        return e;
    }

    public static T OnFocus<T>(this T e, Action onFocus)
        where T : UIElement
    {
        ArgumentNullException.ThrowIfNull(e);
        e.GotFocus += _ => onFocus();
        return e;
    }

    public static T OnBlur<T>(this T e, Action onBlur)
        where T : UIElement
    {
        ArgumentNullException.ThrowIfNull(e);
        e.LostFocus += _ => onBlur();
        return e;
    }

    /// <summary>Raises <paramref name="action"/> through <see cref="UIContext.ActionInvoked"/> when activated.</summary>
    public static T Action<T>(this T e, string action)
        where T : UIElement => Set(e, x => x.Action = action);

    // Text.

    public static T Text<T>(this T e, Func<string> text)
        where T : Label => e.Bind(text, (x, v) => x.Text = v);

    public static T Text<T>(this T e, IReadable<string> text)
        where T : Label => e.Bind(() => text.Value, (x, v) => x.Text = v);

    public static T Wrap<T>(this T e, bool wrap = true)
        where T : Label => Set(e, x => x.Wrap = wrap);

    public static T Alignment<T>(this T e, TextAlign alignment)
        where T : Label => Set(e, x => x.Alignment = alignment);

    /// <summary>Adds or removes one class in a space-separated <see cref="UIElement.StyleClass"/>.</summary>
    public static void SetClass(this UIElement element, string styleClass, bool on)
    {
        ArgumentNullException.ThrowIfNull(element);
        var classes = (element.StyleClass ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var has = classes.Contains(styleClass, StringComparer.OrdinalIgnoreCase);
        if (has == on)
        {
            return;
        }

        if (on)
        {
            classes.Add(styleClass);
        }
        else
        {
            classes.RemoveAll(c => string.Equals(c, styleClass, StringComparison.OrdinalIgnoreCase));
        }

        element.StyleClass = classes.Count == 0 ? null : string.Join(' ', classes);
    }

    private static T Set<T>(T e, Action<T> set)
        where T : UIElement
    {
        ArgumentNullException.ThrowIfNull(e);
        set(e);
        return e;
    }
}
