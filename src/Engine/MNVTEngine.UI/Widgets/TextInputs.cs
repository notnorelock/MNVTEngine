using MNVTEngine.Graphics.Text;
using MNVTEngine.Mathematics;

namespace MNVTEngine.UI.Widgets;

/// <summary>
/// Shared behaviour of <see cref="TextField"/> and <see cref="TextArea"/>: keyboard editing through a
/// <see cref="TextEditor"/>; mouse caret placement, drag selection and double-click word selection; clipboard;
/// a blinking caret; selection highlight; and scrolling that keeps the caret visible. While focused it takes the
/// keyboard from gameplay.
/// </summary>
public abstract class TextInputBase : UIElement
{
    private float _blink;
    private bool _caretOn = true;

    protected TextInputBase(bool multiline)
    {
        Focusable = true;
        Editor = new TextEditor { Multiline = multiline };
        Editor.Changed += () =>
        {
            InvalidateLayout();
            TextChanged?.Invoke(Editor.Text);
        };
    }

    public TextEditor Editor { get; }

    public string Text { get => Editor.Text; set => Editor.Text = value; }

    /// <summary>Shown (dimmed) while the text is empty.</summary>
    public string Placeholder { get; set; } = "";

    public bool ReadOnly { get => Editor.ReadOnly; set => Editor.ReadOnly = value; }

    /// <summary>Limit in characters; 0 for none.</summary>
    public int MaxLength { get => Editor.MaxLength; set => Editor.MaxLength = value; }

    /// <summary>Accepts or rejects each edit (e.g. digits only).</summary>
    public Func<string, bool>? Validate { get => Editor.Validate; set => Editor.Validate = value; }

    public event Action<string>? TextChanged;

    public override bool WantsKeyboard => Context?.Focused == this;

    protected TextBlock Block { get; } = new();

    /// <summary>Scroll of the text inside the content box (logical pixels).</summary>
    protected Vector2 ScrollOffset { get; set; }

    /// <summary>The text that is laid out (masked for passwords).</summary>
    protected virtual string DisplayText => Editor.Text;

    protected abstract bool Wrap { get; }

    protected TextLayout CurrentLayout(in ResolvedStyle style) => Block.Get(this, DisplayText, style, Wrap ? ContentBounds.Width : float.PositiveInfinity, TextAlign.Left, Wrap);

    protected internal override bool Tick(float deltaSeconds)
    {
        if (Context?.Focused != this)
        {
            return false;
        }

        _blink += deltaSeconds;
        var on = _blink % 1.06f < 0.53f;
        if (on == _caretOn)
        {
            return false;
        }

        _caretOn = on;
        return true;
    }

    protected internal override void OnFocus()
    {
        base.OnFocus();
        RestartBlink();
    }

    protected internal override void OnBlur()
    {
        base.OnBlur();
        Editor.SetCaret(Editor.Caret);
    }

    protected internal override void OnPointerDown(PointerEventArgs e)
    {
        base.OnPointerDown(e);
        Editor.SetCaret(IndexAt(e.Position));
        RestartBlink();
        e.Handled = true;
    }

    protected internal override void OnPointerDrag(PointerEventArgs e)
    {
        Editor.SetCaret(IndexAt(e.Position), extendSelection: true);
        RestartBlink();
    }

    protected internal override void OnClick(PointerEventArgs e)
    {
        base.OnClick(e);
        if (e.ClickCount == 2)
        {
            Editor.SelectWord(IndexAt(e.Position));
            InvalidateVisual();
        }

        e.Handled = true;
    }

    protected internal override void OnTextInput(TextInputEventArgs e)
    {
        // One character (text element) at a time, so validation and the length limit drop only what they reject.
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(e.Text);
        var changed = false;
        while (elements.MoveNext())
        {
            changed |= Editor.Insert(elements.GetTextElement());
        }

        if (changed)
        {
            RestartBlink();
        }

        e.Handled = true;
    }

    protected internal override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var clipboard = Context?.Clipboard;
        var handled = true;
        switch (e.Key)
        {
            case Key.Left:
                Editor.MoveLeft(e.Control, e.Shift);
                break;
            case Key.Right:
                Editor.MoveRight(e.Control, e.Shift);
                break;
            case Key.Backspace:
                Editor.Backspace(e.Control);
                break;
            case Key.Delete:
                Editor.Delete(e.Control);
                break;
            case Key.A when e.Control:
                Editor.SelectAll();
                break;
            case Key.C when e.Control:
                Editor.Copy(clipboard);
                break;
            case Key.X when e.Control:
                Editor.Cut(clipboard);
                break;
            case Key.V when e.Control:
                Editor.Paste(clipboard);
                break;
            case Key.Space or Key.A or Key.C or Key.X or Key.V:
                break; // typed characters arrive as text input; keep Space from "clicking" the field
            default:
                handled = HandleKey(e);
                break;
        }

        if (handled)
        {
            e.Handled = true;
            RestartBlink();
            InvalidateVisual();
        }
    }

    /// <summary>Keys specific to the single- or multi-line widget (Home/End, Enter, Up/Down...).</summary>
    protected abstract bool HandleKey(KeyEventArgs e);

    protected internal override void Paint(UIPainter painter, in ResolvedStyle style)
    {
        base.Paint(painter, style);
        var content = ContentBounds;
        var layout = CurrentLayout(style);
        KeepCaretVisible(layout, content);
        var origin = new Vector2(content.X - ScrollOffset.X, content.Y - ScrollOffset.Y);
        var scale = Block.PixelScale;
        painter.PushClip(LayoutEngine.Intersect(ClipBounds, Inflate(content, 1f)));
        if (Editor.Text.Length == 0 && Placeholder.Length > 0)
        {
            var hint = new TextBlock().Get(this, Placeholder, style, Wrap ? content.Width : float.PositiveInfinity, TextAlign.Left, Wrap);
            painter.DrawText(hint, origin, style.Foreground with { A = style.Foreground.A * 0.45f });
        }

        if (Editor.HasSelection && Context?.Focused == this)
        {
            var selection = style.Accent with { A = 0.35f };
            for (var l = layout.LineOf(Editor.SelectionStart); l <= layout.LineOf(Editor.SelectionEnd); l++)
            {
                var line = layout.Lines[l];
                var from = Math.Max(Editor.SelectionStart, line.Start);
                var to = Math.Min(Editor.SelectionEnd, line.End);
                var x0 = layout.CaretPosition(from).Top.X / scale;
                var x1 = to > line.End || (Editor.SelectionEnd > line.End && l < layout.LineOf(Editor.SelectionEnd)) ? (line.X + line.Width) / scale + 4f : layout.CaretPosition(to).Top.X / scale;
                painter.FillSolid(new Rect(origin.X + x0, origin.Y + (line.Top / scale), Math.Max(1f, x1 - x0), line.Height / scale), selection);
            }
        }

        painter.DrawText(layout, origin, style.Foreground);
        if (Context?.Focused == this && _caretOn && !ReadOnly)
        {
            var (top, height) = layout.CaretPosition(Editor.Caret);
            painter.FillSolid(new Rect(origin.X + (top.X / scale), origin.Y + (top.Y / scale), MathF.Max(1f, 1f / Context.Scale), height / scale), style.Accent);
        }

        painter.PopClip();
    }

    /// <summary>Caret index at a pointer position (context coordinates).</summary>
    protected int IndexAt(Vector2 position)
    {
        var layout = CurrentLayout(ResolveStyle());
        var local = (position - ContentBounds.Min + ScrollOffset) * Block.PixelScale;
        return layout.HitTest(local);
    }

    /// <summary>Moves the caret one line up or down at the same x (text areas).</summary>
    protected void MoveLines(int lines, bool extend)
    {
        var layout = CurrentLayout(ResolveStyle());
        var (top, height) = layout.CaretPosition(Editor.Caret);
        var target = top + new Vector2(0f, (lines * height) + (height / 2f));
        Editor.SetCaret(target.Y < 0 ? 0 : layout.HitTest(target), extend);
    }

    /// <summary>Caret to the start or end of its visual line.</summary>
    protected void MoveToLineEdge(bool end, bool extend)
    {
        var layout = CurrentLayout(ResolveStyle());
        var line = layout.Lines[layout.LineOf(Editor.Caret)];
        Editor.SetCaret(end ? line.End : line.Start, extend);
    }

    private void RestartBlink()
    {
        _blink = 0f;
        _caretOn = true;
        InvalidateVisual();
    }

    private void KeepCaretVisible(TextLayout layout, Rect content)
    {
        var scale = Block.PixelScale;
        var (top, height) = layout.CaretPosition(Editor.Caret);
        var caret = new Vector2(top.X / scale, top.Y / scale);
        var offset = ScrollOffset;
        if (!Wrap)
        {
            offset.X = Math.Clamp(offset.X, caret.X - content.Width + 2f, caret.X);
            offset.X = Math.Max(0f, Math.Min(offset.X, Math.Max(0f, (layout.Size.X / scale) - content.Width + 2f)));
        }

        offset.Y = Math.Clamp(offset.Y, caret.Y + (height / scale) - content.Height, caret.Y);
        offset.Y = Math.Max(0f, Math.Min(offset.Y, Math.Max(0f, (layout.Size.Y / scale) - content.Height)));
        ScrollOffset = offset;
    }

    private static Rect Inflate(Rect r, float by) => new(r.X - by, r.Y - by, r.Width + (2 * by), r.Height + (2 * by));
}

/// <summary>Single-line text input: placeholder, password mode, length limit, validation, read-only, clipboard; Enter raises <see cref="Submitted"/>.</summary>
public class TextField : TextInputBase
{
    private bool _password;

    public TextField()
        : base(multiline: false)
    {
    }

    /// <summary>Shows bullets instead of the text (copy and cut are disabled).</summary>
    public bool Password { get => _password; set => SetLayout(ref _password, value); }

    public event Action<string>? Submitted;

    protected override string DisplayText => Password ? new string('•', Editor.Text.Length) : Editor.Text;

    protected override bool Wrap => false;

    protected internal override Vector2 MeasureContent(float availableWidth, float availableHeight)
    {
        var style = ResolveStyle();
        return new Vector2(160f, style.Font is { } font ? font.GetMetrics(style.FontSize).LineHeight : style.FontSize * 1.3f);
    }

    protected internal override void OnKeyDown(KeyEventArgs e)
    {
        if (Password && e.Control && e.Key is Key.C or Key.X)
        {
            e.Handled = true; // never put a password on the clipboard
            return;
        }

        base.OnKeyDown(e);
    }

    protected override bool HandleKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Home:
                Editor.SetCaret(0, e.Shift);
                return true;
            case Key.End:
                Editor.SetCaret(Editor.Text.Length, e.Shift);
                return true;
            case Key.Enter when !e.Repeat:
                Submitted?.Invoke(Editor.Text);
                return true;
            default:
                return false;
        }
    }
}

/// <summary>
/// Multi-line text input with its own layout: word wrapping, Enter for new lines, Up/Down and PageUp/PageDown by
/// visual line, Home/End per line (Ctrl for the document), wheel scrolling, and the caret kept in view.
/// </summary>
public class TextArea : TextInputBase
{
    private bool _wrap = true;

    public TextArea()
        : base(multiline: true)
    {
    }

    /// <summary>Wraps lines at the width; off scrolls horizontally instead.</summary>
    public bool WordWrap { get => _wrap; set => SetLayout(ref _wrap, value); }

    protected override bool Wrap => _wrap;

    protected internal override Vector2 MeasureContent(float availableWidth, float availableHeight)
    {
        var style = ResolveStyle();
        var line = style.Font is { } font ? font.GetMetrics(style.FontSize).LineHeight : style.FontSize * 1.3f;
        return new Vector2(240f, line * 5f);
    }

    protected internal override void OnScroll(PointerEventArgs e)
    {
        var style = ResolveStyle();
        var max = Math.Max(0f, (CurrentLayout(style).Size.Y / Block.PixelScale) - ContentBounds.Height);
        ScrollOffset = new Vector2(ScrollOffset.X, Math.Clamp(ScrollOffset.Y - (e.Scroll.Y * style.FontSize * 3f), 0f, max));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override bool HandleKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up:
                MoveLines(-1, e.Shift);
                return true;
            case Key.Down:
                MoveLines(1, e.Shift);
                return true;
            case Key.PageUp or Key.PageDown:
                var lines = Math.Max(1, (int)(ContentBounds.Height / Math.Max(1f, ResolveStyle().FontSize * 1.3f)));
                MoveLines(e.Key == Key.PageUp ? -lines : lines, e.Shift);
                return true;
            case Key.Home:
                if (e.Control)
                {
                    Editor.SetCaret(0, e.Shift);
                }
                else
                {
                    MoveToLineEdge(end: false, e.Shift);
                }

                return true;
            case Key.End:
                if (e.Control)
                {
                    Editor.SetCaret(Editor.Text.Length, e.Shift);
                }
                else
                {
                    MoveToLineEdge(end: true, e.Shift);
                }

                return true;
            case Key.Enter:
                Editor.Insert("\n");
                return true;
            default:
                return false;
        }
    }
}
