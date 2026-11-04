using System.Buffers.Binary;

namespace MNVTEngine.Graphics.Text;

/// <summary>
/// Pair kerning from an OpenType GPOS table: the lookups of every <c>kern</c> feature, pair adjustment (lookup type 2)
/// formats 1 (glyph pairs) and 2 (class pairs), also when wrapped in extension lookups (type 9), which stb_truetype
/// skips and most current fonts use. Only the first glyph's X advance is read, as horizontal kerning needs. Values are
/// in font units; results are cached per pair.
/// </summary>
internal sealed class GposKerning
{
    private readonly byte[] _data;
    private readonly List<int> _subtables = [];
    private readonly Dictionary<(int, int), short> _cache = [];

    private GposKerning(byte[] data) => _data = data;

    /// <returns>Null when the font has no GPOS kern feature.</returns>
    public static GposKerning? Read(byte[] data, int gposOffset)
    {
        var kerning = new GposKerning(data);
        try
        {
            var features = gposOffset + kerning.U16(gposOffset + 6);
            var lookups = gposOffset + kerning.U16(gposOffset + 8);
            var indices = new SortedSet<int>();
            for (int i = 0, count = kerning.U16(features); i < count; i++)
            {
                var record = features + 2 + (i * 6);
                if (System.Text.Encoding.ASCII.GetString(data, record, 4) == "kern")
                {
                    var feature = features + kerning.U16(record + 4);
                    for (int j = 0, n = kerning.U16(feature + 2); j < n; j++)
                    {
                        indices.Add(kerning.U16(feature + 4 + (j * 2)));
                    }
                }
            }

            foreach (var index in indices)
            {
                var lookup = lookups + kerning.U16(lookups + 2 + (index * 2));
                var type = kerning.U16(lookup);
                for (int s = 0, n = kerning.U16(lookup + 4); s < n; s++)
                {
                    var subtable = lookup + kerning.U16(lookup + 6 + (s * 2));
                    if (type == 9 && kerning.U16(subtable + 2) == 2)
                    {
                        kerning._subtables.Add(subtable + (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(subtable + 4)));
                    }
                    else if (type == 2)
                    {
                        kerning._subtables.Add(subtable);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new InvalidDataException("The font's GPOS table is damaged.", ex);
        }

        return kerning._subtables.Count > 0 ? kerning : null;
    }

    /// <summary>X advance adjustment between two glyphs, in font units (0 when not kerned).</summary>
    public int Get(int left, int right)
    {
        if (_cache.TryGetValue((left, right), out var cached))
        {
            return cached;
        }

        var value = 0;
        foreach (var subtable in _subtables)
        {
            if (TryPair(subtable, left, right, out value))
            {
                break; // the first subtable that covers the pair decides
            }
        }

        _cache[(left, right)] = (short)value;
        return value;
    }

    private bool TryPair(int subtable, int left, int right, out int value)
    {
        value = 0;
        var format = U16(subtable);
        var coverage = Coverage(subtable + U16(subtable + 2), left);
        if (coverage < 0)
        {
            return false;
        }

        var format1 = U16(subtable + 4);
        var format2 = U16(subtable + 6);
        var size1 = 2 * System.Numerics.BitOperations.PopCount((uint)format1);
        var size2 = 2 * System.Numerics.BitOperations.PopCount((uint)format2);
        var advanceOffset = (format1 & 0x4) != 0 ? 2 * System.Numerics.BitOperations.PopCount((uint)(format1 & 0x3)) : -1;
        if (format == 1)
        {
            var set = subtable + U16(subtable + 10 + (coverage * 2));
            int lo = 0, hi = U16(set) - 1;
            var record = 2 + size1 + size2;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                var at = set + 2 + (mid * record);
                var second = U16(at);
                if (second == right)
                {
                    value = advanceOffset < 0 ? 0 : S16(at + 2 + advanceOffset);
                    return true;
                }

                (lo, hi) = second < right ? (mid + 1, hi) : (lo, mid - 1);
            }

            return false;
        }

        if (format == 2)
        {
            var class1 = ClassOf(subtable + U16(subtable + 8), left);
            var class2 = ClassOf(subtable + U16(subtable + 10), right);
            var class2Count = U16(subtable + 14);
            var at = subtable + 16 + (((class1 * class2Count) + class2) * (size1 + size2));
            value = advanceOffset < 0 ? 0 : S16(at + advanceOffset);
            return true;
        }

        return false;
    }

    private int Coverage(int table, int glyph)
    {
        var format = U16(table);
        if (format == 1)
        {
            int lo = 0, hi = U16(table + 2) - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                var g = U16(table + 4 + (mid * 2));
                if (g == glyph)
                {
                    return mid;
                }

                (lo, hi) = g < glyph ? (mid + 1, hi) : (lo, mid - 1);
            }
        }
        else if (format == 2)
        {
            for (int i = 0, n = U16(table + 2); i < n; i++)
            {
                var range = table + 4 + (i * 6);
                if (glyph >= U16(range) && glyph <= U16(range + 2))
                {
                    return U16(range + 4) + glyph - U16(range);
                }
            }
        }

        return -1;
    }

    private int ClassOf(int table, int glyph)
    {
        var format = U16(table);
        if (format == 1)
        {
            var start = U16(table + 2);
            var count = U16(table + 4);
            return glyph >= start && glyph < start + count ? U16(table + 6 + ((glyph - start) * 2)) : 0;
        }

        if (format == 2)
        {
            for (int i = 0, n = U16(table + 2); i < n; i++)
            {
                var range = table + 4 + (i * 6);
                if (glyph >= U16(range) && glyph <= U16(range + 2))
                {
                    return U16(range + 4);
                }
            }
        }

        return 0;
    }

    private int U16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(offset, 2));

    private short S16(int offset) => BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(offset, 2));
}
