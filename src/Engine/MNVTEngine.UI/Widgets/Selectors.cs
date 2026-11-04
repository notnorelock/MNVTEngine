using MNVTEngine.Graphics.Text;
using MNVTEngine.Mathematics;

namespace MNVTEngine.UI.Widgets;

/// <summary>Picks a value between <see cref="Minimum"/> and <see cref="Maximum"/> by dragging, clicking the track, or arrow keys when focused.</summary>
public class Slider : UIElement
{
    private float _value, _minimum, _maximum = 1f, _step;

    public Slider() => Focusable = true;

    public float Value
    {
        get => _value;
        set
        {
            var v = Math.Clamp(Step > 0 ? MathF.Round((value - Minimum) / Step) * Step + Minimum : value, Minimum, Math.Max(Minimum, Maximum));
            if (v != _value)
            {
                _value = v;
                InvalidateVisual();
                ValueChanged?.Invoke(v);
            }
        }
    }

    public float Minimum { get => _minimum; set => SetVisual(ref _minimum, value); }

    public float Maximum { get => _maximum; set => SetVisual(ref _maximum, value); }

    /// <summary>Snapping interval (0 = continuous); also the arrow-key increment (else 1/20 of the range).</summary>
    public float Step { get => _step; set => SetVisual(ref _step, value); }

    public event Action<float>? ValueChanged;

    protected internal override Vector2 MeasureContent(float availableWidth, float availableHeight) => new(160f, 20f);

    protected internal override void OnPointerDown(PointerEventArgs e)
    {
        base.OnPointerDown(e);
        SetFromPointer(e.Position);
        e.Handled = true;
    }

    protected internal override void OnPointerDrag(PointerEventArgs e) => SetFromPointer(e.Position);

    protected internal override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = Step > 0 ? Step : (Maximum - Minimum) / 20f;
        switch (e.Key)
        {
            case Key.Left or Key.Down:
                Value -= step;
                e.Handled = true;
                break;
            case Key.Right or Key.Up:
                Value += step;
                e.Handled = true;
                break;
            case Key.Home:
                Value = Minimum;
                e.Handled = true;
                break;
            case Key.End:
                Value = Maximum;
                e.Handled = true;
                break;
        }
    }

    protected internal override void Paint(UIPainter painter, in ResolvedStyle style)
    {
        var box = Bounds;
        var fraction = Maximum > Minimum ? (Value - Minimum) / (Maximum - Minimum) : 0f;
        var trackHeight = 6f;
        var track = new Rect(box.X + 8f, box.Y + ((box.Height - trackHeight) / 2f), Math.Max(0f, box.Width - 16f), trackHeight);
        painter.FillRect(track, style.Background, trackHeight / 2f);
        painter.FillRect(new Rect(track.X, track.Y, track.Width * fraction, trackHeight), style.Accent, trackHeight / 2f);
        var knob = MathF.Min(box.Height, 18f);
        var center = new Vector2(track.X + (track.Width * fraction), box.Y + (box.Height / 2f));
        painter.FillRect(new Rect(center.X - (knob / 2f), center.Y - (knob / 2f), knob, knob), Color.White, knob / 2f);
        if ((State & UIState.Focused) != 0)
        {
            painter.StrokeRect(new Rect(center.X - (knob / 2f) - 2f, center.Y - (knob / 2f) - 2f, knob + 4f, knob + 4f), style.Accent, 2f, (knob / 2f) + 2f);
        }
    }

    private void SetFromPointer(Vector2 position)
    {
        var track = new Rect(Bounds.X + 8f, Bounds.Y, Math.Max(1f, Bounds.Width - 16f), Bounds.Height);
        Value = Minimum + ((Maximum - Minimum) * Math.Clamp((position.X - track.X) / track.Width, 0f, 1f));
    }
}

/// <summary>
/// Shows <see cref="Content"/> through a clipped window that scrolls vertically with the wheel or by dragging the
/// scroll bar. The content is laid out at the view's width and its natural height.
/// </summary>
public class ScrollView : UIElement
{
    private float _offset;
    private bool _draggingBar;
    private float _dragStart, _dragOffset;

    public ScrollView()
    {
        Overflow = Overflow.Clip;
        HitTestVisible = true;
        Content = AddChild(new Panel { Name = "Content", Layout = LayoutMode.Column, Width = UIUnit.Percent(100) });
    }

    /// <summary>Add children here.</summary>
    public Panel Content { get; }

    /// <summary>Pixels scrolled from the top, 0..<see cref="MaxOffset"/>.</summary>
    public float ScrollOffset
    {
        get => _offset;
        set
        {
            var v = Math.Clamp(value, 0f, MaxOffset);
            if (v != _offset)
            {
                _offset = v;
                InvalidateLayout();
            }
        }
    }

    public float ContentHeight { get; private set; }

    public float MaxOffset => Math.Max(0f, ContentHeight - ContentBounds.Height);

    /// <summary>Logical pixels per wheel notch.</summary>
    public float WheelStep { get; set; } = 48f;

    /// <summary>Scrolls just enough to show <paramref name="rect"/> (context coordinates).</summary>
    public void ScrollIntoView(Rect rect)
    {
        var view = ContentBounds;
        if (rect.Y < view.Y)
        {
            ScrollOffset -= view.Y - rect.Y;
        }
        else if (rect.Y + rect.Height > view.Y + view.Height)
        {
            ScrollOffset += rect.Y + rect.Height - (view.Y + view.Height);
        }
    }

    protected internal override Vector2 MeasureContent(float availableWidth, float availableHeight) =>
        new(LayoutEngine.MeasureOuter(Content, availableWidth, float.PositiveInfinity).X, Math.Min(availableHeight, 200f));

    protected internal override void ArrangeChildren()
    {
        var view = ContentBounds;
        var size = LayoutEngine.Size(Content, view.Width - 10f, float.PositiveInfinity, true, false);
        ContentHeight = size.Y;
        _offset = Math.Clamp(_offset, 0f, MaxOffset);
        LayoutEngine.Arrange(Content, new Rect(view.X, view.Y - _offset, size.X, size.Y), ChildClip);
    }

    protected internal override void OnScroll(PointerEventArgs e)
    {
        if (MaxOffset > 0f)
        {
            ScrollOffset -= e.Scroll.Y * WheelStep;
            e.Handled = true;
        }
    }

    protected internal override void OnPointerDown(PointerEventArgs e)
    {
        base.OnPointerDown(e);
        if (Bar() is { } bar && e.Position.X >= bar.X - 4f)
        {
            (_draggingBar, _dragStart, _dragOffset) = (true, e.Position.Y, _offset);
            e.Handled = true;
        }
    }

    protected internal override void OnPointerDrag(PointerEventArgs e)
    {
        if (_draggingBar && Bar() is { } bar)
        {
            var travel = ContentBounds.Height - bar.Height;
            ScrollOffset = _dragOffset + ((e.Position.Y - _dragStart) / Math.Max(1f, travel) * MaxOffset);
        }
    }

    protected internal override void OnPointerUp(PointerEventArgs e)
    {
        base.OnPointerUp(e);
        _draggingBar = false;
    }

    protected internal override void PaintOver(UIPainter painter, in ResolvedStyle style)
    {
        if (Bar() is { } bar)
        {
            painter.FillRect(bar, style.Accent, 3f);
        }
    }

    /// <summary>The scroll bar thumb, or null when everything fits.</summary>
    private Rect? Bar()
    {
        var view = ContentBounds;
        if (MaxOffset <= 0f || ContentHeight <= 0f)
        {
            return null;
        }

        var height = Math.Max(20f, view.Height * view.Height / ContentHeight);
        var y = view.Y + ((view.Height - height) * (_offset / MaxOffset));
        return new Rect(view.X + view.Width - 6f, y, 6f, height);
    }
}

/// <summary>Picks one of <see cref="Options"/> from a list that opens below it (in the context's overlay).</summary>
public class Dropdown : UIElement
{
    private readonly TextBlock _block = new();
    private int _selected = -1;
    private Panel? _backdrop;

    public Dropdown() => Focusable = true;

    public List<string> Options { get; } = [];

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            var v = Math.Clamp(value, -1, Options.Count - 1);
            if (v != _selected)
            {
                _selected = v;
                InvalidateLayout();
                SelectionChanged?.Invoke(v);
            }
        }
    }

    public string? SelectedOption => _selected >= 0 && _selected < Options.Count ? Options[_selected] : null;

    /// <summary>Shown when nothing is selected.</summary>
    public string Placeholder { get; set; } = "Select...";

    public bool IsOpen => _backdrop is not null;

    public event Action<int>? SelectionChanged;

    public void Open()
    {
        if (IsOpen || Context is not { } context)
        {
            return;
        }

        // A transparent full-screen backdrop catches clicks outside the list and closes it.
        _backdrop = context.Overlay.AddChild(new Panel { Name = "DropdownBackdrop", Width = UIUnit.Percent(100), Height = UIUnit.Percent(100), HitTestVisible = true, ZIndex = 1000 });
        _backdrop.PointerPressed += (_, e) =>
        {
            if (e.Target == _backdrop)
            {
                Close();
            }
        };
        var list = _backdrop.AddChild(new DropdownList { Left = Bounds.X, Top = Bounds.Y + Bounds.Height + 2f, Width = Bounds.Width, Layout = LayoutMode.Column });
        for (var i = 0; i < Options.Count; i++)
        {
            var index = i;
            var item = list.AddChild(new DropdownItem { Text = Options[i], Width = UIUnit.Percent(100) });
            item.PointerClicked += (_, _) =>
            {
                SelectedIndex = index;
                Close();
                Focus();
            };
        }
    }

    public void Close()
    {
        _backdrop?.RemoveFromParent();
        _backdrop = null;
    }

    protected internal override void OnClick(PointerEventArgs e)
    {
        base.OnClick(e);
        if (IsOpen)
        {
            Close();
        }
        else
        {
            Open();
        }

        e.Handled = true;
    }

    protected internal override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Up:
                SelectedIndex = Math.Max(0, SelectedIndex - 1);
                e.Handled = true;
                break;
            case Key.Down:
                SelectedIndex = Math.Min(Options.Count - 1, SelectedIndex + 1);
                e.Handled = true;
                break;
            case Key.Escape when IsOpen:
                Close();
                e.Handled = true;
                break;
        }
    }

    protected internal override void OnBlur()
    {
        base.OnBlur();
        if (Context?.Pressed is not DropdownItem)
        {
            Close();
        }
    }

    protected internal override Vector2 MeasureContent(float availableWidth, float availableHeight)
    {
        var style = ResolveStyle();
        var widest = Options.Append(Placeholder).Select(o =>
        {
            _block.Get(this, o, style, float.PositiveInfinity);
            return _block.Size.X;
        }).Max();
        _block.Get(this, SelectedOption ?? Placeholder, style, float.PositiveInfinity);
        return new Vector2(widest + style.FontSize, _block.Size.Y);
    }

    protected internal override void Paint(UIPainter painter, in ResolvedStyle style)
    {
        base.Paint(painter, style);
        var content = ContentBounds;
        var layout = _block.Get(this, SelectedOption ?? Placeholder, style, float.PositiveInfinity);
        painter.PushClip(ClipBounds);
        painter.DrawText(layout, new Vector2(content.X, content.Y + ((content.Height - _block.Size.Y) / 2f)), SelectedOption is null ? style.Foreground with { A = style.Foreground.A * 0.55f } : style.Foreground);
        var arrow = style.FontSize * 0.35f;
        var cx = content.X + content.Width - arrow;
        var cy = content.Y + (content.Height / 2f);
        for (var i = 0; i < (int)arrow; i++)
        {
            // A small downward triangle from stacked bars: no line geometry needed.
            var w = (arrow - i) * 2f;
            painter.FillSolid(new Rect(cx - (w / 2f), cy - (arrow / 2f) + i, w, 1f), style.Foreground);
        }

        painter.PopClip();
    }

    private sealed class DropdownList : Panel;

    private sealed class DropdownItem : Label
    {
        public DropdownItem()
        {
            HitTestVisible = true;
        }
    }
}
