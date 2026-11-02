using System.Globalization;
using System.Runtime.InteropServices;

namespace MNVTEngine.Mathematics;

/// <summary>
/// RGBA color with float channels, nominally in [0, 1]. Values are not clamped so HDR colors are representable.
/// No color-space conversion is applied: values are whatever space the producer used (sRGB for authored colors).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Color : IEquatable<Color>, IFormattable
{
    public float R;
    public float G;
    public float B;
    public float A;

    public Color(float r, float g, float b, float a = 1f)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    public static Color White => new(1f, 1f, 1f);
    public static Color Black => new(0f, 0f, 0f);
    public static Color Clear => new(0f, 0f, 0f, 0f);
    public static Color Red => new(1f, 0f, 0f);
    public static Color Green => new(0f, 1f, 0f);
    public static Color Blue => new(0f, 0f, 1f);
    public static Color Yellow => new(1f, 1f, 0f);
    public static Color Cyan => new(0f, 1f, 1f);
    public static Color Magenta => new(1f, 0f, 1f);
    public static Color Gray => new(0.5f, 0.5f, 0.5f);

    public static Color FromBytes(byte r, byte g, byte b, byte a = 255) =>
        new(r / 255f, g / 255f, b / 255f, a / 255f);

    /// <summary>Packs to 0xAABBGGRR, i.e. bytes R, G, B, A in memory order on little-endian (GL_RGBA + GL_UNSIGNED_BYTE).</summary>
    public readonly uint ToRgba32()
    {
        static uint Channel(float v) => (uint)MathF.Round(Mathf.Clamp01(v) * 255f);
        return Channel(R) | (Channel(G) << 8) | (Channel(B) << 16) | (Channel(A) << 24);
    }

    /// <summary>Parses "#RGB", "#RRGGBB" or "#RRGGBBAA" (leading '#' optional).</summary>
    public static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var hex = text.AsSpan().TrimStart('#');
        if (hex.Length == 3)
        {
            Span<char> expanded = [hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]];
            return TryParseHex(new string(expanded), out color);
        }

        if ((hex.Length != 6 && hex.Length != 8)
            || !uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        if (hex.Length == 6)
        {
            value = (value << 8) | 0xFF;
        }

        color = FromBytes((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    public readonly string ToHex()
    {
        var rgba = ToRgba32();
        return string.Create(CultureInfo.InvariantCulture, $"#{rgba & 0xFF:X2}{(rgba >> 8) & 0xFF:X2}{(rgba >> 16) & 0xFF:X2}{rgba >> 24:X2}");
    }

    public static Color Lerp(Color a, Color b, float t) => LerpUnclamped(a, b, Mathf.Clamp01(t));

    public static Color LerpUnclamped(Color a, Color b, float t) =>
        new(a.R + ((b.R - a.R) * t), a.G + ((b.G - a.G) * t), a.B + ((b.B - a.B) * t), a.A + ((b.A - a.A) * t));

    public readonly Color WithAlpha(float alpha) => new(R, G, B, alpha);

    public static bool Approximately(Color a, Color b, float tolerance = 1e-5f) =>
        Mathf.Approximately(a.R, b.R, tolerance) && Mathf.Approximately(a.G, b.G, tolerance)
        && Mathf.Approximately(a.B, b.B, tolerance) && Mathf.Approximately(a.A, b.A, tolerance);

    public static Color operator +(Color a, Color b) => new(a.R + b.R, a.G + b.G, a.B + b.B, a.A + b.A);
    public static Color operator -(Color a, Color b) => new(a.R - b.R, a.G - b.G, a.B - b.B, a.A - b.A);
    public static Color operator *(Color a, Color b) => new(a.R * b.R, a.G * b.G, a.B * b.B, a.A * b.A);
    public static Color operator *(Color c, float s) => new(c.R * s, c.G * s, c.B * s, c.A * s);
    public static Color operator *(float s, Color c) => c * s;
    public static bool operator ==(Color a, Color b) => a.Equals(b);
    public static bool operator !=(Color a, Color b) => !a.Equals(b);

    public static implicit operator Vector4(Color c) => new(c.R, c.G, c.B, c.A);
    public static explicit operator Color(Vector4 v) => new(v.X, v.Y, v.Z, v.W);

    public readonly bool Equals(Color other) => R.Equals(other.R) && G.Equals(other.G) && B.Equals(other.B) && A.Equals(other.A);
    public override readonly bool Equals(object? obj) => obj is Color other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(R, G, B, A);
    public override readonly string ToString() => ToString("G", CultureInfo.InvariantCulture);

    public readonly string ToString(string? format, IFormatProvider? formatProvider) =>
        $"RGBA({R.ToString(format, formatProvider)}, {G.ToString(format, formatProvider)}, {B.ToString(format, formatProvider)}, {A.ToString(format, formatProvider)})";
}
