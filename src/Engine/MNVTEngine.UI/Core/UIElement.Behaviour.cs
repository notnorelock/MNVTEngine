using MNVTEngine.Mathematics;

namespace MNVTEngine.UI;

public partial class UIElement
{
    public event Action<UIElement, PointerEventArgs>? PointerEntered;

    public event Action<UIElement, PointerEventArgs>? PointerExited;

    public event Action<UIElement, PointerEventArgs>? PointerPressed;

    public event Action<UIElement, PointerEventArgs>? PointerReleased;

    /// <summary>Pressed and released over the element with the left button (or activated with Enter/Space when focused).</summary>
    public event Action<UIElement, PointerEventArgs>? PointerClicked;

    public event Action<UIElement, KeyEventArgs>? KeyPressed;

    public event Action<UIElement>? GotFocus;

    public event Action<UIElement>? LostFocus;

    /// <summary>
    /// Size of the content box when <see cref="Width"/> or <see cref="Height"/> is <see cref="UIUnit.Auto"/>, for the
    /// given space (infinite when unconstrained). Containers measure their children; widgets measure text and images.
    /// </summary>
    protected internal virtual Vector2 MeasureContent(float availableWidth, float availableHeight) =>
        LayoutEngine.MeasureChildren(this, availableWidth, availableHeight);

    /// <summary>Places children inside <see cref="ContentBounds"/>; containers use their <see cref="Layout"/> mode.</summary>
    protected internal virtual void ArrangeChildren() => LayoutEngine.ArrangeChildren(this);

    /// <summary>Draws the element (not its children). The default draws background, border and shadow from the style.</summary>
    protected internal virtual void Paint(UIPainter painter, in ResolvedStyle style) => painter.DrawBox(Bounds, style);

    /// <summary>Drawn after the children (scroll bars, focus rings).</summary>
    protected internal virtual void PaintOver(UIPainter painter, in ResolvedStyle style)
    {
    }

    /// <summary>Called every update while attached (caret blink, animation). Return true when the element must be redrawn.</summary>
    protected internal virtual bool Tick(float deltaSeconds) => false;

    /// <summary>
    /// Raises an event to game code. In a scene's UI it runs in the scene's context (<c>Time</c>, <c>Input</c> and
    /// <c>SceneManager</c> work), and a throwing handler is logged like a component callback instead of breaking the
    /// UI or the host. Standalone contexts call it directly.
    /// </summary>
    protected internal void RaiseEvent(string name, Action raise)
    {
        ArgumentNullException.ThrowIfNull(raise);
        if (Context?.Scene is { } scene)
        {
            scene.RunGameCode(raise, GetType().Name, name, Name.Length > 0 ? Name : GetType().Name);
        }
        else
        {
            raise();
        }
    }

    protected internal virtual void OnPointerEnter(PointerEventArgs e)
    {
        if (PointerEntered is { } handler)
        {
            RaiseEvent(nameof(PointerEntered), () => handler(this, e));
        }
    }

    protected internal virtual void OnPointerExit(PointerEventArgs e)
    {
        if (PointerExited is { } handler)
        {
            RaiseEvent(nameof(PointerExited), () => handler(this, e));
        }
    }

    protected internal virtual void OnPointerDown(PointerEventArgs e)
    {
        if (PointerPressed is { } handler)
        {
            RaiseEvent(nameof(PointerPressed), () => handler(this, e));
        }
    }

    protected internal virtual void OnPointerUp(PointerEventArgs e)
    {
        if (PointerReleased is { } handler)
        {
            RaiseEvent(nameof(PointerReleased), () => handler(this, e));
        }
    }

    /// <summary>Pointer moved while this element holds the pointer capture (dragging after a press on it).</summary>
    protected internal virtual void OnPointerDrag(PointerEventArgs e)
    {
    }

    protected internal virtual void OnClick(PointerEventArgs e)
    {
        if (PointerClicked is { } handler)
        {
            RaiseEvent(nameof(PointerClicked), () => handler(this, e));
        }

        if (Action is { } action)
        {
            Context?.RaiseAction(this, action);
        }
    }

    protected internal virtual void OnScroll(PointerEventArgs e)
    {
    }

    protected internal virtual void OnKeyDown(KeyEventArgs e)
    {
        if (KeyPressed is { } handler)
        {
            RaiseEvent(nameof(KeyPressed), () => handler(this, e));
        }
    }

    protected internal virtual void OnTextInput(TextInputEventArgs e)
    {
    }

    protected internal virtual void OnFocus()
    {
        if (GotFocus is { } handler)
        {
            RaiseEvent(nameof(GotFocus), () => handler(this));
        }
    }

    protected internal virtual void OnBlur()
    {
        if (LostFocus is { } handler)
        {
            RaiseEvent(nameof(LostFocus), () => handler(this));
        }
    }
}

public sealed partial class UITheme
{
    /// <summary>Layers type, type:state, classes and class:states, then the element's own styles; fills defaults.</summary>
    internal ResolvedStyle Resolve(UIElement element, string type, UIState state, Graphics.Text.Font? defaultFont)
    {
        var style = new UIStyle();
        style.Apply(Find("UIElement"));
        Layer(style, type, state);
        if (element.StyleClass is { Length: > 0 } classes)
        {
            foreach (var name in classes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Layer(style, "." + name, state);
            }
        }

        style.Apply(element.Style);
        foreach (var (flag, own) in element.StateStyles)
        {
            if (flag != UIState.Normal && (state & flag) == flag)
            {
                style.Apply(own);
            }
        }

        return new ResolvedStyle(
            style.Background ?? Color.Clear,
            style.Foreground ?? Color.White,
            style.BorderColor ?? Color.Clear,
            style.BorderWidth ?? 0f,
            style.CornerRadius ?? 0f,
            style.Accent ?? new Color(0.27f, 0.55f, 1f, 1f),
            style.Font ?? defaultFont,
            style.FontSize ?? DefaultFontSize,
            style.Padding ?? Thickness.Zero,
            Math.Clamp(style.Opacity ?? 1f, 0f, 1f),
            style.Shadow ?? Color.Clear,
            style.ShadowOffset ?? new Vector2(0f, 2f));
    }

    private void Layer(UIStyle style, string selector, UIState state)
    {
        style.Apply(Find(selector));
        foreach (var (flag, suffix) in StateSuffixes)
        {
            if ((state & flag) != 0)
            {
                style.Apply(Find(selector + suffix));
            }
        }
    }

    private static readonly (UIState Flag, string Suffix)[] StateSuffixes =
        [(UIState.Checked, ":checked"), (UIState.Hover, ":hover"), (UIState.Focused, ":focused"), (UIState.Pressed, ":pressed"), (UIState.Disabled, ":disabled")];
}
