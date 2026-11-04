using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using StbTrueTypeSharp;
using static StbTrueTypeSharp.StbTrueType;

namespace MNVTEngine.Graphics.Text;

/// <summary>Vertical metrics of a font at one size, in pixels. Ascent is above the baseline (positive), descent below (negative).</summary>
public readonly record struct FontMetrics(float Ascent, float Descent, float LineGap)
{
    /// <summary>Distance between baselines of consecutive lines at line height 1.</summary>
    public float LineHeight => Ascent - Descent + LineGap;
}

/// <summary>A glyph rendered to an 8-bit coverage bitmap; offsets are from the pen position on the baseline, y down.</summary>
internal readonly record struct GlyphBitmap(int Width, int Height, int OffsetX, int OffsetY, byte[] Coverage);

/// <summary>
/// One TrueType (<c>.ttf</c>, glyf outlines) or OpenType (<c>.otf</c>, CFF outlines) font face, parsed on the CPU with
/// stb_truetype: glyph lookup by Unicode code point, metrics, kerning (kern table and GPOS pair adjustment) and
/// anti-aliased rasterization. Fonts with a missing glyph defer to <see cref="Fallbacks"/>, in order. Collections
/// (<c>.ttc</c>) load their first face. WOFF/WOFF2 and bitmap-only fonts are rejected with a clear error.
/// <para>
/// Text shaping is per code point with pair kerning: correct for Latin, Latin Extended (Polish included), Greek,
/// Cyrillic and symbols; scripts that need shaping (Arabic, Indic) or ligatures are not shaped.
/// </para>
/// </summary>
public sealed unsafe class Font : IDisposable
{
    private static int _nextId;
    private readonly byte[] _data;
    private readonly GCHandle _pin;
    private readonly stbtt_fontinfo _info = new();
    private readonly Dictionary<int, int> _glyphs = [];
    private bool _disposed;

    private Font(byte[] data, string name)
    {
        _data = data;
        Name = name;
        Id = Interlocked.Increment(ref _nextId);
        var tag = data.Length >= 4 ? BinaryPrimitives.ReadUInt32BigEndian(data) : 0;
        Format = tag switch
        {
            0x00010000 or 0x74727565 => "TrueType", // 1.0, 'true'
            0x4F54544F => "OpenType (CFF)", // 'OTTO'
            0x74746366 => "TrueType collection", // 'ttcf'
            0x774F4646 or 0x774F4632 => throw new InvalidDataException($"Font '{name}' is WOFF/WOFF2 (compressed web font); convert it to .ttf or .otf."),
            _ => throw new InvalidDataException($"'{name}' is not a TrueType or OpenType font."),
        };

        _pin = GCHandle.Alloc(_data, GCHandleType.Pinned);
        var p = (byte*)_pin.AddrOfPinnedObject();
        var offset = stbtt_GetFontOffsetForIndex(p, 0);
        if (offset < 0 || stbtt_InitFont(_info, p, offset) == 0)
        {
            _pin.Free();
            throw new InvalidDataException($"Font '{name}' ({Format}) could not be parsed (damaged, or no outline glyphs).");
        }

        (FamilyName, StyleName) = ReadNames(data, offset, name);
        (Weight, Italic) = ReadStyle(data, offset);
        int ascent, descent, lineGap;
        stbtt_GetFontVMetrics(_info, &ascent, &descent, &lineGap);
        (_ascent, _descent, _lineGap) = (ascent, descent, lineGap);
        UnitsPerEm = 1f / stbtt_ScaleForMappingEmToPixels(_info, 1f);
        if (FindTable(data, offset, "GPOS") is { } gpos)
        {
            _gpos = GposKerning.Read(data, gpos.Offset);
        }
    }

    /// <summary>Unique per loaded font (glyph cache key).</summary>
    public int Id { get; }

    /// <summary>The asset path or file name the font was loaded from.</summary>
    public string Name { get; }

    /// <summary>"TrueType", "OpenType (CFF)" or "TrueType collection".</summary>
    public string Format { get; }

    /// <summary>Family from the font's name table (e.g. "Source Sans 3"); the file name when absent.</summary>
    public string FamilyName { get; }

    /// <summary>Style from the name table (e.g. "Bold Italic").</summary>
    public string StyleName { get; }

    /// <summary>OS/2 weight class: 400 regular, 500 medium, 600 semibold, 700 bold.</summary>
    public int Weight { get; }

    public bool Italic { get; }

    public float UnitsPerEm { get; }

    /// <summary>Fonts tried, in order, for code points this font has no glyph for.</summary>
    public IList<Font> Fallbacks { get; } = [];

    private readonly int _ascent, _descent, _lineGap;
    private readonly GposKerning? _gpos;

    /// <exception cref="InvalidDataException">Not a supported font file.</exception>
    public static Font Load(byte[] data, string name)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new Font([.. data], name);
    }

    public static Font LoadFile(string path) => Load(File.ReadAllBytes(path), Path.GetFileName(path));

    /// <summary>
    /// A new instance of the engine's built-in font, Source Sans 3 Regular (SIL Open Font License 1.1): Latin, Latin
    /// Extended, Greek, Cyrillic and common symbols. Used when UI or text has no font set. The caller owns it.
    /// </summary>
    public static Font LoadDefault()
    {
        using var stream = typeof(Font).Assembly.GetManifestResourceStream("MNVTEngine.DefaultFont.ttf")
            ?? throw new InvalidOperationException("The built-in font resource is missing from MNVTEngine.Graphics.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return new Font(memory.ToArray(), "SourceSans3-Regular.ttf (built-in)");
    }

    /// <summary>Pixels per font unit for a font size in pixels (the em square maps to <paramref name="pixelSize"/>, as in CSS).</summary>
    public float Scale(float pixelSize) => pixelSize / UnitsPerEm;

    public FontMetrics GetMetrics(float pixelSize)
    {
        var s = Scale(pixelSize);
        return new FontMetrics(_ascent * s, _descent * s, _lineGap * s);
    }

    /// <summary>True when this font itself (not a fallback) has a glyph for <paramref name="codePoint"/>.</summary>
    public bool HasGlyph(int codePoint) => GlyphIndex(codePoint) != 0;

    /// <summary>The font and glyph that render <paramref name="codePoint"/>: this font, else the first fallback that has it, else this font's "missing" glyph (index 0).</summary>
    public (Font Font, int Glyph) Resolve(int codePoint)
    {
        var glyph = GlyphIndex(codePoint);
        if (glyph != 0)
        {
            return (this, glyph);
        }

        var visited = new HashSet<Font> { this };
        return ResolveFallback(codePoint, visited) ?? (this, 0);
    }

    internal int GlyphIndex(int codePoint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_glyphs.TryGetValue(codePoint, out var glyph))
        {
            glyph = stbtt_FindGlyphIndex(_info, codePoint);
            _glyphs[codePoint] = glyph;
        }

        return glyph;
    }

    /// <summary>Horizontal advance in pixels.</summary>
    internal float Advance(int glyph, float pixelSize)
    {
        int advance, bearing;
        stbtt_GetGlyphHMetrics(_info, glyph, &advance, &bearing);
        return advance * Scale(pixelSize);
    }

    /// <summary>Kerning between two glyphs of this font, in pixels (usually negative).</summary>
    internal float Kerning(int left, int right, float pixelSize)
    {
        var units = stbtt_GetGlyphKernAdvance(_info, left, right); // legacy kern table, simple GPOS
        if (units == 0 && _gpos is not null)
        {
            units = _gpos.Get(left, right); // GPOS pair kerning stb skips (extension lookups)
        }

        return units * Scale(pixelSize);
    }

    internal GlyphBitmap Rasterize(int glyph, float pixelSize)
    {
        var s = Scale(pixelSize);
        int x0, y0, x1, y1;
        stbtt_GetGlyphBitmapBox(_info, glyph, s, s, &x0, &y0, &x1, &y1);
        int w = x1 - x0, h = y1 - y0;
        if (w <= 0 || h <= 0)
        {
            return new GlyphBitmap(0, 0, 0, 0, []); // space and other blank glyphs
        }

        var coverage = new byte[w * h];
        fixed (byte* output = coverage)
        {
            stbtt_MakeGlyphBitmap(_info, output, w, h, w, s, s, glyph);
        }

        return new GlyphBitmap(w, h, x0, y0, coverage);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _pin.Free();
        }
    }

    public override string ToString() => $"{FamilyName} {StyleName} ({Format}, {Name})";

    private (Font, int)? ResolveFallback(int codePoint, HashSet<Font> visited)
    {
        foreach (var fallback in Fallbacks)
        {
            if (!visited.Add(fallback))
            {
                continue;
            }

            var glyph = fallback.GlyphIndex(codePoint);
            if (glyph != 0)
            {
                return (fallback, glyph);
            }

            if (fallback.ResolveFallback(codePoint, visited) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    /// <summary>Byte offset and length of a table in the face at <paramref name="faceOffset"/>, or null.</summary>
    private static (int Offset, int Length)? FindTable(byte[] data, int faceOffset, string tag)
    {
        if (faceOffset + 12 > data.Length)
        {
            return null;
        }

        var tables = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(faceOffset + 4));
        for (var i = 0; i < tables; i++)
        {
            var record = faceOffset + 12 + (i * 16);
            if (record + 16 > data.Length)
            {
                return null;
            }

            if (Encoding.ASCII.GetString(data, record, 4) == tag)
            {
                var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 8));
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 12));
                return offset >= 0 && length >= 0 && (long)offset + length <= data.Length ? (offset, length) : null;
            }
        }

        return null;
    }

    private static (int Weight, bool Italic) ReadStyle(byte[] data, int faceOffset)
    {
        if (FindTable(data, faceOffset, "OS/2") is not { Length: >= 64 } os2)
        {
            return (400, false);
        }

        var weight = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(os2.Offset + 4));
        var selection = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(os2.Offset + 62));
        return (weight is >= 1 and <= 1000 ? weight : 400, (selection & 1) != 0);
    }

    /// <summary>Family (name ID 16, else 1) and style (17, else 2) from Windows Unicode name records.</summary>
    private static (string Family, string Style) ReadNames(byte[] data, int faceOffset, string fallbackName)
    {
        var names = new Dictionary<int, string>();
        if (FindTable(data, faceOffset, "name") is { Length: >= 6 } table)
        {
            var count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(table.Offset + 2));
            var strings = table.Offset + BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(table.Offset + 4));
            for (var i = 0; i < count; i++)
            {
                var r = table.Offset + 6 + (i * 12);
                if (r + 12 > table.Offset + table.Length)
                {
                    break;
                }

                var platform = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(r));
                var nameId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(r + 6));
                var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(r + 8));
                var offset = strings + BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(r + 10));
                if ((platform == 3 || platform == 0) && nameId is 1 or 2 or 16 or 17 && offset + length <= data.Length && !names.ContainsKey(nameId))
                {
                    names[nameId] = Encoding.BigEndianUnicode.GetString(data, offset, length);
                }
            }
        }

        var family = names.GetValueOrDefault(16) ?? names.GetValueOrDefault(1) ?? Path.GetFileNameWithoutExtension(fallbackName);
        var style = names.GetValueOrDefault(17) ?? names.GetValueOrDefault(2) ?? "Regular";
        return (family, style);
    }
}
