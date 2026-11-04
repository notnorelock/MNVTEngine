using System.Globalization;
using System.Text;

namespace MNVTEngine.UI.Widgets;

/// <summary>
/// The editing model behind <see cref="TextField"/> and <see cref="TextArea"/>, independent of rendering: text, caret,
/// selection (anchor to caret), insertion with length and validation limits, deletion, and moves by character, word
/// or document. Positions are UTF-16 indices that never split a surrogate pair. Moves by line live in the widgets,
/// because they need the text layout.
/// </summary>
public sealed class TextEditor
{
    private string _text = "";

    public string Text
    {
        get => _text;
        set
        {
            var text = Filter(value ?? "");
            if (text != _text)
            {
                _text = text;
                Caret = Math.Min(Caret, _text.Length);
                Anchor = Math.Min(Anchor, _text.Length);
                Changed?.Invoke();
            }
        }
    }

    /// <summary>Caret position, 0..Text.Length.</summary>
    public int Caret { get; private set; }

    /// <summary>The other end of the selection; equal to <see cref="Caret"/> when nothing is selected.</summary>
    public int Anchor { get; private set; }

    public bool HasSelection => Caret != Anchor;

    public int SelectionStart => Math.Min(Caret, Anchor);

    public int SelectionEnd => Math.Max(Caret, Anchor);

    public string SelectedText => _text[SelectionStart..SelectionEnd];

    public bool Multiline { get; init; }

    /// <summary>Maximum length in characters (text elements); 0 for no limit.</summary>
    public int MaxLength { get; set; }

    public bool ReadOnly { get; set; }

    /// <summary>Accepts or rejects a proposed new text (e.g. digits only). Rejected edits change nothing.</summary>
    public Func<string, bool>? Validate { get; set; }

    /// <summary>Raised after the text changed.</summary>
    public event Action? Changed;

    public void SetCaret(int index, bool extendSelection = false)
    {
        Caret = Align(Math.Clamp(index, 0, _text.Length));
        if (!extendSelection)
        {
            Anchor = Caret;
        }
    }

    public void SelectAll()
    {
        Anchor = 0;
        Caret = _text.Length;
    }

    public void Select(int start, int end)
    {
        Anchor = Align(Math.Clamp(start, 0, _text.Length));
        Caret = Align(Math.Clamp(end, 0, _text.Length));
    }

    /// <summary>Selects the word around <paramref name="index"/> (double click).</summary>
    public void SelectWord(int index)
    {
        var start = Math.Clamp(index, 0, _text.Length);
        var end = start;
        while (start > 0 && IsWord(_text[start - 1]))
        {
            start--;
        }

        while (end < _text.Length && IsWord(_text[end]))
        {
            end++;
        }

        Select(start, end);
    }

    /// <summary>Replaces the selection with <paramref name="input"/> (typed text or paste). Returns false when nothing changed.</summary>
    public bool Insert(string input)
    {
        if (ReadOnly)
        {
            return false;
        }

        var insert = Filter(input);
        if (MaxLength > 0)
        {
            var room = MaxLength - (TextLength(_text) - TextLength(SelectedText));
            insert = Truncate(insert, Math.Max(0, room));
        }

        if (insert.Length == 0 && !HasSelection)
        {
            return false;
        }

        return Replace(SelectionStart, SelectionEnd, insert);
    }

    /// <summary>Backspace: deletes the selection, the previous character, or the previous word with <paramref name="word"/>.</summary>
    public bool Backspace(bool word = false)
    {
        if (ReadOnly)
        {
            return false;
        }

        if (HasSelection)
        {
            return Replace(SelectionStart, SelectionEnd, "");
        }

        return Caret > 0 && Replace(word ? WordLeft(Caret) : Previous(Caret), Caret, "");
    }

    /// <summary>Delete: deletes the selection, the next character, or the next word with <paramref name="word"/>.</summary>
    public bool Delete(bool word = false)
    {
        if (ReadOnly)
        {
            return false;
        }

        if (HasSelection)
        {
            return Replace(SelectionStart, SelectionEnd, "");
        }

        return Caret < _text.Length && Replace(Caret, word ? WordRight(Caret) : Next(Caret), "");
    }

    public void MoveLeft(bool word, bool extend)
    {
        if (HasSelection && !extend)
        {
            SetCaret(SelectionStart);
            return;
        }

        SetCaret(word ? WordLeft(Caret) : Previous(Caret), extend);
    }

    public void MoveRight(bool word, bool extend)
    {
        if (HasSelection && !extend)
        {
            SetCaret(SelectionEnd);
            return;
        }

        SetCaret(word ? WordRight(Caret) : Next(Caret), extend);
    }

    /// <summary>Copies the selection; returns false when nothing is selected.</summary>
    public bool Copy(IClipboard? clipboard)
    {
        if (!HasSelection || clipboard is null)
        {
            return false;
        }

        clipboard.SetText(SelectedText);
        return true;
    }

    public bool Cut(IClipboard? clipboard) => !ReadOnly && Copy(clipboard) && Replace(SelectionStart, SelectionEnd, "");

    public bool Paste(IClipboard? clipboard) => clipboard?.GetText() is { Length: > 0 } text && Insert(text);

    /// <summary>Number of user-perceived characters (text elements), as limited by <see cref="MaxLength"/>.</summary>
    public static int TextLength(string text) => new StringInfo(text).LengthInTextElements;

    private bool Replace(int start, int end, string insert)
    {
        var proposed = string.Concat(_text.AsSpan(0, start), insert, _text.AsSpan(end));
        if (Validate is { } validate && !validate(proposed))
        {
            return false;
        }

        _text = proposed;
        Caret = Anchor = start + insert.Length;
        Changed?.Invoke();
        return true;
    }

    private string Filter(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            if (c == '\n' || c == '\r')
            {
                builder.Append(Multiline ? '\n' : ' ');
            }
            else if (c == '\t' || !char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static string Truncate(string text, int elements)
    {
        if (elements <= 0)
        {
            return "";
        }

        var e = StringInfo.GetTextElementEnumerator(text);
        var count = 0;
        while (e.MoveNext())
        {
            if (++count > elements)
            {
                return text[..e.ElementIndex];
            }
        }

        return text;
    }

    private int Align(int index) => index > 0 && index < _text.Length && char.IsLowSurrogate(_text[index]) ? index - 1 : index;

    private int Previous(int index) => index <= 0 ? 0 : index >= 2 && char.IsLowSurrogate(_text[index - 1]) ? index - 2 : index - 1;

    private int Next(int index) => index >= _text.Length ? _text.Length : char.IsHighSurrogate(_text[index]) && index + 1 < _text.Length ? index + 2 : index + 1;

    private int WordLeft(int index)
    {
        var i = index;
        while (i > 0 && !IsWord(_text[i - 1]))
        {
            i--;
        }

        while (i > 0 && IsWord(_text[i - 1]))
        {
            i--;
        }

        return i;
    }

    private int WordRight(int index)
    {
        var i = index;
        while (i < _text.Length && IsWord(_text[i]))
        {
            i++;
        }

        while (i < _text.Length && !IsWord(_text[i]))
        {
            i++;
        }

        return i;
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_' || char.IsSurrogate(c);
}
