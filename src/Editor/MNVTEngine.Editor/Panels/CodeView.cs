using ImGuiNET;
using MNVTEngine.Editor.Services;
using SysVector2 = System.Numerics.Vector2;
using SysVector4 = System.Numerics.Vector4;

namespace MNVTEngine.Editor.Panels;

/// <summary>
/// Coloured code in VS Code's Dark+ colours. <see cref="Edit"/> keeps Dear ImGui's multiline text box for editing
/// (selection, clipboard, undo, IME) but makes its text transparent and draws the coloured tokens, the line numbers and
/// the caret over it. The box is sized to the whole text and scrolls inside an outer child, so the overlay never has to
/// guess the box's own scroll position.
/// </summary>
internal sealed class CodeView
{
    public static readonly SysVector4 Background = Rgb(0x1E1E1E);

    private static readonly SysVector4 GutterText = Rgb(0x858585), GutterCurrent = Rgb(0xC6C6C6), Caret = Rgb(0xAEAFAD);
    private static readonly SysVector4[] Colors =
    [
        Rgb(0xD4D4D4), // Plain
        Rgb(0x569CD6), // Keyword
        Rgb(0xC586C0), // Control
        Rgb(0x4EC9B0), // Type
        Rgb(0xDCDCAA), // Function
        Rgb(0x9CDCFE), // Variable
        Rgb(0xCE9178), // String
        Rgb(0xB5CEA8), // Number
        Rgb(0x6A9955), // Comment
        Rgb(0x9B9B9B), // Preprocessor
    ];

    private HighlightedText? _highlighted;
    private ScriptLanguage _language;
    private float _widest;
    private (int Line, int Column) _lastCaret;

    public static uint Color(TokenClass token) => ImGui.GetColorU32(Colors[(int)token]);

    public static ImFontPtr? CodeFont { get; set; }

    /// <summary>The highlighting of a file's text (cached until the text changes).</summary>
    public HighlightedText Highlight(string path, string text)
    {
        var language = SyntaxHighlighter.LanguageOf(path) ?? ScriptLanguage.CSharp; // shaders, JSON and text read well as C#-like
        if (_highlighted is null || !ReferenceEquals(_highlighted.Text, text) || _language != language)
        {
            (_highlighted, _language) = (SyntaxHighlighter.Highlight(text, language), language);
            _widest = 0f;
            for (var i = 0; i < _highlighted.LineCount; i++)
            {
                _widest = Math.Max(_widest, ImGui.CalcTextSize(_highlighted.Line(i)).X);
            }
        }

        return _highlighted;
    }

    /// <summary>A read-only line, coloured (the debugger's script view).</summary>
    public static void Line(HighlightedText highlighted, int index)
    {
        var line = highlighted.Line(index);
        var first = true;
        foreach (var (column, length, token) in highlighted.Segments(index))
        {
            if (!first)
            {
                ImGui.SameLine(0f, 0f);
            }

            ImGui.PushStyleColor(ImGuiCol.Text, Colors[(int)token]);
            ImGui.TextUnformatted(line.AsSpan(column, length));
            ImGui.PopStyleColor();
            first = false;
        }

        if (first)
        {
            ImGui.TextUnformatted("");
        }
    }

    /// <summary>The editable, coloured text box. True when the text changed.</summary>
    /// <param name="caret">The caret's line and column (1-based) as the text box's callback last reported them.</param>
    public bool Edit(string path, ref string text, SysVector2 size, ImGuiInputTextCallback callback, (int Line, int Column) caret, double time)
    {
        var font = CodeFont;
        if (font is { } pushed)
        {
            ImGui.PushFont(pushed);
        }

        var highlighted = Highlight(path, text);
        var style = ImGui.GetStyle();
        var lineHeight = ImGui.GetTextLineHeight();
        var gutter = ImGui.CalcTextSize(highlighted.LineCount.ToString(System.Globalization.CultureInfo.InvariantCulture)).X + 20f;
        var changed = false;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Background);
        if (ImGui.BeginChild("##code", size, ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar))
        {
            var visible = ImGui.GetContentRegionAvail();
            var boxSize = new SysVector2(
                Math.Max(_widest + (style.FramePadding.X * 2f) + 200f, visible.X - gutter),
                Math.Max((highlighted.LineCount + 1) * lineHeight + (style.FramePadding.Y * 2f), visible.Y));
            ImGui.SetCursorPos(new SysVector2(gutter, 0f));
            var capacity = (uint)(System.Text.Encoding.UTF8.GetByteCount(text) + (64 * 1024));
            // The box's text and background are invisible: the box is a child window drawn after this one, so the colours
            // drawn here below show through it (with its selection highlight on top).
            ImGui.PushStyleColor(ImGuiCol.Text, new SysVector4(0f, 0f, 0f, 0f));
            ImGui.PushStyleColor(ImGuiCol.FrameBg, new SysVector4(0f, 0f, 0f, 0f));
            if (ImGui.InputTextMultiline("##text", ref text, capacity, boxSize, ImGuiInputTextFlags.AllowTabInput | ImGuiInputTextFlags.CallbackAlways, callback))
            {
                changed = true;
            }

            ImGui.PopStyleColor(2);
            var active = ImGui.IsItemActive() || ImGui.IsItemFocused();
            var origin = ImGui.GetItemRectMin() + style.FramePadding;
            if (changed)
            {
                highlighted = Highlight(path, text);
            }

            var draw = ImGui.GetWindowDrawList();
            var windowPos = ImGui.GetWindowPos();
            var scroll = new SysVector2(ImGui.GetScrollX(), ImGui.GetScrollY());
            var first = Math.Max(0, (int)((scroll.Y - style.FramePadding.Y) / lineHeight));
            var last = Math.Min(highlighted.LineCount - 1, first + (int)(visible.Y / lineHeight) + 2);
            for (var i = first; i <= last; i++)
            {
                var line = highlighted.Line(i);
                var y = origin.Y + (i * lineHeight);
                foreach (var (column, length, token) in highlighted.Segments(i))
                {
                    var x = origin.X + (column == 0 ? 0f : ImGui.CalcTextSize(line[..column]).X);
                    draw.AddText(new SysVector2(x, y), Color(token), line.AsSpan(column, length).ToString());
                }
            }

            // The caret: the box's own is invisible with its text.
            if (active && caret.Line - 1 < highlighted.LineCount && (time % 1.0) < 0.6)
            {
                var line = highlighted.Line(Math.Max(0, caret.Line - 1));
                var x = origin.X + ImGui.CalcTextSize(line[..Math.Clamp(caret.Column - 1, 0, line.Length)]).X;
                var y = origin.Y + ((caret.Line - 1) * lineHeight);
                draw.AddLine(new SysVector2(x, y), new SysVector2(x, y + lineHeight), ImGui.GetColorU32(Caret), 1.5f);
            }

            // Line numbers, pinned to the left edge while scrolling sideways.
            var left = windowPos.X;
            draw.AddRectFilled(new SysVector2(left, windowPos.Y), new SysVector2(left + gutter - 6f, windowPos.Y + ImGui.GetWindowHeight()), ImGui.GetColorU32(Background));
            for (var i = first; i <= last; i++)
            {
                var number = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var width = ImGui.CalcTextSize(number).X;
                draw.AddText(new SysVector2(left + gutter - 12f - width, origin.Y + (i * lineHeight)), ImGui.GetColorU32(active && i == caret.Line - 1 ? GutterCurrent : GutterText), number);
            }

            // Keep the caret in view when it moves (typing, arrows, jumps), not while the user scrolls.
            if (active && caret != _lastCaret)
            {
                var caretY = (caret.Line - 1) * lineHeight + style.FramePadding.Y;
                if (caretY < scroll.Y)
                {
                    ImGui.SetScrollY(caretY);
                }
                else if (caretY + lineHeight > scroll.Y + visible.Y)
                {
                    ImGui.SetScrollY(caretY + lineHeight - visible.Y + style.FramePadding.Y);
                }

                var currentLine = highlighted.Line(Math.Clamp(caret.Line - 1, 0, highlighted.LineCount - 1));
                var caretX = gutter + style.FramePadding.X + ImGui.CalcTextSize(currentLine[..Math.Clamp(caret.Column - 1, 0, currentLine.Length)]).X;
                if (caretX < scroll.X + gutter)
                {
                    ImGui.SetScrollX(Math.Max(0f, caretX - gutter - 20f));
                }
                else if (caretX > scroll.X + visible.X - 20f)
                {
                    ImGui.SetScrollX(caretX - visible.X + 40f);
                }

                _lastCaret = caret;
            }
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();
        if (font is not null)
        {
            ImGui.PopFont();
        }

        return changed;
    }

    private static SysVector4 Rgb(int rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
}
