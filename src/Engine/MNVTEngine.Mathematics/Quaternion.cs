using System.Globalization;
using System.Runtime.InteropServices;

namespace MNVTEngine.Mathematics;

/// <summary>
/// Rotation quaternion (X, Y, Z = vector part, W = scalar part). Positive angles rotate counter-clockwise
/// when looking down the axis towards the origin (right-hand rule).
/// <c>a * b</c> applies <c>b</c> first, then <c>a</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Quaternion : IEquatable<Quaternion>, IFormattable
{
    public float X;
    public float Y;
    public float Z;
    public float W;

    public Quaternion(float x, float y, float z, float w)
    {
        X = x;
        Y = y;
        Z = z;
        W = w;
    }

    public static Quaternion Identity => new(0f, 0f, 0f, 1f);

    public readonly Quaternion Normalized
    {
        get
        {
            var m = MathF.Sqrt(Dot(this, this));
            return m > Mathf.Epsilon ? new Quaternion(X / m, Y / m, Z / m, W / m) : Identity;
        }
    }

    /// <summary>Inverse of a unit quaternion (its conjugate).</summary>
    public readonly Quaternion Inverse => new(-X, -Y, -Z, W);

    /// <summary>
    /// Euler angles in degrees, each in [0, 360). See <see cref="Euler(float, float, float)"/> for the rotation order.
    /// </summary>
    public readonly Vector3 EulerAngles
    {
        get
        {
            // Rotation matrix entries for R = Ry * Rx * Rz.
            var m12 = 2f * ((Y * Z) - (X * W));
            var sinX = Mathf.Clamp(-m12, -1f, 1f);
            float x, y, z;

            if (MathF.Abs(sinX) < 0.99999f)
            {
                x = MathF.Asin(sinX);
                y = MathF.Atan2(2f * ((X * Z) + (Y * W)), 1f - (2f * ((X * X) + (Y * Y))));
                z = MathF.Atan2(2f * ((X * Y) + (Z * W)), 1f - (2f * ((X * X) + (Z * Z))));
            }
            else
            {
                // Gimbal lock: X is +-90 degrees, fold Z into Y.
                x = MathF.CopySign(MathF.PI / 2f, sinX);
                y = MathF.Atan2(-2f * ((X * Z) - (Y * W)), 1f - (2f * ((Y * Y) + (Z * Z))));
                z = 0f;
            }

            return new Vector3(
                Mathf.Repeat(x * Mathf.Rad2Deg, 360f),
                Mathf.Repeat(y * Mathf.Rad2Deg, 360f),
                Mathf.Repeat(z * Mathf.Rad2Deg, 360f));
        }
    }

    /// <summary>Rotation of <paramref name="degrees"/> around <paramref name="axis"/> (normalized internally).</summary>
    public static Quaternion AngleAxis(float degrees, Vector3 axis)
    {
        var n = axis.Normalized;
        if (n == Vector3.Zero)
        {
            return Identity;
        }

        var (sin, cos) = MathF.SinCos(degrees * Mathf.Deg2Rad * 0.5f);
        return new Quaternion(n.X * sin, n.Y * sin, n.Z * sin, cos);
    }

    /// <summary>
    /// Rotation from Euler angles in degrees. Applied in the order Z, then X, then Y (q = Y * X * Z).
    /// In 2D only <paramref name="z"/> is used: a counter-clockwise rotation in the XY plane.
    /// </summary>
    public static Quaternion Euler(float x, float y, float z) =>
        AngleAxis(y, Vector3.UnitY) * AngleAxis(x, Vector3.UnitX) * AngleAxis(z, Vector3.UnitZ);

    public static Quaternion Euler(Vector3 degrees) => Euler(degrees.X, degrees.Y, degrees.Z);

    /// <summary>
    /// The rotation that makes an object's forward (-Z) point along <paramref name="forward"/>, with its up (+Y) as
    /// close to <paramref name="up"/> as possible. Identity for a zero direction; a direction parallel to
    /// <paramref name="up"/> uses another up axis.
    /// </summary>
    public static Quaternion LookRotation(Vector3 forward, Vector3 up)
    {
        var z = -forward.Normalized; // the object's +Z points backwards
        if (z == Vector3.Zero)
        {
            return Identity;
        }

        var x = Vector3.Cross(up, z);
        if (x.SqrMagnitude < 1e-12f)
        {
            x = Vector3.Cross(MathF.Abs(z.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX, z);
        }

        x = x.Normalized;
        var y = Vector3.Cross(z, x);
        return FromAxes(x, y, z);
    }

    public static Quaternion LookRotation(Vector3 forward) => LookRotation(forward, Vector3.Up);

    /// <summary>The shortest rotation that turns <paramref name="from"/> into <paramref name="to"/>.</summary>
    public static Quaternion FromToRotation(Vector3 from, Vector3 to)
    {
        var a = from.Normalized;
        var b = to.Normalized;
        var dot = Vector3.Dot(a, b);
        if (dot > 0.999999f)
        {
            return Identity;
        }

        if (dot < -0.999999f)
        {
            var axis = Vector3.Cross(Vector3.UnitX, a);
            return AngleAxis(180f, axis.SqrMagnitude < 1e-12f ? Vector3.Cross(Vector3.UnitY, a) : axis);
        }

        var c = Vector3.Cross(a, b);
        return new Quaternion(c.X, c.Y, c.Z, 1f + dot).Normalized;
    }

    /// <summary>The rotation whose rotated X, Y and Z axes are the given orthonormal columns.</summary>
    public static Quaternion FromAxes(Vector3 x, Vector3 y, Vector3 z)
    {
        // Shepperd's method on the rotation matrix [x y z] (columns).
        var trace = x.X + y.Y + z.Z;
        if (trace > 0f)
        {
            var s = MathF.Sqrt(trace + 1f) * 2f;
            return new Quaternion((y.Z - z.Y) / s, (z.X - x.Z) / s, (x.Y - y.X) / s, 0.25f * s).Normalized;
        }

        if (x.X > y.Y && x.X > z.Z)
        {
            var s = MathF.Sqrt(1f + x.X - y.Y - z.Z) * 2f;
            return new Quaternion(0.25f * s, (y.X + x.Y) / s, (z.X + x.Z) / s, (y.Z - z.Y) / s).Normalized;
        }

        if (y.Y > z.Z)
        {
            var s = MathF.Sqrt(1f + y.Y - x.X - z.Z) * 2f;
            return new Quaternion((y.X + x.Y) / s, 0.25f * s, (z.Y + y.Z) / s, (z.X - x.Z) / s).Normalized;
        }

        var t = MathF.Sqrt(1f + z.Z - x.X - y.Y) * 2f;
        return new Quaternion((z.X + x.Z) / t, (z.Y + y.Z) / t, 0.25f * t, (x.Y - y.X) / t).Normalized;
    }

    public static float Dot(Quaternion a, Quaternion b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z) + (a.W * b.W);

    /// <summary>Smallest angle in degrees between two rotations, in [0, 180].</summary>
    public static float Angle(Quaternion a, Quaternion b)
    {
        var dot = MathF.Min(MathF.Abs(Dot(a.Normalized, b.Normalized)), 1f);
        return dot > 1f - Mathf.Epsilon ? 0f : MathF.Acos(dot) * 2f * Mathf.Rad2Deg;
    }

    /// <summary>Normalized linear interpolation along the shortest path; <paramref name="t"/> clamped to [0, 1].</summary>
    public static Quaternion Lerp(Quaternion a, Quaternion b, float t)
    {
        t = Mathf.Clamp01(t);
        if (Dot(a, b) < 0f)
        {
            b = new Quaternion(-b.X, -b.Y, -b.Z, -b.W);
        }

        return new Quaternion(
            a.X + ((b.X - a.X) * t),
            a.Y + ((b.Y - a.Y) * t),
            a.Z + ((b.Z - a.Z) * t),
            a.W + ((b.W - a.W) * t)).Normalized;
    }

    /// <summary>Spherical interpolation along the shortest path; <paramref name="t"/> clamped to [0, 1].</summary>
    public static Quaternion Slerp(Quaternion a, Quaternion b, float t)
    {
        t = Mathf.Clamp01(t);
        var cos = Dot(a, b);
        if (cos < 0f)
        {
            cos = -cos;
            b = new Quaternion(-b.X, -b.Y, -b.Z, -b.W);
        }

        // Nearly identical rotations: sin(theta) underflows, fall back to nlerp.
        if (cos > 0.9995f)
        {
            return Lerp(a, b, t);
        }

        var theta = MathF.Acos(cos);
        var sin = MathF.Sin(theta);
        var wa = MathF.Sin((1f - t) * theta) / sin;
        var wb = MathF.Sin(t * theta) / sin;
        return new Quaternion(
            (a.X * wa) + (b.X * wb),
            (a.Y * wa) + (b.Y * wb),
            (a.Z * wa) + (b.Z * wb),
            (a.W * wa) + (b.W * wb));
    }

    /// <summary>Rotates <paramref name="from"/> towards <paramref name="to"/> by at most <paramref name="maxDegreesDelta"/>.</summary>
    public static Quaternion RotateTowards(Quaternion from, Quaternion to, float maxDegreesDelta)
    {
        var angle = Angle(from, to);
        return angle == 0f ? to : Slerp(from, to, MathF.Min(1f, maxDegreesDelta / angle));
    }

    public static bool Approximately(Quaternion a, Quaternion b, float toleranceDegrees = 1e-3f) =>
        Angle(a, b) <= toleranceDegrees;

    /// <summary>Hamilton product: the result applies <paramref name="b"/> first, then <paramref name="a"/>.</summary>
    public static Quaternion operator *(Quaternion a, Quaternion b) => new(
        (a.W * b.X) + (a.X * b.W) + (a.Y * b.Z) - (a.Z * b.Y),
        (a.W * b.Y) - (a.X * b.Z) + (a.Y * b.W) + (a.Z * b.X),
        (a.W * b.Z) + (a.X * b.Y) - (a.Y * b.X) + (a.Z * b.W),
        (a.W * b.W) - (a.X * b.X) - (a.Y * b.Y) - (a.Z * b.Z));

    /// <summary>Rotates a vector.</summary>
    public static Vector3 operator *(Quaternion q, Vector3 v)
    {
        var u = new Vector3(q.X, q.Y, q.Z);
        var t = 2f * Vector3.Cross(u, v);
        return v + (q.W * t) + Vector3.Cross(u, t);
    }

    public static bool operator ==(Quaternion a, Quaternion b) => a.Equals(b);
    public static bool operator !=(Quaternion a, Quaternion b) => !a.Equals(b);

    public readonly bool Equals(Quaternion other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z) && W.Equals(other.W);
    public override readonly bool Equals(object? obj) => obj is Quaternion other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(X, Y, Z, W);
    public override readonly string ToString() => ToString("G", CultureInfo.InvariantCulture);

    public readonly string ToString(string? format, IFormatProvider? formatProvider) =>
        $"({X.ToString(format, formatProvider)}, {Y.ToString(format, formatProvider)}, {Z.ToString(format, formatProvider)}, {W.ToString(format, formatProvider)})";
}
