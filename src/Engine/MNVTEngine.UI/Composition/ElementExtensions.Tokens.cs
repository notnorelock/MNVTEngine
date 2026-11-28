// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

using MNVTEngine.Mathematics;

namespace MNVTEngine.UI;

/// <summary>Borders, shadows and state looks from the design tokens, following theme changes.</summary>
public static partial class ElementExtensions
{
    public static T Border<T>(this T e, Func<DesignTokens, Color> color, float width = 1f)
        where T : UIElement => e.Bind(() => color(Design.Tokens), (x, v) => (x.BorderColor, x.BorderWidth) = (v, width));

    /// <summary>A drop shadow: the element's shape in <paramref name="color"/>, moved by <paramref name="offset"/>.</summary>
    public static T Shadow<T>(this T e, Func<DesignTokens, Color> color, Vector2 offset)
        where T : UIElement => e.Bind(() => color(Design.Tokens), (x, v) =>
        {
            (x.Style.Shadow, x.Style.ShadowOffset) = (v, offset);
            x.InvalidateVisual();
        });

    /// <summary>
    /// How the element looks in <paramref name="state"/> (hover, pressed, checked...), built from the tokens: layered
    /// over its own style while the state holds, like a <c>Button:hover</c> selector for this element alone.
    /// </summary>
    public static T WhenState<T>(this T e, UIState state, Func<DesignTokens, UIStyle> style)
        where T : UIElement => e.Bind(() => style(Design.Tokens), (x, v) =>
        {
            x.StateStyles[state] = v;
            x.InvalidateVisual();
        });

    /// <summary>A background while hovered (<see cref="WhenState{T}"/>).</summary>
    public static T HoverBackground<T>(this T e, Func<DesignTokens, Color> color)
        where T : UIElement => e.WhenState(UIState.Hover, t => new UIStyle { Background = color(t) });
}
