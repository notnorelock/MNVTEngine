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

    protected internal virtual void OnPointerEnter(PointerEventArgs e) => PointerEntered?.Invoke(this, e);

    protected internal virtual void OnPointerExit(PointerEventArgs e) => PointerExited?.Invoke(this, e);

    protected internal virtual void OnPointerDown(PointerEventArgs e) => PointerPressed?.Invoke(this, e);

    protected internal virtual void OnPointerUp(PointerEventArgs e) => PointerReleased?.Invoke(this, e);

    /// <summary>Pointer moved while this element holds the pointer capture (dragging after a press on it).</summary>
    protected internal virtual void OnPointerDrag(PointerEventArgs e)
    {
    }

    protected internal virtual void OnClick(PointerEventArgs e)
    {
        PointerClicked?.Invoke(this, e);
        if (Action is { } action)
        {
            Context?.RaiseAction(this, action);
        }
    }

    protected internal virtual void OnScroll(PointerEventArgs e)
    {
    }

    protected internal virtual void OnKeyDown(KeyEventArgs e) => KeyPressed?.Invoke(this, e);

    protected internal virtual void OnTextInput(TextInputEventArgs e)
    {
    }

    protected internal virtual void OnFocus() => GotFocus?.Invoke(this);

    protected internal virtual void OnBlur() => LostFocus?.Invoke(this);
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
