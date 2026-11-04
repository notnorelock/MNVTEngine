using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Text;
using MNVTEngine.Mathematics;

namespace MNVTEngine.UI.Widgets;

/// <summary>
/// A clickable control with text and an optional icon or background image. States normal, hover, pressed, focused and
/// disabled come from the theme (<c>Button</c>, <c>Button:hover</c>...). Activated by a click, or by Enter/Space when focused.
/// </summary>
public class Button : UIElement
{
    private string _text = "";
    private Texture2D? _icon, _backgroundImage;
    private readonly TextBlock _block = new();

    public Button() => Focusable = true;

    public string Text { get => _text; set => SetLayout(ref _text, value ?? ""); }

    /// <summary>Drawn left of the text, at the font size.</summary>
    public Texture2D? Icon { get => _icon; set => SetLayout(ref _icon, value); }

    /// <summary>Stretched over the button under the text (tinted by the background color's alpha, white when unset).</summary>
    public Texture2D? BackgroundImage { get => _backgroundImage; set => SetVisual(ref _backgroundImage, value); }

    /// <summary>Raised when the button is activated (click, Enter or Space).</summary>
    public event Action? Clicked;

    protected internal override void OnClick(PointerEventArgs e)
    {
        base.OnClick(e);
        Clicked?.Invoke();
        e.Handled = true;
    }

    protected internal override Vector2 MeasureContent(float availableWidth, float availableHeight)
    {
        var style = ResolveStyle();
        _block.Get(this, Text, style, float.PositiveInfinity, TextAlign.Center);
        var text = _block.Size;
        var icon = Icon is null ? 0f : style.FontSize + (Text.Length > 0 ? 6f : 0f);
        return new Vector2(text.X + icon, Math.Max(text.Y, Icon is null ? 0f : style.FontSize));
    }

    protected internal override void Paint(UIPainter painter, in ResolvedStyle style)
    {
        if (BackgroundImage is { } image)
        {
            painter.DrawImage(image, Bounds, style.Background.A > 0f ? style.Background : Color.White);
            if (style.BorderWidth > 0f)
            {
                painter.StrokeRect(Bounds, style.BorderColor, style.BorderWidth, style.CornerRadius);
            }
        }
        else
        {
            base.Paint(painter, style);
        }

        var content = ContentBounds;
        var layout = _block.Get(this, Text, style, float.PositiveInfinity, TextAlign.Center);
        var iconSize = Icon is null ? 0f : style.FontSize;
        var width = _block.Size.X + (Icon is null ? 0f : iconSize + (Text.Length > 0 ? 6f : 0f));
        var x = content.X + ((content.Width - width) / 2f);
        painter.PushClip(ClipBounds);
        if (Icon is { } icon)
        {
            painter.DrawImage(icon, new Rect(x, content.Y + ((content.Height - iconSize) / 2f), iconSize, iconSize), style.Foreground);
            x += iconSize + 6f;
        }

        painter.DrawText(layout, new Vector2(x, content.Y + ((content.Height - _block.Size.Y) / 2f)), style.Foreground);
        painter.PopClip();
    }
}

/// <summary>A box that toggles <see cref="Checked"/>, with a label to its right.</summary>
public class Checkbox : UIElement
{
    private bool _checked;
    private string _text = "";
    private readonly TextBlock _block = new();

    public Checkbox() => Focusable = true;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked != value)
            {
                _checked = value;
                InvalidateVisual();
                CheckedChanged?.Invoke(value);
            }
        }
    }

    public string Text { get => _text; set => SetLayout(ref _text, value ?? ""); }

    public event Action<bool>? CheckedChanged;

    protected override bool IsChecked => Checked;

    protected static float BoxSize(in ResolvedStyle style) => MathF.Round(style.FontSize * 1.05f);

    /// <summary>Width of the mark left of the label.</summary>
    protected virtual float MarkWidth(in ResolvedStyle style) => BoxSize(style);

    protected internal override void OnClick(PointerEventArgs e)
    {
        base.OnClick(e);
        Toggle();
        e.Handled = true;
    }

    /// <summary>What a click does; radio buttons only turn on.</summary>
    protected virtual void Toggle() => Checked = !Checked;

    protected internal override Vector2 MeasureContent(float availableWidth, float availableHeight)
    {
        var style = ResolveStyle();
        _block.Get(this, Text, style, float.PositiveInfinity);
        var box = BoxSize(style);
        return new Vector2(MarkWidth(style) + (Text.Length > 0 ? 8f + _block.Size.X : 0f), Math.Max(box, _block.Size.Y));
    }

    protected internal override void Paint(UIPainter painter, in ResolvedStyle style)
    {
        var content = ContentBounds;
        var size = BoxSize(style);
        var box = new Rect(content.X, content.Y + ((content.Height - size) / 2f), size, size);
        PaintMark(painter, style, box);
        if (Text.Length > 0)
        {
            var layout = _block.Get(this, Text, style, float.PositiveInfinity);
            painter.DrawText(layout, new Vector2(box.X + MarkWidth(style) + 8f, content.Y + ((content.Height - _block.Size.Y) / 2f)), style.Foreground);
        }
    }

    /// <summary>Draws the square box with its check mark.</summary>
    protected virtual void PaintMark(UIPainter painter, in ResolvedStyle style, Rect box)
    {
        var theme = Context?.Theme ?? UITheme.Dark;
        var control = theme.Styles.TryGetValue("Button" + ((State & UIState.Hover) != 0 ? ":hover" : ""), out var s) && s.Background is { } c ? c : new Color(0.2f, 0.2f, 0.23f, 1f);
        painter.FillRect(box, Checked ? style.Accent : control, 4f);
        painter.StrokeRect(box, (State & UIState.Focused) != 0 ? style.Accent : style.BorderColor, 1f, 4f);
        if (Checked)
        {
            // A check mark from two strokes would need line geometry; a bold inner square reads as "on" at any size.
            var inset = box.Width * 0.28f;
            painter.FillRect(new Rect(box.X + inset, box.Y + inset, box.Width - (2 * inset), box.Height - (2 * inset)), Color.White, 2f);
        }
    }
}

/// <summary>A sliding on/off switch.</summary>
public class Toggle : Checkbox
{
    protected override void PaintMark(UIPainter painter, in ResolvedStyle style, Rect box)
    {
        var track = new Rect(box.X, box.Y + (box.Height * 0.1f), box.Width * 1.9f, box.Height * 0.8f);
        painter.FillRect(track, Checked ? style.Accent : style.BorderColor, track.Height / 2f);
        var knob = track.Height - 4f;
        var x = Checked ? track.X + track.Width - knob - 2f : track.X + 2f;
        painter.FillRect(new Rect(x, track.Y + 2f, knob, knob), Color.White, knob / 2f);
        if ((State & UIState.Focused) != 0)
        {
            painter.StrokeRect(track, style.Accent, 1f, track.Height / 2f);
        }
    }

    protected override float MarkWidth(in ResolvedStyle style) => BoxSize(style) * 1.9f;
}

/// <summary>One choice of a group: checking it unchecks the other radio buttons with the same <see cref="Group"/> in the context.</summary>
public class RadioButton : Checkbox
{
    public string Group { get; set; } = "";

    protected override void Toggle()
    {
        if (Checked)
        {
            return;
        }

        Checked = true;
        foreach (var other in Context?.Root.Descendants().Concat(Context.Overlay.Descendants()).OfType<RadioButton>() ?? [])
        {
            if (other != this && other.Group == Group)
            {
                other.Checked = false;
            }
        }
    }

    protected override void PaintMark(UIPainter painter, in ResolvedStyle style, Rect box)
    {
        painter.FillRect(box, style.Background.A > 0 ? style.Background : new Color(0.2f, 0.2f, 0.23f, 1f), box.Width / 2f);
        painter.StrokeRect(box, Checked || (State & UIState.Focused) != 0 ? style.Accent : style.BorderColor, 1.5f, box.Width / 2f);
        if (Checked)
        {
            var inset = box.Width * 0.26f;
            painter.FillRect(new Rect(box.X + inset, box.Y + inset, box.Width - (2 * inset), box.Height - (2 * inset)), style.Accent, (box.Width / 2f) - inset);
        }
    }
}
