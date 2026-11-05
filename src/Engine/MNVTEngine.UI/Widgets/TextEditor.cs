using System.Globalization;
using System.Text;

namespace MNVTEngine.UI.Widgets;

/// <summary>
/// The editing model behind <see cref="TextField"/> and <see cref="TextArea"/>, independent of rendering: text, caret,
/// selection (anchor to caret), insertion with length and validation limits, deletion, and moves by character, word
/// or document. Positions are UTF-16 indices that never split a surrogate pair. Moves by line live in the widgets,
/// because they need the text layout. Edits can be undone and redone (<see cref="Undo"/>, <see cref="Redo"/>): typing
/// and deleting merge into one step per word, like other editors.
/// </summary>
public sealed class TextEditor
{
    private const int MaxUndoSteps = 100;
    private readonly List<Snapshot> _undo = [];
    private readonly List<Snapshot> _redo = [];
    private string _text = "";
    private EditKind _lastEdit;
    private int _lastEditEnd = -1;

    private enum EditKind
    {
        None,
        Typing,
        Deleting,
        Other,
    }

    /// <summary>The text and selection before an edit.</summary>
    private readonly record struct Snapshot(string Text, int Caret, int Anchor);

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Back to before the last edit step; false when there is none.</summary>
    public bool Undo() => Restore(_undo, _redo);

    /// <summary>Repeats the last undone step; false when there is none.</summary>
    public bool Redo() => Restore(_redo, _undo);

    /// <summary>Forgets the undo history (the text was replaced from outside, e.g. a new document).</summary>
    public void ClearHistory()
    {
        _undo.Clear();
        _redo.Clear();
        _lastEdit = EditKind.None;
    }

    private bool Restore(List<Snapshot> from, List<Snapshot> to)
    {
        if (from.Count == 0 || ReadOnly)
        {
            return false;
        }

        to.Add(new Snapshot(_text, Caret, Anchor));
        var snapshot = from[^1];
        from.RemoveAt(from.Count - 1);
        _text = snapshot.Text;
        (Caret, Anchor) = (Math.Min(snapshot.Caret, _text.Length), Math.Min(snapshot.Anchor, _text.Length));
        _lastEdit = EditKind.None;
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Records the state before an edit, unless the edit continues the previous one: typing right after typing (within a
    /// word), or deleting right where the last delete ended.
    /// </summary>
    private void Record(EditKind kind, int start, int end, string insert)
    {
        var continues = kind == _lastEdit && kind is EditKind.Typing or EditKind.Deleting
            && (kind == EditKind.Typing ? start == _lastEditEnd && insert.Length > 0 && !char.IsWhiteSpace(insert[0]) : end == _lastEditEnd || start == _lastEditEnd);
        if (!continues)
        {
            _undo.Add(new Snapshot(_text, Caret, Anchor));
            if (_undo.Count > MaxUndoSteps)
            {
                _undo.RemoveAt(0);
            }
        }

        _redo.Clear();
        _lastEdit = kind;
        _lastEditEnd = kind == EditKind.Deleting ? start : start + insert.Length;
    }

    public string Text
    {
        get => _text;
        set
        {
            var text = Filter(value ?? "");
            if (text != _text)
            {
                _text = text;
                ClearHistory(); // set from code (a new value), not an edit: nothing to undo into
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

        return Replace(SelectionStart, SelectionEnd, insert, HasSelection || TextLength(insert) > 1 ? EditKind.Other : EditKind.Typing);
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
            return Replace(SelectionStart, SelectionEnd, "", EditKind.Other);
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
            return Replace(SelectionStart, SelectionEnd, "", EditKind.Other);
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

    public bool Cut(IClipboard? clipboard) => !ReadOnly && Copy(clipboard) && Replace(SelectionStart, SelectionEnd, "", EditKind.Other);

    public bool Paste(IClipboard? clipboard) => clipboard?.GetText() is { Length: > 0 } text && Insert(text);

    /// <summary>Number of user-perceived characters (text elements), as limited by <see cref="MaxLength"/>.</summary>
    public static int TextLength(string text) => new StringInfo(text).LengthInTextElements;

    private bool Replace(int start, int end, string insert, EditKind kind = EditKind.Deleting)
    {
        var proposed = string.Concat(_text.AsSpan(0, start), insert, _text.AsSpan(end));
        if (Validate is { } validate && !validate(proposed))
        {
            return false;
        }

        Record(kind, start, end, insert);
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
