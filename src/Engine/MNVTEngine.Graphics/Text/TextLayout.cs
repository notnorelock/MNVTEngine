using System.Text;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics.Text;

public enum TextAlign
{
    Left,
    Center,
    Right,
}

/// <summary>How text is set. Sizes and spacings are in pixels.</summary>
/// <param name="Size">Font size in pixels (the em square, as in CSS).</param>
/// <param name="LineHeight">Multiplier of the font's natural line height.</param>
/// <param name="LetterSpacing">Added after every character.</param>
/// <param name="WordSpacing">Added to every space.</param>
public readonly record struct TextStyle(Font Font, float Size, TextAlign Align = TextAlign.Left, bool Wrap = false, float LineHeight = 1f, float LetterSpacing = 0f, float WordSpacing = 0f, int TabSize = 4);

/// <summary>A glyph placed by <see cref="TextLayout"/>: pen position on its line's baseline, relative to the layout's top-left (y down).</summary>
public readonly record struct PlacedGlyph(Font Font, int Glyph, int CharIndex, float X, float Baseline);

/// <summary>One line of a layout. <see cref="End"/> is exclusive and excludes the newline.</summary>
public readonly record struct TextLine(int Start, int End, float X, float Width, float Top, float Baseline, float Height);

/// <summary>
/// Lays out a string with one <see cref="TextStyle"/>. The layout handles:
/// <list type="bullet">
/// <item>Unicode code points (surrogate pairs included), with font fallback per code point.</item>
/// <item>Pair kerning, letter, word and tab spacing, and greedy word wrapping at spaces (long words break anywhere).</item>
/// <item>Alignment, and line height from the primary font.</item>
/// </list>
/// The result is immutable. It answers measurement (<see cref="Size"/>) and caret queries
/// (<see cref="CaretPosition"/>, <see cref="HitTest"/>) for text editing. Coordinates are pixels from the top-left, y down.
/// </summary>
public sealed class TextLayout
{
    private readonly float[] _caretX;
    private readonly int[] _lineOf;

    private TextLayout(string text, TextStyle style, float maxWidth, List<PlacedGlyph> glyphs, List<TextLine> lines, float[] caretX, int[] lineOf, Vector2 size)
    {
        Text = text;
        Style = style;
        MaxWidth = maxWidth;
        Glyphs = glyphs;
        Lines = lines;
        _caretX = caretX;
        _lineOf = lineOf;
        Size = size;
    }

    public string Text { get; }

    public TextStyle Style { get; }

    public float MaxWidth { get; }

    public IReadOnlyList<PlacedGlyph> Glyphs { get; }

    public IReadOnlyList<TextLine> Lines { get; }

    /// <summary>Width of the widest line (or <see cref="MaxWidth"/> when wrapping and aligned), and the height of all lines.</summary>
    public Vector2 Size { get; }

    public static TextLayout Create(string text, TextStyle style, float maxWidth = float.PositiveInfinity)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(style.Font);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(style.Size);
        var metrics = style.Font.GetMetrics(style.Size);
        var lineAdvance = metrics.LineHeight * Math.Max(0.1f, style.LineHeight);
        var baselineOffset = metrics.Ascent + ((lineAdvance - metrics.LineHeight) / 2f);

        // 1. Advance and glyph of every UTF-16 unit (the second half of a surrogate pair advances 0).
        var advances = new float[text.Length];
        var resolved = new (Font Font, int Glyph)[text.Length];
        var space = style.Font.Resolve(' ');
        var spaceAdvance = space.Font.Advance(space.Glyph, style.Size);
        (Font Font, int Glyph) previous = default;
        for (var i = 0; i < text.Length;)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out var units) != System.Buffers.OperationStatus.Done)
            {
                rune = Rune.ReplacementChar;
                units = 1;
            }

            var c = rune.Value;
            if (c == '\n' || c == '\r')
            {
                previous = default;
            }
            else if (c == '\t')
            {
                advances[i] = (spaceAdvance + style.WordSpacing) * style.TabSize;
                previous = default;
            }
            else
            {
                var glyph = style.Font.Resolve(c);
                var kerning = previous.Font == glyph.Font && previous.Font is not null ? glyph.Font.Kerning(previous.Glyph, glyph.Glyph, style.Size) : 0f;
                advances[i] = glyph.Font.Advance(glyph.Glyph, style.Size) + kerning + style.LetterSpacing + (c == ' ' ? style.WordSpacing : 0f);
                if (kerning != 0f && i > 0)
                {
                    // Kerning moves this glyph relative to the previous one: account for it in the previous advance.
                    advances[PreviousStart(text, i)] += kerning;
                    advances[i] -= kerning;
                }

                resolved[i] = glyph;
                previous = glyph;
            }

            i += units;
        }

        // 2. Break into lines.
        var breaks = new List<(int Start, int End, int Next)>();
        var lineStart = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] == '\n')
            {
                BreakParagraph(text, advances, lineStart, i, style.Wrap ? maxWidth : float.PositiveInfinity, breaks);
                lineStart = i + 1;
            }
        }

        // 3. Place lines and glyphs.
        var widths = breaks.Select(b => Width(text, advances, b.Start, b.End)).ToList();
        // A finite width is the alignment box when wrapping or when centering/right-aligning; otherwise the text is as wide as it is.
        var layoutWidth = float.IsFinite(maxWidth) && (style.Wrap || style.Align != TextAlign.Left) ? maxWidth : widths.DefaultIfEmpty(0f).Max();
        var glyphs = new List<PlacedGlyph>(text.Length);
        var lines = new List<TextLine>(breaks.Count);
        var caretX = new float[text.Length + 1];
        var lineOf = new int[text.Length + 1];
        for (var l = 0; l < breaks.Count; l++)
        {
            var (start, end, next) = breaks[l];
            var x = style.Align switch
            {
                TextAlign.Center => (layoutWidth - widths[l]) / 2f,
                TextAlign.Right => layoutWidth - widths[l],
                _ => 0f,
            };
            var top = l * lineAdvance;
            var baseline = top + baselineOffset;
            lines.Add(new TextLine(start, end, x, widths[l], top, baseline, lineAdvance));
            for (var i = start; i < next && i < text.Length; i++)
            {
                caretX[i] = x;
                lineOf[i] = l;
                if (i < end && resolved[i].Font is { } font && !char.IsWhiteSpace(text[i]))
                {
                    glyphs.Add(new PlacedGlyph(font, resolved[i].Glyph, i, x, baseline));
                }

                x += i < end ? advances[i] : 0f;
            }

            if (l == breaks.Count - 1)
            {
                caretX[text.Length] = x;
                lineOf[text.Length] = l;
            }
        }

        return new TextLayout(text, style, maxWidth, glyphs, lines, caretX, lineOf, new Vector2(layoutWidth, breaks.Count * lineAdvance));
    }

    /// <summary>Measures without keeping the layout.</summary>
    public static Vector2 Measure(string text, TextStyle style, float maxWidth = float.PositiveInfinity) => Create(text, style, maxWidth).Size;

    /// <summary>Index of the line holding the caret position <paramref name="index"/> (0..Text.Length).</summary>
    public int LineOf(int index) => _lineOf[Math.Clamp(index, 0, Text.Length)];

    /// <summary>Top of the caret before character <paramref name="index"/>, and its line.</summary>
    public (Vector2 Top, float Height) CaretPosition(int index)
    {
        index = Math.Clamp(index, 0, Text.Length);
        var line = Lines[_lineOf[index]];
        return (new Vector2(_caretX[index], line.Top), line.Height);
    }

    /// <summary>The caret index closest to <paramref name="point"/> (layout coordinates).</summary>
    public int HitTest(Vector2 point)
    {
        var lineIndex = Math.Clamp((int)MathF.Floor(point.Y / Math.Max(1e-3f, Lines[0].Height)), 0, Lines.Count - 1);
        var line = Lines[lineIndex];
        var best = line.Start;
        var bestDistance = float.MaxValue;
        for (var i = line.Start; i <= line.End; i++)
        {
            if (i < Text.Length && char.IsLowSurrogate(Text[i]))
            {
                continue; // never between the halves of a pair
            }

            var distance = MathF.Abs(_caretX[i] - point.X);
            if (distance < bestDistance)
            {
                (best, bestDistance) = (i, distance);
            }
        }

        return best;
    }

    private static int PreviousStart(string text, int i) => i >= 2 && char.IsLowSurrogate(text[i - 1]) ? i - 2 : i - 1;

    private static float Width(string text, float[] advances, int start, int end)
    {
        var width = 0f;
        for (var i = start; i < end; i++)
        {
            width += advances[i];
        }

        // Trailing spaces hang outside the line.
        for (var i = end - 1; i >= start && text[i] == ' '; i--)
        {
            width -= advances[i];
        }

        return width;
    }

    /// <summary>Greedy breaking of [start, end): break after spaces; a word wider than the line breaks where it overflows.</summary>
    private static void BreakParagraph(string text, float[] advances, int start, int end, float maxWidth, List<(int Start, int End, int Next)> lines)
    {
        var lineStart = start;
        var x = 0f;
        var lastBreak = -1;
        for (var i = start; i < end; i++)
        {
            if (text[i] == ' ')
            {
                x += advances[i];
                lastBreak = i + 1;
                continue;
            }

            if (x + advances[i] > maxWidth && i > lineStart)
            {
                var breakAt = lastBreak > lineStart ? lastBreak : i;
                if (char.IsLowSurrogate(text[breakAt]))
                {
                    breakAt--;
                }

                var lineEnd = breakAt;
                lines.Add((lineStart, lineEnd, breakAt));
                lineStart = breakAt;
                lastBreak = -1;
                x = 0f;
                for (var j = lineStart; j <= i; j++)
                {
                    x += advances[j];
                }

                continue;
            }

            x += advances[i];
        }

        lines.Add((lineStart, end, end + 1)); // the newline belongs to this line
    }
}
