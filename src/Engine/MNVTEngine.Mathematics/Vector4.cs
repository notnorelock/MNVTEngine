using System.Globalization;
using System.Runtime.InteropServices;

namespace MNVTEngine.Mathematics;

/// <summary>4D vector, used for homogeneous coordinates and shader parameters.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vector4 : IEquatable<Vector4>, IFormattable
{
    public float X;
    public float Y;
    public float Z;
    public float W;

    public Vector4(float x, float y, float z, float w)
    {
        X = x;
        Y = y;
        Z = z;
        W = w;
    }

    public Vector4(Vector3 xyz, float w)
        : this(xyz.X, xyz.Y, xyz.Z, w)
    {
    }

    public static Vector4 Zero => default;
    public static Vector4 One => new(1f, 1f, 1f, 1f);

    public readonly float Magnitude => MathF.Sqrt(SqrMagnitude);
    public readonly float SqrMagnitude => Dot(this, this);

    /// <summary>Unit-length copy, or <see cref="Zero"/> when the vector is (nearly) zero.</summary>
    public readonly Vector4 Normalized
    {
        get
        {
            var m = Magnitude;
            return m > Mathf.Epsilon ? this / m : Zero;
        }
    }

    public void Normalize() => this = Normalized;

    public static float Dot(Vector4 a, Vector4 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z) + (a.W * b.W);

    public static Vector4 Lerp(Vector4 a, Vector4 b, float t) => LerpUnclamped(a, b, Mathf.Clamp01(t));
    public static Vector4 LerpUnclamped(Vector4 a, Vector4 b, float t) => a + ((b - a) * t);

    public static Vector4 Min(Vector4 a, Vector4 b) => new(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Min(a.Z, b.Z), MathF.Min(a.W, b.W));
    public static Vector4 Max(Vector4 a, Vector4 b) => new(MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y), MathF.Max(a.Z, b.Z), MathF.Max(a.W, b.W));

    public static bool Approximately(Vector4 a, Vector4 b, float tolerance = 1e-5f) =>
        Mathf.Approximately(a.X, b.X, tolerance) && Mathf.Approximately(a.Y, b.Y, tolerance)
        && Mathf.Approximately(a.Z, b.Z, tolerance) && Mathf.Approximately(a.W, b.W, tolerance);

    public static Vector4 operator +(Vector4 a, Vector4 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W);
    public static Vector4 operator -(Vector4 a, Vector4 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z, a.W - b.W);
    public static Vector4 operator -(Vector4 v) => new(-v.X, -v.Y, -v.Z, -v.W);
    public static Vector4 operator *(Vector4 v, float s) => new(v.X * s, v.Y * s, v.Z * s, v.W * s);
    public static Vector4 operator *(float s, Vector4 v) => v * s;
    public static Vector4 operator /(Vector4 v, float s) => new(v.X / s, v.Y / s, v.Z / s, v.W / s);
    public static bool operator ==(Vector4 a, Vector4 b) => a.Equals(b);
    public static bool operator !=(Vector4 a, Vector4 b) => !a.Equals(b);

    public static explicit operator Vector3(Vector4 v) => new(v.X, v.Y, v.Z);
    public static explicit operator Vector2(Vector4 v) => new(v.X, v.Y);

    public readonly bool Equals(Vector4 other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z) && W.Equals(other.W);
    public override readonly bool Equals(object? obj) => obj is Vector4 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(X, Y, Z, W);
    public override readonly string ToString() => ToString("G", CultureInfo.InvariantCulture);

    public readonly string ToString(string? format, IFormatProvider? formatProvider) =>
        $"({X.ToString(format, formatProvider)}, {Y.ToString(format, formatProvider)}, {Z.ToString(format, formatProvider)}, {W.ToString(format, formatProvider)})";
}
