// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

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
                if (ValueChanged is { } handler)
                {
                    RaiseEvent(nameof(ValueChanged), () => handler(v));
                }
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
/// scroll bar. The content is laid out at the view's width and its natural height. The wheel glides: each notch moves
/// a target and the view eases toward it (<see cref="Smoothing"/>; instant under <see cref="Design.ReducedMotion"/>).
/// </summary>
public class ScrollView : UIElement
{
    private float _offset, _target;
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
            _target = v;
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

    /// <summary>How fast a wheel scroll catches up with its target, per second (0: jumps at once).</summary>
    public float Smoothing { get; set; } = 16f;

    /// <summary>Where the wheel is taking the view; equals <see cref="ScrollOffset"/> once it settles.</summary>
    public float TargetOffset => _target;

    /// <summary>Moves toward <see cref="TargetOffset"/>: a smooth stop, no overshoot.</summary>
    public static float Ease(float offset, float target, float smoothing, float deltaSeconds)
    {
        var next = offset + ((target - offset) * (1f - MathF.Exp(-smoothing * deltaSeconds)));
        return MathF.Abs(target - next) < 0.25f ? target : next;
    }

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
        _target = Math.Clamp(_target, 0f, MaxOffset);
        LayoutEngine.Arrange(Content, new Rect(view.X, view.Y - _offset, size.X, size.Y), ChildClip);
    }

    protected internal override void OnScroll(PointerEventArgs e)
    {
        if (MaxOffset > 0f)
        {
            var target = Math.Clamp(_target - (e.Scroll.Y * WheelStep), 0f, MaxOffset);
            if (Smoothing <= 0f || Design.ReducedMotion)
            {
                ScrollOffset = target;
            }
            else
            {
                _target = target;
            }

            e.Handled = true;
        }
    }

    protected internal override bool Tick(float deltaSeconds)
    {
        if (_target == _offset)
        {
            return false;
        }

        _offset = Ease(_offset, _target, Smoothing, deltaSeconds);
        InvalidateLayout();
        return true;
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
            painter.FillRect(bar, style.Accent with { A = style.Accent.A * 0.55f }, 2f);
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
        return new Rect(view.X + view.Width - 5f, y, 4f, height);
    }
}

/// <summary>
/// Picks one of <see cref="Options"/> from a list in the context's overlay: below it, or above when there is more room
/// there; at most <see cref="MaxListHeight"/> tall (it scrolls), wide enough for long options (up to
/// <see cref="MaxListWidth"/>), the selected one checked. It fades and slides in; Up/Down move the selection.
/// </summary>
public class Dropdown : UIElement
{
    private readonly TextBlock _block = new();
    private int _selected = -1;
    private Panel? _backdrop;
    private ScrollView? _scroll;

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
                if (SelectionChanged is { } handler)
                {
                    RaiseEvent(nameof(SelectionChanged), () => handler(v));
                }
            }
        }
    }

    public string? SelectedOption => _selected >= 0 && _selected < Options.Count ? Options[_selected] : null;

    /// <summary>Shown when nothing is selected.</summary>
    public string Placeholder { get; set; } = "Select...";

    public bool IsOpen => _backdrop is not null;

    public event Action<int>? SelectionChanged;

    /// <summary>The open list's height limit; longer lists scroll.</summary>
    public float MaxListHeight { get; set; } = 280f;

    /// <summary>How wide the list may grow past the box to show long options whole.</summary>
    public float MaxListWidth { get; set; } = 460f;

    /// <summary>Where the list goes: its box and whether it opens upward (context coordinates).</summary>
    public static (Rect List, bool Up) Place(Rect box, Vector2 screen, float contentHeight, float contentWidth, float maxHeight, float maxWidth)
    {
        const float Gap = 4f, Margin = 8f;
        var below = screen.Y - (box.Y + box.Height) - Gap - Margin;
        var above = box.Y - Gap - Margin;
        var wanted = MathF.Min(contentHeight, maxHeight);
        var up = below < wanted && above > below;
        var height = MathF.Max(0f, MathF.Min(wanted, up ? above : below));
        var width = MathF.Min(MathF.Max(box.Width, contentWidth), MathF.Max(box.Width, maxWidth));
        var x = MathF.Max(Margin, MathF.Min(box.X, screen.X - width - Margin));
        var y = up ? box.Y - Gap - height : box.Y + box.Height + Gap;
        return (new Rect(x, y, width, height), up);
    }

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

        var list = new DropdownList { Layout = LayoutMode.Column };
        var scroll = _scroll = list.AddChild(new ScrollView { Width = UIUnit.Percent(100), Height = UIUnit.Percent(100) });
        scroll.Content.Layout = LayoutMode.Column;
        for (var i = 0; i < Options.Count; i++)
        {
            var index = i;
            var item = scroll.Content.AddChild(new DropdownItem(Options[i], i == _selected) { Width = UIUnit.Percent(100) });
            item.PointerClicked += (_, _) =>
            {
                SelectedIndex = index;
                Close();
                Focus();
            };
        }

        // Measure the items as laid out (in the context: padding, font), then place the list where it fits.
        _backdrop.AddChild(list);
        var listStyle = list.ResolveStyle();
        var chrome = listStyle.Padding.Vertical + (2 * listStyle.BorderWidth);
        var itemSizes = scroll.Content.Children.Select(c => LayoutEngine.Size(c, float.PositiveInfinity, float.PositiveInfinity, false, false)).ToList();
        var contentHeight = itemSizes.Sum(s => s.Y) + chrome;
        var contentWidth = (itemSizes.Count == 0 ? 0f : itemSizes.Max(s => s.X)) + listStyle.Padding.Horizontal + (2 * listStyle.BorderWidth) + 12f; // room for the scrollbar
        var (box, up) = Place(Bounds, context.Size, contentHeight, contentWidth, MaxListHeight, MaxListWidth);
        (list.Left, list.Top, list.Width, list.Height) = (box.X, box.Y, box.Width, box.Height);
        list.Appear(new Vector2(0f, up ? 6f : -6f), 0f, 0.16f);
        if (_selected >= 0)
        {
            // Open on the selection: scroll it into the middle once laid out.
            var before = itemSizes.Take(_selected).Sum(s => s.Y);
            scroll.ScrollOffset = MathF.Max(0f, before - ((box.Height - chrome) / 2f) + (itemSizes[_selected].Y / 2f));
        }
    }

    public void Close()
    {
        _backdrop?.RemoveFromParent();
        (_backdrop, _scroll) = (null, null);
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
                Move(Math.Max(0, SelectedIndex - 1));
                e.Handled = true;
                break;
            case Key.Down:
                Move(Math.Min(Options.Count - 1, SelectedIndex + 1));
                e.Handled = true;
                break;
            case Key.Enter or Key.Space when !IsOpen:
                Open();
                e.Handled = true;
                break;
            case Key.Enter when IsOpen:
                Close();
                e.Handled = true;
                break;
            case Key.Escape when IsOpen:
                Close();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Selects <paramref name="index"/>; with the list open, its check follows and stays in view.</summary>
    private void Move(int index)
    {
        SelectedIndex = index;
        if (_scroll is null)
        {
            return;
        }

        var items = _scroll.Content.Children.OfType<DropdownItem>().ToList();
        for (var i = 0; i < items.Count; i++)
        {
            items[i].Selected = i == _selected;
        }

        if (_selected >= 0 && _selected < items.Count)
        {
            _scroll.ScrollIntoView(items[_selected].Bounds);
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
        var arrow = style.FontSize * 0.35f;
        painter.PushClip(new Rect(Bounds.X, Bounds.Y, MathF.Max(0f, content.X + content.Width - (2.5f * arrow) - Bounds.X), Bounds.Height)); // the text stops before the arrow
        painter.DrawText(layout, new Vector2(content.X, content.Y + ((content.Height - _block.Size.Y) / 2f)), SelectedOption is null ? style.Foreground with { A = style.Foreground.A * 0.55f } : style.Foreground);
        painter.PopClip();
        painter.PushClip(Bounds); // the parent's clip is already pushed (and stays put when this one moves)
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

    /// <summary>An option: its text on one line (clipped), and a check when selected.</summary>
    private sealed class DropdownItem : UIElement
    {
        private readonly Icon _check;

        public DropdownItem(string text, bool selected)
        {
            (HitTestVisible, Layout, AlignItems, Gap, Overflow) = (true, LayoutMode.Row, Align.Center, 8f, Overflow.Clip);
            AddChild(new Label { Text = text, FlexGrow = 1f, MinWidth = 0f, HitTestVisible = false });
            _check = AddChild(new Icon { Text = Icons.Check });
            Selected = selected;
        }

        public bool Selected
        {
            set
            {
                _check.Visible = value;
                StyleClass = value ? "dropdown-selected" : "";
            }
        }
    }
}
