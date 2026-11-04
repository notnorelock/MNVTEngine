using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Text;
using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;

namespace MNVTEngine.UI.Widgets;

/// <summary>A container: background, border and children arranged by <see cref="UIElement.Layout"/>. Style class "panel" gives the themed card look.</summary>
public class Panel : UIElement
{
}

/// <summary>Text with font, size, color, alignment, wrapping, line and letter spacing (all through the style or properties).</summary>
public class Label : UIElement
{
    private string _text = "";
    private TextAlign _alignment;
    private Align _verticalAlignment = Align.Start;
    private bool _wrap;
    private float _lineSpacing = 1f, _letterSpacing;

    public string Text { get => _text; set => SetLayout(ref _text, value ?? ""); }

    public TextAlign Alignment { get => _alignment; set => SetLayout(ref _alignment, value); }

    /// <summary>Placement of the text inside a taller label.</summary>
    public Align VerticalAlignment { get => _verticalAlignment; set => SetVisual(ref _verticalAlignment, value); }

    /// <summary>Wraps at the label's width (needs a width from the layout, e.g. stretch or a fixed width).</summary>
    public bool Wrap { get => _wrap; set => SetLayout(ref _wrap, value); }

    /// <summary>Line height multiplier.</summary>
    public float LineSpacing { get => _lineSpacing; set => SetLayout(ref _lineSpacing, value); }

    public float LetterSpacing { get => _letterSpacing; set => SetLayout(ref _letterSpacing, value); }

    protected TextBlock Block { get; } = new();

    protected internal override Vector2 MeasureContent(float availableWidth, float availableHeight)
    {
        var style = ResolveStyle();
        Block.Get(this, Text, style, Wrap ? availableWidth : float.PositiveInfinity, Alignment, Wrap, LineSpacing, LetterSpacing);
        return Block.Size;
    }

    protected internal override void Paint(UIPainter painter, in ResolvedStyle style)
    {
        base.Paint(painter, style);
        var content = ContentBounds;
        var layout = Block.Get(this, Text, style, Wrap || Alignment != TextAlign.Left ? content.Width : float.PositiveInfinity, Alignment, Wrap, LineSpacing, LetterSpacing);
        var height = Block.Size.Y;
        var y = VerticalAlignment switch
        {
            Align.Center => content.Y + ((content.Height - height) / 2f),
            Align.End => content.Y + content.Height - height,
            _ => content.Y,
        };
        painter.PushClip(ClipBounds);
        painter.DrawText(layout, new Vector2(content.X, y), style.Foreground);
        painter.PopClip();
    }
}

public enum ImageScaleMode
{
    /// <summary>Fills the content box, ignoring the aspect ratio.</summary>
    Stretch,

    /// <summary>Largest size that fits, aspect kept (letterboxed).</summary>
    Fit,

    /// <summary>Covers the content box, aspect kept (cropped).</summary>
    Fill,
}

/// <summary>Shows a texture: directly, or a texture asset resolved through the scene's UI services.</summary>
public class Image : UIElement
{
    private Texture2D? _texture;
    private AssetRef? _asset;
    private ImageScaleMode _scaleMode = ImageScaleMode.Fit;
    private Color _tint = Color.White;

    public Texture2D? Texture { get => _texture; set => SetLayout(ref _texture, value); }

    /// <summary>A texture asset, used when <see cref="Texture"/> is null.</summary>
    public AssetRef? Asset { get => _asset; set => SetLayout(ref _asset, value); }

    public ImageScaleMode ScaleMode { get => _scaleMode; set => SetVisual(ref _scaleMode, value); }

    public Color Tint { get => _tint; set => SetVisual(ref _tint, value); }

    /// <summary>The texture that is drawn: <see cref="Texture"/>, else the loaded <see cref="Asset"/>.</summary>
    public Texture2D? Resolved => _texture ?? (_asset is { } asset && Context is { } context ? context.LoadTexture(asset) : null);

    protected internal override Vector2 MeasureContent(float availableWidth, float availableHeight) =>
        Resolved is { } texture ? new Vector2(texture.Width, texture.Height) : Vector2.Zero;

    protected internal override void Paint(UIPainter painter, in ResolvedStyle style)
    {
        base.Paint(painter, style);
        if (Resolved is not { } texture)
        {
            return;
        }

        var box = ContentBounds;
        var aspect = (float)texture.Width / texture.Height;
        var rect = box;
        if (ScaleMode != ImageScaleMode.Stretch && box.Height > 0)
        {
            var fitWidth = (box.Width / box.Height > aspect) == (ScaleMode == ImageScaleMode.Fit);
            var size = fitWidth ? new Vector2(box.Height * aspect, box.Height) : new Vector2(box.Width, box.Width / aspect);
            rect = new Rect(box.X + ((box.Width - size.X) / 2f), box.Y + ((box.Height - size.Y) / 2f), size.X, size.Y);
        }

        painter.PushClip(ClipBounds);
        painter.DrawImage(texture, rect, Tint);
        painter.PopClip();
    }
}

/// <summary>A thin line between groups (1 px high in columns, 1 px wide in rows).</summary>
public class Separator : UIElement
{
    protected internal override Vector2 MeasureContent(float availableWidth, float availableHeight) =>
        Parent?.Layout == LayoutMode.Row ? new Vector2(1f, 0f) : new Vector2(0f, 1f);
}

/// <summary>The popup the context shows for <see cref="UIElement.Tooltip"/>; can also be placed manually.</summary>
public sealed class Tooltip : Label
{
    public Tooltip() => HitTestVisible = false;
}

/// <summary>A horizontal bar showing <see cref="Value"/> between <see cref="Minimum"/> and <see cref="Maximum"/>, or an animated indeterminate bar.</summary>
public class ProgressBar : UIElement
{
    private float _value, _minimum, _maximum = 1f;
    private bool _indeterminate, _showText;
    private float _phase;
    private readonly TextBlock _block = new();

    public float Value { get => _value; set => SetVisual(ref _value, value); }

    public float Minimum { get => _minimum; set => SetVisual(ref _minimum, value); }

    public float Maximum { get => _maximum; set => SetVisual(ref _maximum, value); }

    /// <summary>Progress unknown: a moving block instead of a fill (never a made-up percentage).</summary>
    public bool Indeterminate { get => _indeterminate; set => SetVisual(ref _indeterminate, value); }

    /// <summary>Shows the percentage on the bar (determinate only).</summary>
    public bool ShowText { get => _showText; set => SetVisual(ref _showText, value); }

    /// <summary>Fraction filled, 0..1.</summary>
    public float Fraction => Maximum > Minimum ? Math.Clamp((Value - Minimum) / (Maximum - Minimum), 0f, 1f) : 0f;

    protected internal override Vector2 MeasureContent(float availableWidth, float availableHeight) => new(160f, 10f);

    protected internal override bool Tick(float deltaSeconds)
    {
        if (!Indeterminate)
        {
            return false;
        }

        _phase = (_phase + (deltaSeconds * 0.8f)) % 1.4f;
        return true;
    }

    protected internal override void Paint(UIPainter painter, in ResolvedStyle style)
    {
        base.Paint(painter, style);
        var box = Bounds;
        painter.PushClip(ClipBounds);
        if (Indeterminate)
        {
            var width = box.Width * 0.3f;
            var x = box.X + ((_phase - 0.3f) * box.Width);
            painter.PushClip(box);
            painter.FillRect(new Rect(x, box.Y, width, box.Height), style.Accent, style.CornerRadius);
            painter.PopClip();
        }
        else if (Fraction > 0f)
        {
            painter.FillRect(new Rect(box.X, box.Y, Math.Max(box.Width * Fraction, style.CornerRadius * 2f), box.Height), style.Accent, style.CornerRadius);
        }

        if (ShowText && !Indeterminate)
        {
            var layout = _block.Get(this, $"{Fraction * 100f:0}%", style, box.Width, TextAlign.Center);
            painter.DrawText(layout, new Vector2(box.X, box.Y + ((box.Height - _block.Size.Y) / 2f)), style.Foreground);
        }

        painter.PopClip();
    }
}
