namespace MNVTEngine.Editor.Services;

public enum ScriptLanguage
{
    CSharp,

    /// <summary>The engine's mission language (<c>.mnvts</c>).</summary>
    Mnvts,
}

/// <summary>What a piece of code is, for colouring (the classes of VS Code's Dark+ theme).</summary>
public enum TokenClass
{
    Plain,
    Keyword,
    Control,
    Type,
    Function,
    Variable,
    String,
    Number,
    Comment,
    Preprocessor,
}

/// <summary>A coloured range of the text (offsets into the whole text, in UTF-16 chars).</summary>
public readonly record struct HighlightSpan(int Start, int Length, TokenClass Class)
{
    public int End => Start + Length;
}

/// <summary>A text split into lines, with its coloured spans (sorted, not overlapping; text between spans is plain).</summary>
public sealed class HighlightedText
{
    private readonly List<HighlightSpan> _spans;

    internal HighlightedText(string text, List<int> lineStarts, List<HighlightSpan> spans) => (Text, LineStarts, _spans) = (text, lineStarts, spans);

    public string Text { get; }

    /// <summary>Offset of each line's first char; lines end at '\n' (not included).</summary>
    public IReadOnlyList<int> LineStarts { get; }

    public int LineCount => LineStarts.Count;

    public IReadOnlyList<HighlightSpan> Spans => _spans;

    public string Line(int index)
    {
        var start = LineStarts[index];
        var end = index + 1 < LineStarts.Count ? LineStarts[index + 1] - 1 : Text.Length;
        return Text[start..Math.Max(start, end)].TrimEnd('\r');
    }

    /// <summary>
    /// A line as consecutive segments that cover it all: (start column, length, class). Spans crossing lines (block
    /// comments, verbatim strings) are clipped to the line.
    /// </summary>
    public IEnumerable<(int Column, int Length, TokenClass Class)> Segments(int index)
    {
        var start = LineStarts[index];
        var length = Line(index).Length;
        var end = start + length;
        var position = start;
        var first = FirstSpanEndingAfter(start);
        for (var i = first; i < _spans.Count && _spans[i].Start < end; i++)
        {
            var span = _spans[i];
            var from = Math.Max(span.Start, start);
            var to = Math.Min(span.End, end);
            if (from > position)
            {
                yield return (position - start, from - position, TokenClass.Plain);
            }

            if (to > from)
            {
                yield return (from - start, to - from, span.Class);
            }

            position = Math.Max(position, to);
        }

        if (position < end)
        {
            yield return (position - start, end - position, TokenClass.Plain);
        }
    }

    private int FirstSpanEndingAfter(int offset)
    {
        int low = 0, high = _spans.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (_spans[middle].End <= offset)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}

/// <summary>
/// Colours C# and <c>.mnvts</c> source the way VS Code's Dark+ theme does, with a fast lexical pass (no semantic
/// analysis): keywords and control keywords, strings (C# verbatim, interpolated and raw strings included), numbers,
/// comments, preprocessor lines, function calls (a name followed by '('), types (C#: names starting with a capital;
/// .mnvts: names before a '.', such as <c>audio.play</c>) and other names as variables.
/// </summary>
public static class SyntaxHighlighter
{
    private static readonly HashSet<string> CSharpControl = new(StringComparer.Ordinal)
    {
        "if", "else", "switch", "case", "default", "for", "foreach", "while", "do", "break", "continue", "return", "goto",
        "throw", "try", "catch", "finally", "yield", "await", "when",
    };

    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "byte", "char", "checked", "class", "const", "decimal", "delegate", "double", "enum",
        "event", "explicit", "extern", "false", "fixed", "float", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected", "public",
        "readonly", "ref", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "this", "true",
        "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "var", "async",
        "get", "set", "init", "value", "record", "nameof", "partial", "where", "global", "required", "file", "scoped", "dynamic",
        "nint", "nuint", "not", "and", "or", "with", "field", "add", "remove",
    };

    private static readonly HashSet<string> MnvtsControl = new(StringComparer.Ordinal)
    {
        "if", "else", "while", "for", "in", "break", "continue", "return", "wait", "until", "try", "catch", "throw", "spawn", "after", "every",
    };

    public static ScriptLanguage? LanguageOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => ScriptLanguage.CSharp,
        ".mnvts" => ScriptLanguage.Mnvts,
        _ => null,
    };

    public static HighlightedText Highlight(string text, ScriptLanguage language)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lineStarts.Add(i + 1);
            }
        }

        var spans = new List<HighlightSpan>();
        var csharp = language == ScriptLanguage.CSharp;
        var lineStart = true; // only whitespace so far on this line (for C# preprocessor lines)
        var i0 = 0;
        while (i0 < text.Length)
        {
            var c = text[i0];
            var next = i0 + 1 < text.Length ? text[i0 + 1] : '\0';
            if (c == '\n')
            {
                lineStart = true;
                i0++;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                i0++;
                continue;
            }

            var start = i0;
            if (c == '/' && next == '/')
            {
                i0 = LineEnd(text, i0);
                spans.Add(new(start, i0 - start, TokenClass.Comment));
            }
            else if (c == '/' && next == '*')
            {
                var close = text.IndexOf("*/", i0 + 2, StringComparison.Ordinal);
                i0 = close < 0 ? text.Length : close + 2;
                spans.Add(new(start, i0 - start, TokenClass.Comment));
            }
            else if (csharp && c == '#' && lineStart)
            {
                i0 = LineEnd(text, i0);
                spans.Add(new(start, i0 - start, TokenClass.Preprocessor));
            }
            else if (csharp && StringStart(text, i0) is { } prefix)
            {
                i0 = CSharpStringEnd(text, i0, prefix);
                spans.Add(new(start, i0 - start, TokenClass.String));
            }
            else if (c == '"')
            {
                i0 = QuotedEnd(text, i0 + 1, '"');
                spans.Add(new(start, i0 - start, TokenClass.String));
            }
            else if (csharp && c == '\'')
            {
                i0 = QuotedEnd(text, i0 + 1, '\'');
                spans.Add(new(start, i0 - start, TokenClass.String));
            }
            else if (char.IsDigit(c) || (c == '.' && char.IsDigit(next)))
            {
                while (i0 < text.Length && (char.IsLetterOrDigit(text[i0]) || text[i0] is '_' or '.') && !(text[i0] == '.' && i0 + 1 < text.Length && text[i0 + 1] == '.'))
                {
                    i0++;
                }

                spans.Add(new(start, i0 - start, TokenClass.Number));
            }
            else if (char.IsLetter(c) || c == '_' || (csharp && c == '@' && (char.IsLetter(next) || next == '_')))
            {
                i0++;
                while (i0 < text.Length && (char.IsLetterOrDigit(text[i0]) || text[i0] == '_'))
                {
                    i0++;
                }

                var word = text[start..i0];
                spans.Add(new(start, i0 - start, Classify(word, text, i0, csharp)));
            }
            else
            {
                i0++; // operators and punctuation stay plain
            }

            lineStart = false;
        }

        return new HighlightedText(text, lineStarts, spans);
    }

    private static TokenClass Classify(string word, string text, int after, bool csharp)
    {
        if (csharp ? CSharpControl.Contains(word) : MnvtsControl.Contains(word))
        {
            return TokenClass.Control;
        }

        if (csharp ? CSharpKeywords.Contains(word) : Scripting.Syntax.Lexer.IsKeyword(word))
        {
            return TokenClass.Keyword;
        }

        var following = NextNonSpace(text, after);
        if (following == '(' || (csharp && following == '<' && GenericCallFollows(text, after)))
        {
            return TokenClass.Function;
        }

        if (csharp)
        {
            return char.IsUpper(word.TrimStart('@')[0]) ? TokenClass.Type : TokenClass.Variable;
        }

        return following == '.' ? TokenClass.Type : TokenClass.Variable;
    }

    /// <summary>"Method&lt;T&gt;(": the angle brackets close and a '(' follows.</summary>
    private static bool GenericCallFollows(string text, int after)
    {
        var depth = 0;
        for (var i = after; i < text.Length && i < after + 200; i++)
        {
            var c = text[i];
            if (c == '<')
            {
                depth++;
            }
            else if (c == '>' && --depth == 0)
            {
                return NextNonSpace(text, i + 1) == '(';
            }
            else if (c is ';' or '{' or '}' or '\n' or '=' or '(' or ')')
            {
                return false;
            }
        }

        return false;
    }

    private static char NextNonSpace(string text, int from)
    {
        for (var i = from; i < text.Length; i++)
        {
            if (text[i] is not (' ' or '\t'))
            {
                return text[i];
            }
        }

        return '\0';
    }

    private static int LineEnd(string text, int from)
    {
        var end = text.IndexOf('\n', from);
        return end < 0 ? text.Length : end;
    }

    /// <summary>A quoted literal with backslash escapes, ending at the closing quote or the end of the line.</summary>
    private static int QuotedEnd(string text, int from, char quote)
    {
        for (var i = from; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
            }
            else if (text[i] == quote)
            {
                return i + 1;
            }
            else if (text[i] == '\n')
            {
                return i;
            }
        }

        return text.Length;
    }

    /// <summary>The prefix of a C# string that is not a plain "..." ($, @, $@, @$, raw """), or null.</summary>
    private static string? StringStart(string text, int i)
    {
        var end = i;
        while (end < text.Length && text[end] is '$' or '@' && end - i < 4)
        {
            end++;
        }

        if (end >= text.Length || text[end] != '"' || (end == i && !RawAt(text, i)))
        {
            return null;
        }

        return text[i..end];
    }

    private static bool RawAt(string text, int i) => i + 2 < text.Length && text[i] == '"' && text[i + 1] == '"' && text[i + 2] == '"';

    private static int CSharpStringEnd(string text, int i, string prefix)
    {
        var quote = i + prefix.Length;
        if (RawAt(text, quote))
        {
            var count = 0;
            while (quote + count < text.Length && text[quote + count] == '"')
            {
                count++;
            }

            var closing = new string('"', count);
            var close = text.IndexOf(closing, quote + count, StringComparison.Ordinal);
            return close < 0 ? text.Length : close + count;
        }

        if (prefix.Contains('@', StringComparison.Ordinal))
        {
            for (var j = quote + 1; j < text.Length; j++)
            {
                if (text[j] == '"')
                {
                    if (j + 1 < text.Length && text[j + 1] == '"')
                    {
                        j++; // "" is a quote inside a verbatim string
                        continue;
                    }

                    return j + 1;
                }
            }

            return text.Length;
        }

        return QuotedEnd(text, quote + 1, '"');
    }
}
