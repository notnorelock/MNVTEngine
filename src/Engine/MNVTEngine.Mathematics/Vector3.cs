using System.Globalization;
using System.Runtime.InteropServices;

namespace MNVTEngine.Mathematics;

/// <summary>
/// 3D vector in a right-handed space: +X right, +Y up, +Z towards the viewer.
/// Equality operators are exact; use <see cref="Approximately"/> for tolerance.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vector3 : IEquatable<Vector3>, IFormattable
{
    public float X;
    public float Y;
    public float Z;

    public Vector3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public Vector3(Vector2 xy, float z)
        : this(xy.X, xy.Y, z)
    {
    }

    /// <summary>From double-precision math (tools computing in double); each component is rounded to float.</summary>
    public Vector3(double x, double y, double z)
        : this((float)x, (float)y, (float)z)
    {
    }

    public static Vector3 Zero => default;
    public static Vector3 One => new(1f, 1f, 1f);
    public static Vector3 UnitX => new(1f, 0f, 0f);
    public static Vector3 UnitY => new(0f, 1f, 0f);
    public static Vector3 UnitZ => new(0f, 0f, 1f);
    public static Vector3 Up => new(0f, 1f, 0f);
    public static Vector3 Down => new(0f, -1f, 0f);
    public static Vector3 Left => new(-1f, 0f, 0f);
    public static Vector3 Right => new(1f, 0f, 0f);

    /// <summary>(0, 0, -1): the way cameras and objects face (right-handed, as in glTF).</summary>
    public static Vector3 Forward => new(0f, 0f, -1f);

    public static Vector3 Back => new(0f, 0f, 1f);

    public readonly float Magnitude => MathF.Sqrt(SqrMagnitude);
    public readonly float SqrMagnitude => (X * X) + (Y * Y) + (Z * Z);

    /// <summary>Unit-length copy, or <see cref="Zero"/> when the vector is (nearly) zero.</summary>
    public readonly Vector3 Normalized
    {
        get
        {
            var m = Magnitude;
            return m > Mathf.Epsilon ? this / m : Zero;
        }
    }

    public void Normalize() => this = Normalized;

    public static float Dot(Vector3 a, Vector3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    public readonly float Dot(Vector3 other) => Dot(this, other);

    public readonly Vector3 Cross(Vector3 other) => Cross(this, other);

    public readonly float DistanceTo(Vector3 other) => Distance(this, other);

    public readonly float DistanceToSquared(Vector3 other) => DistanceSquared(this, other);

    /// <summary>Unclamped interpolation toward <paramref name="to"/> (t outside 0–1 extrapolates).</summary>
    public readonly Vector3 LerpUnclamped(Vector3 to, double t) => new(X + ((to.X - X) * t), Y + ((to.Y - Y) * t), Z + ((to.Z - Z) * t));

    /// <summary>This vector with another Y.</summary>
    public readonly Vector3 WithY(double y) => new(X, (float)y, Z);

    /// <summary>The plan-view (X, Z) part.</summary>
    public readonly Vector2 Xz => new(X, Z);

    /// <summary>Angle in radians to another direction; π/2 when either is zero (three.js <c>angleTo</c>).</summary>
    public readonly double AngleTo(Vector3 other)
    {
        var denominator = Math.Sqrt((double)SqrMagnitude * other.SqrMagnitude);
        return denominator == 0 ? Math.PI / 2 : Math.Acos(Math.Clamp(Dot(this, other) / denominator, -1, 1));
    }

    public static Vector3 Cross(Vector3 a, Vector3 b) => new(
        (a.Y * b.Z) - (a.Z * b.Y),
        (a.Z * b.X) - (a.X * b.Z),
        (a.X * b.Y) - (a.Y * b.X));

    public static float Distance(Vector3 a, Vector3 b) => (a - b).Magnitude;
    public static float DistanceSquared(Vector3 a, Vector3 b) => (a - b).SqrMagnitude;

    public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => LerpUnclamped(a, b, Mathf.Clamp01(t));
    public static Vector3 LerpUnclamped(Vector3 a, Vector3 b, float t) => a + ((b - a) * t);

    public static Vector3 MoveTowards(Vector3 current, Vector3 target, float maxDistanceDelta)
    {
        var delta = target - current;
        var distance = delta.Magnitude;
        return distance <= maxDistanceDelta || distance == 0f ? target : current + (delta / distance * maxDistanceDelta);
    }

    public static Vector3 ClampMagnitude(Vector3 vector, float maxLength)
    {
        var sqr = vector.SqrMagnitude;
        return sqr > maxLength * maxLength ? vector / MathF.Sqrt(sqr) * maxLength : vector;
    }

    /// <summary>Projects <paramref name="vector"/> onto <paramref name="onNormal"/>; zero if the normal is zero.</summary>
    public static Vector3 Project(Vector3 vector, Vector3 onNormal)
    {
        var sqr = onNormal.SqrMagnitude;
        return sqr < Mathf.Epsilon ? Zero : onNormal * (Dot(vector, onNormal) / sqr);
    }

    public static Vector3 Reflect(Vector3 direction, Vector3 normal) => direction - (2f * Dot(direction, normal) * normal);

    /// <summary>Unsigned angle in degrees, in [0, 180].</summary>
    public static float Angle(Vector3 from, Vector3 to)
    {
        var denominator = MathF.Sqrt(from.SqrMagnitude * to.SqrMagnitude);
        if (denominator < Mathf.Epsilon)
        {
            return 0f;
        }

        return MathF.Acos(Mathf.Clamp(Dot(from, to) / denominator, -1f, 1f)) * Mathf.Rad2Deg;
    }

    public static Vector3 Min(Vector3 a, Vector3 b) => new(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Min(a.Z, b.Z));
    public static Vector3 Max(Vector3 a, Vector3 b) => new(MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y), MathF.Max(a.Z, b.Z));
    public static Vector3 Abs(Vector3 v) => new(MathF.Abs(v.X), MathF.Abs(v.Y), MathF.Abs(v.Z));

    /// <summary>Component-wise multiplication.</summary>
    public static Vector3 Scale(Vector3 a, Vector3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);

    public static bool Approximately(Vector3 a, Vector3 b, float tolerance = 1e-5f) =>
        Mathf.Approximately(a.X, b.X, tolerance) && Mathf.Approximately(a.Y, b.Y, tolerance) && Mathf.Approximately(a.Z, b.Z, tolerance);

    public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3 operator -(Vector3 v) => new(-v.X, -v.Y, -v.Z);
    public static Vector3 operator *(Vector3 v, float s) => new(v.X * s, v.Y * s, v.Z * s);
    public static Vector3 operator *(float s, Vector3 v) => new(v.X * s, v.Y * s, v.Z * s);
    public static Vector3 operator *(Vector3 a, Vector3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
    public static Vector3 operator /(Vector3 v, float s) => new(v.X / s, v.Y / s, v.Z / s);

    // Double scalars: the product is computed in double, then rounded to float.
    public static Vector3 operator *(Vector3 v, double s) => new(v.X * s, v.Y * s, v.Z * s);

    public static Vector3 operator *(double s, Vector3 v) => new(v.X * s, v.Y * s, v.Z * s);

    public static Vector3 operator /(Vector3 v, double s) => new(v.X / s, v.Y / s, v.Z / s);
    public static Vector3 operator /(Vector3 a, Vector3 b) => new(a.X / b.X, a.Y / b.Y, a.Z / b.Z);
    public static bool operator ==(Vector3 a, Vector3 b) => a.Equals(b);
    public static bool operator !=(Vector3 a, Vector3 b) => !a.Equals(b);

    public readonly void Deconstruct(out float x, out float y, out float z)
    {
        x = X;
        y = Y;
        z = Z;
    }

    public readonly bool Equals(Vector3 other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);
    public override readonly bool Equals(object? obj) => obj is Vector3 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(X, Y, Z);
    public override readonly string ToString() => ToString("G", CultureInfo.InvariantCulture);

    public readonly string ToString(string? format, IFormatProvider? formatProvider) =>
        $"({X.ToString(format, formatProvider)}, {Y.ToString(format, formatProvider)}, {Z.ToString(format, formatProvider)})";
}
