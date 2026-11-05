using MNVTEngine.Mathematics;

namespace MNVTEngine.Terrain.Geometry;

/// <summary>A 2D point or direction in double precision (the terrain tools' plan view: x and z).</summary>
public readonly record struct Vec2(double X, double Y)
{
    public static Vec2 Zero => default;

    public double Length => Math.Sqrt((X * X) + (Y * Y));

    public double LengthSquared => (X * X) + (Y * Y);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);

    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);

    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);

    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);

    public static Vec2 operator /(Vec2 a, double s) => new(a.X / s, a.Y / s);

    public double Dot(Vec2 other) => (X * other.X) + (Y * other.Y);

    /// <summary>The z of the 3D cross product (positive when <paramref name="other"/> is counter-clockwise).</summary>
    public double Cross(Vec2 other) => (X * other.Y) - (Y * other.X);

    public double DistanceTo(Vec2 other) => (other - this).Length;

    /// <summary>Unit length; zero stays zero (as three.js normalize).</summary>
    public Vec2 Normalized()
    {
        var length = Length;
        return length == 0 ? this : this / length;
    }

    public Vec2 Lerp(Vec2 to, double t) => new(X + ((to.X - X) * t), Y + ((to.Y - Y) * t));
}

/// <summary>A 3D point or direction in double precision: the terrain tools compute in doubles, as JavaScript does.</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 Zero => default;

    public static Vec3 UnitX => new(1, 0, 0);

    public static Vec3 UnitY => new(0, 1, 0);

    public static Vec3 UnitZ => new(0, 0, 1);

    public double Length => Math.Sqrt((X * X) + (Y * Y) + (Z * Z));

    public double LengthSquared => (X * X) + (Y * Y) + (Z * Z);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);

    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public static Vec3 operator *(double s, Vec3 a) => new(a.X * s, a.Y * s, a.Z * s);

    public static Vec3 operator /(Vec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    public double Dot(Vec3 other) => (X * other.X) + (Y * other.Y) + (Z * other.Z);

    public Vec3 Cross(Vec3 other) => new((Y * other.Z) - (Z * other.Y), (Z * other.X) - (X * other.Z), (X * other.Y) - (Y * other.X));

    public double DistanceTo(Vec3 other) => (other - this).Length;

    public double DistanceToSquared(Vec3 other) => (other - this).LengthSquared;

    /// <summary>Unit length; zero stays zero (as three.js normalize).</summary>
    public Vec3 Normalized()
    {
        var length = Length;
        return length == 0 ? this : this / length;
    }

    public Vec3 Lerp(Vec3 to, double t) => new(X + ((to.X - X) * t), Y + ((to.Y - Y) * t), Z + ((to.Z - Z) * t));

    public Vec3 WithY(double y) => new(X, y, Z);

    /// <summary>The plan-view (x, z) part.</summary>
    public Vec2 Xz => new(X, Z);

    /// <summary>Angle in radians between two directions (three.js angleTo).</summary>
    public double AngleTo(Vec3 other)
    {
        var denominator = Math.Sqrt(LengthSquared * other.LengthSquared);
        return denominator == 0 ? Math.PI / 2 : Math.Acos(Math.Clamp(Dot(other) / denominator, -1, 1));
    }

    public Vec3 ApplyQuaternion(Quat q)
    {
        // three.js Vector3.applyQuaternion: t = 2 * cross(q.xyz, v); v + q.w * t + cross(q.xyz, t)
        var tx = 2 * ((q.Y * Z) - (q.Z * Y));
        var ty = 2 * ((q.Z * X) - (q.X * Z));
        var tz = 2 * ((q.X * Y) - (q.Y * X));
        return new Vec3(
            X + (q.W * tx) + ((q.Y * tz) - (q.Z * ty)),
            Y + (q.W * ty) + ((q.Z * tx) - (q.X * tz)),
            Z + (q.W * tz) + ((q.X * ty) - (q.Y * tx)));
    }

    public Vector3 ToFloat() => new((float)X, (float)Y, (float)Z);

    public static Vec3 FromFloat(Vector3 v) => new(v.X, v.Y, v.Z);
}

/// <summary>A rotation in double precision (three.js Quaternion semantics).</summary>
public readonly record struct Quat(double X, double Y, double Z, double W)
{
    public static Quat Identity => new(0, 0, 0, 1);

    public static Quat FromAxisAngle(Vec3 axis, double angle)
    {
        var half = angle / 2;
        var s = Math.Sin(half);
        return new Quat(axis.X * s, axis.Y * s, axis.Z * s, Math.Cos(half));
    }

    /// <summary>The rotation turning unit vector <paramref name="from"/> onto unit vector <paramref name="to"/> (three.js setFromUnitVectors).</summary>
    public static Quat FromUnitVectors(Vec3 from, Vec3 to)
    {
        var r = from.Dot(to) + 1;
        Quat q;
        if (r < 1e-8)
        {
            q = Math.Abs(from.X) > Math.Abs(from.Z) ? new Quat(-from.Y, from.X, 0, 0) : new Quat(0, -from.Z, from.Y, 0);
        }
        else
        {
            var c = from.Cross(to);
            q = new Quat(c.X, c.Y, c.Z, r);
        }

        return q.Normalized();
    }

    public Quat Normalized()
    {
        var length = Math.Sqrt((X * X) + (Y * Y) + (Z * Z) + (W * W));
        return length == 0 ? Identity : new Quat(X / length, Y / length, Z / length, W / length);
    }

    public Quat Inverse() => new(-X, -Y, -Z, W);

    /// <summary>this * other: apply <paramref name="other"/> first.</summary>
    public static Quat operator *(Quat a, Quat b) => new(
        (a.X * b.W) + (a.W * b.X) + (a.Y * b.Z) - (a.Z * b.Y),
        (a.Y * b.W) + (a.W * b.Y) + (a.Z * b.X) - (a.X * b.Z),
        (a.Z * b.W) + (a.W * b.Z) + (a.X * b.Y) - (a.Y * b.X),
        (a.W * b.W) - (a.X * b.X) - (a.Y * b.Y) - (a.Z * b.Z));
}

/// <summary>three.js MathUtils equivalents used throughout the tools.</summary>
public static class MathUtil
{
    public const double Deg2Rad = Math.PI / 180;
    public const double Rad2Deg = 180 / Math.PI;

    public static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));

    public static double Lerp(double a, double b, double t) => a + ((b - a) * t);

    public static double DegToRad(double degrees) => degrees * Deg2Rad;

    public static double RadToDeg(double radians) => radians * Rad2Deg;

    /// <summary>JavaScript Math.round: halves round up (toward +infinity), unlike Math.Round's banker's rounding.</summary>
    public static double JsRound(double value) => Math.Floor(value + 0.5);

    public static double Hypot(double x, double y) => Math.Sqrt((x * x) + (y * y));

    public static double Smoothstep(double t) => t * t * (3 - (2 * t));
}
