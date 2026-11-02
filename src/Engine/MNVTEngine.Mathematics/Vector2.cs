using System.Globalization;
using System.Runtime.InteropServices;

namespace MNVTEngine.Mathematics;

/// <summary>
/// 2D vector. +X is right, +Y is up. Equality operators are exact; use <see cref="Approximately"/> for tolerance.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vector2 : IEquatable<Vector2>, IFormattable
{
    public float X;
    public float Y;

    public Vector2(float x, float y)
    {
        X = x;
        Y = y;
    }

    public static Vector2 Zero => default;
    public static Vector2 One => new(1f, 1f);
    public static Vector2 UnitX => new(1f, 0f);
    public static Vector2 UnitY => new(0f, 1f);
    public static Vector2 Up => new(0f, 1f);
    public static Vector2 Down => new(0f, -1f);
    public static Vector2 Left => new(-1f, 0f);
    public static Vector2 Right => new(1f, 0f);

    public readonly float Magnitude => MathF.Sqrt(SqrMagnitude);
    public readonly float SqrMagnitude => (X * X) + (Y * Y);

    /// <summary>Unit-length copy, or <see cref="Zero"/> when the vector is (nearly) zero.</summary>
    public readonly Vector2 Normalized
    {
        get
        {
            var m = Magnitude;
            return m > Mathf.Epsilon ? this / m : Zero;
        }
    }

    /// <summary>Counter-clockwise perpendicular (rotated +90 degrees).</summary>
    public readonly Vector2 Perpendicular => new(-Y, X);

    public void Normalize() => this = Normalized;

    public static float Dot(Vector2 a, Vector2 b) => (a.X * b.X) + (a.Y * b.Y);

    /// <summary>Z component of the 3D cross product; positive when b is counter-clockwise from a.</summary>
    public static float Cross(Vector2 a, Vector2 b) => (a.X * b.Y) - (a.Y * b.X);

    public static float Distance(Vector2 a, Vector2 b) => (a - b).Magnitude;
    public static float DistanceSquared(Vector2 a, Vector2 b) => (a - b).SqrMagnitude;

    public static Vector2 Lerp(Vector2 a, Vector2 b, float t) => LerpUnclamped(a, b, Mathf.Clamp01(t));
    public static Vector2 LerpUnclamped(Vector2 a, Vector2 b, float t) => new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));

    public static Vector2 MoveTowards(Vector2 current, Vector2 target, float maxDistanceDelta)
    {
        var delta = target - current;
        var distance = delta.Magnitude;
        return distance <= maxDistanceDelta || distance == 0f ? target : current + (delta / distance * maxDistanceDelta);
    }

    public static Vector2 ClampMagnitude(Vector2 vector, float maxLength)
    {
        var sqr = vector.SqrMagnitude;
        return sqr > maxLength * maxLength ? vector / MathF.Sqrt(sqr) * maxLength : vector;
    }

    /// <summary>Reflects <paramref name="direction"/> off a surface with the given unit <paramref name="normal"/>.</summary>
    public static Vector2 Reflect(Vector2 direction, Vector2 normal) => direction - (2f * Dot(direction, normal) * normal);

    /// <summary>Unsigned angle in degrees, in [0, 180].</summary>
    public static float Angle(Vector2 from, Vector2 to)
    {
        var denominator = MathF.Sqrt(from.SqrMagnitude * to.SqrMagnitude);
        if (denominator < Mathf.Epsilon)
        {
            return 0f;
        }

        return MathF.Acos(Mathf.Clamp(Dot(from, to) / denominator, -1f, 1f)) * Mathf.Rad2Deg;
    }

    /// <summary>Signed angle in degrees, positive counter-clockwise, in [-180, 180].</summary>
    public static float SignedAngle(Vector2 from, Vector2 to) =>
        MathF.Atan2(Cross(from, to), Dot(from, to)) * Mathf.Rad2Deg;

    /// <summary>Rotates counter-clockwise by <paramref name="degrees"/>.</summary>
    public static Vector2 Rotate(Vector2 vector, float degrees)
    {
        var (sin, cos) = MathF.SinCos(degrees * Mathf.Deg2Rad);
        return new Vector2((vector.X * cos) - (vector.Y * sin), (vector.X * sin) + (vector.Y * cos));
    }

    public static Vector2 Min(Vector2 a, Vector2 b) => new(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y));
    public static Vector2 Max(Vector2 a, Vector2 b) => new(MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
    public static Vector2 Abs(Vector2 v) => new(MathF.Abs(v.X), MathF.Abs(v.Y));

    /// <summary>Component-wise multiplication.</summary>
    public static Vector2 Scale(Vector2 a, Vector2 b) => new(a.X * b.X, a.Y * b.Y);

    public static bool Approximately(Vector2 a, Vector2 b, float tolerance = 1e-5f) =>
        Mathf.Approximately(a.X, b.X, tolerance) && Mathf.Approximately(a.Y, b.Y, tolerance);

    public static Vector2 operator +(Vector2 a, Vector2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vector2 operator -(Vector2 a, Vector2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vector2 operator -(Vector2 v) => new(-v.X, -v.Y);
    public static Vector2 operator *(Vector2 v, float s) => new(v.X * s, v.Y * s);
    public static Vector2 operator *(float s, Vector2 v) => new(v.X * s, v.Y * s);
    public static Vector2 operator *(Vector2 a, Vector2 b) => new(a.X * b.X, a.Y * b.Y);
    public static Vector2 operator /(Vector2 v, float s) => new(v.X / s, v.Y / s);
    public static Vector2 operator /(Vector2 a, Vector2 b) => new(a.X / b.X, a.Y / b.Y);
    public static bool operator ==(Vector2 a, Vector2 b) => a.Equals(b);
    public static bool operator !=(Vector2 a, Vector2 b) => !a.Equals(b);

    public static implicit operator Vector3(Vector2 v) => new(v.X, v.Y, 0f);
    public static explicit operator Vector2(Vector3 v) => new(v.X, v.Y);

    public readonly void Deconstruct(out float x, out float y)
    {
        x = X;
        y = Y;
    }

    public readonly bool Equals(Vector2 other) => X.Equals(other.X) && Y.Equals(other.Y);
    public override readonly bool Equals(object? obj) => obj is Vector2 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(X, Y);
    public override readonly string ToString() => ToString("G", CultureInfo.InvariantCulture);

    public readonly string ToString(string? format, IFormatProvider? formatProvider) =>
        $"({X.ToString(format, formatProvider)}, {Y.ToString(format, formatProvider)})";
}
