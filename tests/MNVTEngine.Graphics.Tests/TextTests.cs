using MNVTEngine.Graphics.Text;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics.Tests;

/// <summary>Font parsing, fallback, layout, wrapping and caret queries; CPU only.</summary>
public sealed class TextTests : IDisposable
{
    private const string Polish = "Zażółć gęślą jaźń ĄĆĘŁŃÓŚŹŻ";
    private readonly Font _ttf = Font.LoadFile(TestFonts.Path("SourceSans3-Regular.ttf"));
    private readonly Font _otf = Font.LoadFile(TestFonts.Path("SourceSans3-Regular.otf"));
    private readonly Font _bold = Font.LoadFile(TestFonts.Path("SourceSans3-Bold.ttf"));

    public void Dispose()
    {
        _ttf.Dispose();
        _otf.Dispose();
        _bold.Dispose();
    }

    [Fact]
    public void TrueTypeAndOpenTypeCff_Load_WithFamilyStyleAndWeight()
    {
        Assert.Equal(("TrueType", "Source Sans 3", 400, false), (_ttf.Format, _ttf.FamilyName, _ttf.Weight, _ttf.Italic));
        Assert.Equal(("OpenType (CFF)", "Source Sans 3"), (_otf.Format, _otf.FamilyName));
        Assert.Equal(700, _bold.Weight);
        Assert.Contains("Bold", _bold.StyleName, StringComparison.Ordinal);
        using var builtIn = Font.LoadDefault();
        Assert.Equal("Source Sans 3", builtIn.FamilyName);
    }

    [Fact]
    public void PolishAndLatinExtended_HaveGlyphs_InBothFormats()
    {
        foreach (var font in new[] { _ttf, _otf })
        {
            Assert.All(Polish.Where(c => c != ' '), c => Assert.True(font.HasGlyph(c), $"{font.Format} lacks '{c}'"));
            Assert.True(font.HasGlyph('€') && font.HasGlyph('©') && font.HasGlyph('Ω'));
        }
    }

    [Fact]
    public void Glyphs_Rasterize_FromBothOutlineFormats()
    {
        using var device = new FakeGraphicsDevice();
        using var atlas = new GlyphAtlas(device);
        foreach (var font in new[] { _ttf, _otf })
        {
            var region = atlas.GetGlyph(font, font.Resolve('ż').Glyph, 32f);
            Assert.False(region.IsEmpty);
            Assert.InRange(region.Size.Y, 18f, 40f); // ascender-height glyph with a dot at 32 px
            Assert.True(region.Offset.Y < 0); // drawn above the baseline
        }

        Assert.True(atlas.GetGlyph(_ttf, _ttf.Resolve(' ').Glyph, 32f).IsEmpty);
        var again = atlas.GetGlyph(_ttf, _ttf.Resolve('ż').Glyph, 32f);
        Assert.Equal(3, atlas.GlyphsRasterized); // cached: not rasterized twice
        Assert.Equal(again, atlas.GetGlyph(_ttf, _ttf.Resolve('ż').Glyph, 32.1f)); // quarter-pixel size buckets
    }

    [Fact]
    public void InvalidFonts_AreRejectedWithTheReason()
    {
        Assert.Contains("not a TrueType or OpenType", Assert.Throws<InvalidDataException>(() => Font.Load([1, 2, 3, 4, 5], "x.ttf")).Message, StringComparison.Ordinal);
        Assert.Contains("WOFF", Assert.Throws<InvalidDataException>(() => Font.Load("wOFF0000"u8.ToArray(), "x.woff")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingGlyphs_UseFallbacks_ThenTheMissingGlyph()
    {
        Assert.Equal((_ttf, 0), _ttf.Resolve(0x4E2D)); // no CJK in Source Sans: .notdef of the primary font
        _ttf.Fallbacks.Add(_bold);
        _bold.Fallbacks.Add(_ttf); // cycles are harmless
        Assert.Equal((_ttf, 0), _ttf.Resolve(0x4E2D));

        var symbols = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "seguisym.ttf");
        if (!File.Exists(symbols))
        {
            Assert.Skip("Segoe UI Symbol is not installed; fallback to another face is not exercised.");
        }

        using var symbolFont = Font.LoadFile(symbols);
        const int chessKing = 0x265A;
        Assert.False(_ttf.HasGlyph(chessKing));
        _ttf.Fallbacks.Insert(0, symbolFont);
        Assert.Same(symbolFont, _ttf.Resolve(chessKing).Font);
    }

    [Fact]
    public void Layout_AppliesKerningAndSpacing()
    {
        var style = new TextStyle(_ttf, 40f);
        var big = style with { Size = 200f }; // Source Sans kerns A-V by -14/1000 em (GPOS class pairs in extension lookups)
        Assert.Equal(TextLayout.Measure("A", big).X + TextLayout.Measure("V", big).X - 2.8f, TextLayout.Measure("AV", big).X, 1);
        Assert.Equal(TextLayout.Measure("abc", style).X + 30f, TextLayout.Measure("abc", style with { LetterSpacing = 10f }).X, 3);
        Assert.Equal(TextLayout.Measure("a b", style).X + 7f, TextLayout.Measure("a b", style with { WordSpacing = 7f }).X, 3);
        var metrics = _ttf.GetMetrics(40f);
        Assert.Equal(metrics.LineHeight, TextLayout.Measure("x", style).Y, 3);
        Assert.Equal(metrics.LineHeight * 3, TextLayout.Measure("x\ny\nz", style).Y, 3);
        Assert.Equal(metrics.LineHeight * 1.5f, TextLayout.Measure("x", style with { LineHeight = 1.5f }).Y, 3);
    }

    [Fact]
    public void Wrapping_BreaksAtSpaces_KeepsLinesWithinTheWidth_AndBreaksLongWords()
    {
        var style = new TextStyle(_ttf, 20f, Wrap: true);
        var layout = TextLayout.Create("The quick brown fox jumps over the lazy dog " + Polish, style, 120f);
        Assert.True(layout.Lines.Count >= 4);
        Assert.All(layout.Lines, line => Assert.True(line.Width <= 120.01f, $"line {line.Start}-{line.End} is {line.Width}"));
        Assert.All(layout.Lines.Skip(1), line => Assert.NotEqual(' ', layout.Text[line.Start])); // breaks after spaces

        var word = TextLayout.Create(new string('m', 40), style, 100f);
        Assert.True(word.Lines.Count > 1);
        Assert.Equal(40, word.Lines.Sum(l => l.End - l.Start));

        var noWrap = TextLayout.Create("The quick brown fox", style with { Wrap = false }, 50f);
        Assert.Single(noWrap.Lines);
    }

    [Fact]
    public void Alignment_OffsetsLines()
    {
        var style = new TextStyle(_ttf, 20f, TextAlign.Center, Wrap: true);
        var layout = TextLayout.Create("wide line here\nab", style, 300f);
        Assert.Equal(300f, layout.Size.X);
        Assert.Equal((300f - layout.Lines[1].Width) / 2f, layout.Lines[1].X, 3);
        var right = TextLayout.Create("ab", style with { Align = TextAlign.Right }, 300f);
        Assert.Equal(300f - right.Lines[0].Width, right.Glyphs[0].X, 1);
    }

    [Fact]
    public void Carets_AreMonotonic_AndHitTestingRoundTrips_AcrossLinesAndSurrogatePairs()
    {
        var text = "Zażółć\ngęślą 😀 jaźń";
        var layout = TextLayout.Create(text, new TextStyle(_ttf, 24f));
        Assert.Equal(2, layout.Lines.Count);
        for (var i = 1; i < 6; i++)
        {
            Assert.True(layout.CaretPosition(i).Top.X > layout.CaretPosition(i - 1).Top.X);
        }

        Assert.Equal(1, layout.LineOf(text.IndexOf('g', StringComparison.Ordinal)));
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && char.IsLowSurrogate(text[i]))
            {
                continue;
            }

            var (top, height) = layout.CaretPosition(i);
            var hit = layout.HitTest(top + new Vector2(0.1f, height / 2f));
            Assert.True(hit == i || layout.CaretPosition(hit).Top == top, $"caret {i} hit {hit}");
        }

        var emoji = text.IndexOf('\uD83D', StringComparison.Ordinal);
        Assert.NotEqual(emoji + 1, layout.HitTest(layout.CaretPosition(emoji).Top + new Vector2(1f, 5f)));
    }

    [Fact]
    public void EmptyText_HasOneEmptyLine()
    {
        var layout = TextLayout.Create("", new TextStyle(_ttf, 16f));
        var line = Assert.Single(layout.Lines);
        Assert.Equal((0, 0), (line.Start, line.End));
        Assert.Empty(layout.Glyphs);
        Assert.Equal(0, layout.HitTest(new Vector2(50, 5)));
    }
}

internal static class TestFonts
{
    public static string Path(string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "tests", "TestAssets", "Fonts", file);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Test font {file} not found under tests/TestAssets/Fonts.");
    }
}
