using System.Globalization;

namespace MNVTEngine.Terrain.Geometry;

/// <summary>JavaScript semantics the port depends on: number formatting in keys, localeCompare and stable sorting.</summary>
public static class Js
{
    /// <summary>
    /// A number as JavaScript prints it, for values snapped to at most five decimals (the mesher's grid): no exponent,
    /// no trailing zeros, and negative zero as "0".
    /// </summary>
    public static string Num(double value)
    {
        if (value == 0)
        {
            return "0";
        }

        return Math.Abs(value) is >= 1e-5 and < 1e21
            ? value.ToString("0.#####", CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>String.prototype.localeCompare (ICU root collation, as V8 uses).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1309", Justification = "Emulates JavaScript localeCompare tie-breaking, which is a cultural (ICU) collation by design.")]
    public static int LocaleCompare(string a, string b) => string.Compare(a, b, CultureInfo.InvariantCulture, CompareOptions.None);

    /// <summary>Array.prototype.sort: a stable sort by a comparison.</summary>
    public static void StableSort<T>(List<T> list, Comparison<T> comparison)
    {
        ArgumentNullException.ThrowIfNull(list);
        var sorted = list.Select((item, index) => (item, index)).ToList();
        sorted.Sort((x, y) =>
        {
            var c = comparison(x.item, y.item);
            return c != 0 ? c : x.index.CompareTo(y.index);
        });
        for (var i = 0; i < list.Count; i++)
        {
            list[i] = sorted[i].item;
        }
    }

    /// <summary>A numeric comparator result (a - b) as a sign, NaN as equal.</summary>
    public static int Sign(double difference) => difference > 0 ? 1 : difference < 0 ? -1 : 0;
}
