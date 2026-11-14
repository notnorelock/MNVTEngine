using System.Globalization;

namespace MNVTEngine.Versioning;

/// <summary>
/// The MINOR component: day and month of the build as <c>DDMM</c>. Human-readable versions keep all four digits
/// ("0301" for 3 January); numeric fields (FileVersion) cannot, so they carry 301.
/// </summary>
public static class VersionDate
{
    /// <summary>The DDMM text of <paramref name="instant"/> in <paramref name="zone"/>.</summary>
    public static string Ddmm(DateTimeOffset instant, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return TimeZoneInfo.ConvertTime(instant, zone).ToString("ddMM", CultureInfo.InvariantCulture);
    }

    /// <summary>The numeric MINOR of a DDMM text.</summary>
    /// <exception cref="FormatException">Not four digits, or not a day of that month (29 February is allowed).</exception>
    public static int Parse(string ddmm)
    {
        ArgumentNullException.ThrowIfNull(ddmm);
        if (ddmm.Length != 4 || !ddmm.All(char.IsAsciiDigit))
        {
            throw new FormatException($"\"{ddmm}\" is not a DDMM date: four digits, day then month.");
        }

        var day = int.Parse(ddmm.AsSpan(0, 2), CultureInfo.InvariantCulture);
        var month = int.Parse(ddmm.AsSpan(2, 2), CultureInfo.InvariantCulture);
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(2000, month)) // 2000: a leap year
        {
            throw new FormatException($"\"{ddmm}\" is not a date: day {day} of month {month} does not exist.");
        }

        return (day * 100) + month;
    }

    /// <summary>The four-digit text of a numeric MINOR (301 to "0301").</summary>
    /// <exception cref="FormatException">Not a DDMM date.</exception>
    public static string Text(int minor)
    {
        var text = minor.ToString("D4", CultureInfo.InvariantCulture);
        _ = Parse(text);
        return text;
    }
}
