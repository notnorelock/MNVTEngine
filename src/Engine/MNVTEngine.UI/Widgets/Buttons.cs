// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Text;
using MNVTEngine.Mathematics;

namespace MNVTEngine.UI.Widgets;

/// <summary>
/// A clickable control with text and an optional icon (a texture, or a glyph from the icon font) or background image.
/// States normal, hover, pressed, focused and disabled come from the theme (<c>Button</c>, <c>Button:hover</c>...);
/// variants are style classes (<c>.primary</c>, <c>.quiet</c>...). Activated by a click, or by Enter/Space when
/// focused. Optional motion: a spring scale on hover and press, and a ripple from the point pressed.
/// </summary>
public class Button : UIElement
{
    private const float RippleSeconds = 0.55f;
    private readonly TextBlock _block = new();
    private readonly TextBlock _glyphBlock = new();
    private readonly List<(Vector2 Center, float Age)> _ripples = [];
    private string _text = "";
    private string? _glyph;
    private Texture2D? _icon, _backgroundImage;
    private bool _loading;
    private float _loadingTime;

    public Button() => Focusable = true;

    public string Text { get => _text; set => SetLayout(ref _text, value ?? ""); }

    /// <summary>Drawn left of the text, at the font size.</summary>
    public Texture2D? Icon { get => _icon; set => SetLayout(ref _icon, value); }

    /// <summary>An icon-font glyph (see <see cref="Icons"/>) drawn left of the text, in the text color.</summary>
    public string? Glyph { get => _glyph; set => SetLayout(ref _glyph, string.IsNullOrEmpty(value) ? null : value); }

    /// <summary>Stretched over the button under the text (tinted by the background color's alpha, white when unset).</summary>
    public Texture2D? BackgroundImage { get => _backgroundImage; set => SetVisual(ref _backgroundImage, value); }

    /// <summary>Shows a pulsing indicator instead of the content and refuses clicks (work in progress).</summary>
    public bool Loading { get => _loading; set => SetVisual(ref _loading, value); }

    /// <summary>Scale while hovered (1 = none).</summary>
    public float HoverScale { get; set; } = 1f;

    /// <summary>Scale while pressed (1 = none).</summary>
    public float PressScale { get; set; } = 1f;

    /// <summary>How the hover and press scale move.</summary>
    public Motion ScaleMotion { get; set; } = Motion.Spring(480f);

    /// <summary>A press spreads a soft circle from the point pressed.</summary>
    public bool Ripple { get; set; }

    /// <summary>Raised when the button is activated (click, Enter or Space).</summary>
    public event Action? Clicked;

    protected internal override void OnClick(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        e.Handled = true;
        if (Loading)
        {
            return;
        }

        base.OnClick(e);
        if (Clicked is { } handler)
        {
            RaiseEvent(nameof(Clicked), handler);
        }
    }

    protected internal override void OnPointerEnter(PointerEventArgs e)
    {
        base.OnPointerEnter(e);
        ScaleTo(Context?.Pressed == this ? PressScale : HoverScale);
    }

    protected internal override void OnPointerExit(PointerEventArgs e)
    {
        base.OnPointerExit(e);
        if (Context?.Pressed != this)
        {
            ScaleTo(1f); // a held press keeps its scale, or a shrinking button near the pointer would flicker
        }
    }

    protected internal override void OnPointerDown(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerDown(e);
        ScaleTo(PressScale);
        if (Ripple && !Loading)
        {
            _ripples.Add((e.Position, 0f));
            InvalidateVisual();
        }
    }

    protected internal override void OnPointerUp(PointerEventArgs e)
    {
        base.OnPointerUp(e);
        ScaleTo(Context?.Hovered == this ? HoverScale : 1f);
    }

    protected internal override bool Tick(float deltaSeconds)
    {
        var changed = false;
        if (_ripples.Count > 0)
        {
            for (var i = _ripples.Count - 1; i >= 0; i--)
            {
                var age = _ripples[i].Age + deltaSeconds;
                if (age >= RippleSeconds)
                {
                    _ripples.RemoveAt(i);
                }
                else
                {
                    _ripples[i] = (_ripples[i].Center, age);
                }
            }

            changed = true;
        }

        if (Loading)
        {
            _loadingTime += deltaSeconds;
            changed = true;
        }

        return changed;
    }

    protected internal override Vector2 MeasureContent(float availableWidth, float availableHeight)
    {
        var style = ResolveStyle();
        _block.Get(this, Text, style, float.PositiveInfinity, TextAlign.Center);
        var text = _block.Size;
        var icon = IconWidth(style);
        return new Vector2(text.X + icon, Math.Max(text.Y, icon > 0f ? style.FontSize : 0f));
    }

    protected internal override void Paint(UIPainter painter, in ResolvedStyle style)
    {
        ArgumentNullException.ThrowIfNull(painter);
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

        painter.PushClip(Bounds); // the parent's clip is already pushed (and stays put when this one moves)
        foreach (var (center, age) in _ripples)
        {
            var t = age / RippleSeconds;
            var reach = MathF.Sqrt((Bounds.Width * Bounds.Width) + (Bounds.Height * Bounds.Height));
            var radius = reach * (0.2f + (0.8f * Motion.Ease(Easing.CubicOut, t)));
            painter.FillRect(new Rect(center.X - radius, center.Y - radius, radius * 2f, radius * 2f), style.Foreground with { A = 0.14f * (1f - t) }, radius);
        }

        var content = ContentBounds;
        if (Loading)
        {
            PaintLoading(painter, style, content);
            painter.PopClip();
            return;
        }

        var layout = _block.Get(this, Text, style, float.PositiveInfinity, TextAlign.Center);
        var iconWidth = IconWidth(style);
        var x = content.X + ((content.Width - (_block.Size.X + iconWidth)) / 2f);
        if (Icon is { } icon)
        {
            painter.DrawImage(icon, new Rect(x, content.Y + ((content.Height - style.FontSize) / 2f), style.FontSize, style.FontSize), style.Foreground);
        }
        else if (Glyph is { } glyph)
        {
            var glyphLayout = _glyphBlock.Get(this, glyph, GlyphStyle(style), float.PositiveInfinity);
            painter.DrawText(glyphLayout, new Vector2(x, content.Y + ((content.Height - _glyphBlock.Size.Y) / 2f)), style.Foreground);
        }

        painter.DrawText(layout, new Vector2(x + iconWidth, content.Y + ((content.Height - _block.Size.Y) / 2f)), style.Foreground);
        painter.PopClip();
    }

    private static float GlyphSize(in ResolvedStyle style) => MathF.Round(style.FontSize * 1.2f);

    private static ResolvedStyle GlyphStyle(in ResolvedStyle style) => style with { Font = DesignFonts.Icons, FontSize = GlyphSize(style) };

    private float IconWidth(in ResolvedStyle style)
    {
        var size = Icon is not null ? style.FontSize : Glyph is not null ? GlyphSize(style) : 0f;
        return size > 0f && Text.Length > 0 ? size + MathF.Round(style.FontSize * 0.5f) : size;
    }

    private void PaintLoading(UIPainter painter, in ResolvedStyle style, Rect content)
    {
        var dot = MathF.Max(4f, MathF.Round(style.FontSize * 0.4f));
        var gap = dot * 0.8f;
        var x = content.X + ((content.Width - ((3f * dot) + (2f * gap))) / 2f);
        var y = content.Y + ((content.Height - dot) / 2f);
        for (var i = 0; i < 3; i++)
        {
            var pulse = 0.35f + (0.65f * (0.5f + (0.5f * MathF.Sin((_loadingTime * 6f) - (i * 0.9f)))));
            painter.FillRect(new Rect(x + (i * (dot + gap)), y, dot, dot), style.Foreground with { A = style.Foreground.A * pulse }, dot / 2f);
        }
    }

    private void ScaleTo(float scale)
    {
        if (HoverScale != 1f || PressScale != 1f || RenderScale != 1f)
        {
            this.AnimateScale(IsEnabledInHierarchy ? scale : 1f, ScaleMotion);
        }
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
                if (CheckedChanged is { } handler)
                {
                    RaiseEvent(nameof(CheckedChanged), () => handler(value));
                }
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
