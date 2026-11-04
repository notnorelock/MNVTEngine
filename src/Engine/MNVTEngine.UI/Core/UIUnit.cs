using System.Globalization;

namespace MNVTEngine.UI;

public enum UIUnitKind
{
    /// <summary>Sized by content (text, children).</summary>
    Auto,

    /// <summary>Logical pixels (scaled by the context's UI scale).</summary>
    Pixels,

    /// <summary>Percent of the parent's content box.</summary>
    Percent,
}

/// <summary>A length in the UI: <see cref="Pixels"/>, <see cref="Percent"/> of the parent, or <see cref="Auto"/>.</summary>
public readonly record struct UIUnit(UIUnitKind Kind, float Value)
{
    public static UIUnit Auto => default;

    public bool IsAuto => Kind == UIUnitKind.Auto;

    public static UIUnit Pixels(float value) => new(UIUnitKind.Pixels, value);

    public static UIUnit Percent(float value) => new(UIUnitKind.Percent, value);

    /// <summary>Plain numbers are pixels: <c>Width = 240</c>.</summary>
    public static implicit operator UIUnit(float pixels) => Pixels(pixels);

    /// <summary>The length in logical pixels, or null for <see cref="Auto"/>.</summary>
    public float? Resolve(float parentSize) => Kind switch
    {
        UIUnitKind.Pixels => Value,
        UIUnitKind.Percent => parentSize * Value / 100f,
        _ => null,
    };

    public override string ToString() => Kind switch
    {
        UIUnitKind.Pixels => Value.ToString("0.##", CultureInfo.InvariantCulture) + "px",
        UIUnitKind.Percent => Value.ToString("0.##", CultureInfo.InvariantCulture) + "%",
        _ => "auto",
    };

    /// <summary>Parses "120", "120px", "50%" or "auto".</summary>
    /// <exception cref="FormatException">Anything else.</exception>
    public static UIUnit Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var t = text.Trim();
        if (t.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return Auto;
        }

        var (number, kind) = t.EndsWith('%') ? (t[..^1], UIUnitKind.Percent)
            : t.EndsWith("px", StringComparison.OrdinalIgnoreCase) ? (t[..^2], UIUnitKind.Pixels)
            : (t, UIUnitKind.Pixels);
        return float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && float.IsFinite(value)
            ? new UIUnit(kind, value)
            : throw new FormatException($"'{text}' is not a UI length (use 120, 120px, 50% or auto).");
    }
}

/// <summary>Space around (margin) or inside (padding) an element, in logical pixels.</summary>
public readonly record struct Thickness(float Left, float Top, float Right, float Bottom)
{
    public static Thickness Zero => default;

    public float Horizontal => Left + Right;

    public float Vertical => Top + Bottom;

    public static Thickness All(float value) => new(value, value, value, value);

    public static Thickness Symmetric(float horizontal, float vertical) => new(horizontal, vertical, horizontal, vertical);

    public static implicit operator Thickness(float all) => All(all);
}

/// <summary>How a container places its children.</summary>
public enum LayoutMode
{
    /// <summary>Children are positioned by <see cref="UIElement.Anchor"/>, <see cref="UIElement.Pivot"/> and <see cref="UIElement.Position"/>.</summary>
    Absolute,

    /// <summary>Children left to right (flex row): <see cref="UIElement.FlexGrow"/> shares free space.</summary>
    Row,

    /// <summary>Children top to bottom (flex column).</summary>
    Column,

    /// <summary>Children fill <see cref="UIElement.GridColumns"/> equal columns, row by row.</summary>
    Grid,
}

/// <summary>Placement along an axis.</summary>
public enum Align
{
    Start,
    Center,
    End,

    /// <summary>Cross axis only: fill the line (the default for children of rows and columns with an auto cross size).</summary>
    Stretch,
}

/// <summary>Distribution of free space along a row or column.</summary>
public enum Justify
{
    Start,
    Center,
    End,
    SpaceBetween,
}

/// <summary>What happens to children that do not fit.</summary>
public enum Overflow
{
    Visible,

    /// <summary>Children are clipped to the padding box.</summary>
    Clip,
}
