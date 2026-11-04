using MNVTEngine.Graphics.Text;
using MNVTEngine.Mathematics;

namespace MNVTEngine.UI;

/// <summary>Interaction state used to pick style variants.</summary>
[Flags]
public enum UIState
{
    Normal = 0,
    Hover = 1,
    Pressed = 2,
    Focused = 4,
    Disabled = 8,

    /// <summary>On, for checkboxes, toggles and radio buttons.</summary>
    Checked = 16,
}

/// <summary>
/// Visual properties; null means "not set here" so styles can be layered (theme, then class, then the element's own).
/// Independent of rendering: the UI renderer reads the resolved values.
/// </summary>
public sealed class UIStyle
{
    public Color? Background { get; set; }

    public Color? Foreground { get; set; }

    public Color? BorderColor { get; set; }

    public float? BorderWidth { get; set; }

    public float? CornerRadius { get; set; }

    /// <summary>Accent: slider fill, checkbox mark, caret, selection, focus ring.</summary>
    public Color? Accent { get; set; }

    public Font? Font { get; set; }

    public float? FontSize { get; set; }

    public Thickness? Padding { get; set; }

    /// <summary>Multiplies the element's (and its children's) alpha.</summary>
    public float? Opacity { get; set; }

    /// <summary>Drop shadow color (offset below-right by <see cref="ShadowOffset"/>); null for none.</summary>
    public Color? Shadow { get; set; }

    public Vector2? ShadowOffset { get; set; }

    /// <summary>Copies every property set on <paramref name="other"/> over this style.</summary>
    public UIStyle Apply(UIStyle? other)
    {
        if (other is null)
        {
            return this;
        }

        Background = other.Background ?? Background;
        Foreground = other.Foreground ?? Foreground;
        BorderColor = other.BorderColor ?? BorderColor;
        BorderWidth = other.BorderWidth ?? BorderWidth;
        CornerRadius = other.CornerRadius ?? CornerRadius;
        Accent = other.Accent ?? Accent;
        Font = other.Font ?? Font;
        FontSize = other.FontSize ?? FontSize;
        Padding = other.Padding ?? Padding;
        Opacity = other.Opacity ?? Opacity;
        Shadow = other.Shadow ?? Shadow;
        ShadowOffset = other.ShadowOffset ?? ShadowOffset;
        return this;
    }

    public UIStyle Clone() => new UIStyle().Apply(this);
}

/// <summary>Style values after layering, with defaults filled in.</summary>
public readonly record struct ResolvedStyle(
    Color Background,
    Color Foreground,
    Color BorderColor,
    float BorderWidth,
    float CornerRadius,
    Color Accent,
    Font? Font,
    float FontSize,
    Thickness Padding,
    float Opacity,
    Color Shadow,
    Vector2 ShadowOffset);

/// <summary>
/// Named styles, the CSS idea in small: selectors are a widget type (<c>"Button"</c>), a style class (<c>".primary"</c>),
/// either with a state (<c>"Button:hover"</c>, <c>".primary:pressed"</c>; states: hover, pressed, focused, disabled,
/// checked). An element's style is: type, type:state..., class, class:state..., then the element's own
/// <see cref="UIElement.Style"/> and its <see cref="UIElement.StateStyles"/>. Later layers win.
/// </summary>
public sealed partial class UITheme
{
    private readonly Dictionary<string, UIStyle> _styles = new(StringComparer.OrdinalIgnoreCase);

    public UITheme(string name) => Name = name;

    public string Name { get; }

    /// <summary>Used when nothing sets a font (the context's default font, normally the built-in one).</summary>
    public float DefaultFontSize { get; set; } = 18f;

    public IReadOnlyDictionary<string, UIStyle> Styles => _styles;

    /// <summary>The style for a selector, created empty on first use (so <c>theme["Button:hover"].Background = ...</c> works).</summary>
    public UIStyle this[string selector]
    {
        get
        {
            if (!_styles.TryGetValue(selector, out var style))
            {
                _styles[selector] = style = new UIStyle();
            }

            return style;
        }
        set => _styles[selector] = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Dark theme: near-black panels, light text, blue accent.</summary>
    public static UITheme Dark => UIThemes.Dark();

    /// <summary>Light theme: white panels, dark text, blue accent.</summary>
    public static UITheme Light => UIThemes.Light();

    internal UIStyle? Find(string selector) => _styles.GetValueOrDefault(selector);
}
