using System.Globalization;
using System.Runtime.InteropServices;

namespace MNVTEngine.Mathematics;

/// <summary>
/// 4x4 float matrix using the column-vector convention: <c>v' = M * v</c>, and <c>A * B</c> applies B first.
/// <c>Mrc</c> is row r, column c. Fields are laid out column-major, the layout of a GLSL <c>mat4</c> (std140), so the
/// struct is copied into uniform blocks as is. Translation lives in column 3 (M03, M13, M23).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Matrix4x4 : IEquatable<Matrix4x4>, IFormattable
{
    // Column 0
    public float M00, M10, M20, M30;

    // Column 1
    public float M01, M11, M21, M31;

    // Column 2
    public float M02, M12, M22, M32;

    // Column 3
    public float M03, M13, M23, M33;

    public static Matrix4x4 Identity => new() { M00 = 1f, M11 = 1f, M22 = 1f, M33 = 1f };
    public static Matrix4x4 Zero => default;

    /// <summary>Element at <paramref name="row"/>, <paramref name="column"/> (both 0-based).</summary>
    public float this[int row, int column]
    {
        readonly get => AsSpan(in this)[Index(row, column)];
        set => MemoryMarshal.CreateSpan(ref M00, 16)[Index(row, column)] = value;
    }

    public readonly Vector3 Translation => new(M03, M13, M23);

    public readonly Matrix4x4 Transposed => new()
    {
        M00 = M00, M01 = M10, M02 = M20, M03 = M30,
        M10 = M01, M11 = M11, M12 = M21, M13 = M31,
        M20 = M02, M21 = M12, M22 = M22, M23 = M32,
        M30 = M03, M31 = M13, M32 = M23, M33 = M33,
    };

    public readonly float Determinant => ToNumerics(this).GetDeterminant();

    /// <summary>Returns false (and <paramref name="result"/> = NaN matrix) when the matrix is singular.</summary>
    public static bool TryInvert(Matrix4x4 matrix, out Matrix4x4 result)
    {
        // Inversion is convention-independent, so the SIMD System.Numerics routine can be reused element-wise.
        var ok = System.Numerics.Matrix4x4.Invert(ToNumerics(matrix), out var inverse);
        result = FromNumerics(inverse);
        return ok;
    }

    /// <exception cref="InvalidOperationException">The matrix is singular.</exception>
    public readonly Matrix4x4 Inverse =>
        TryInvert(this, out var result) ? result : throw new InvalidOperationException("Matrix is singular and cannot be inverted.");

    public static Matrix4x4 Translate(Vector3 t) => new()
    {
        M00 = 1f, M11 = 1f, M22 = 1f, M33 = 1f,
        M03 = t.X, M13 = t.Y, M23 = t.Z,
    };

    public static Matrix4x4 Scale(Vector3 s) => new() { M00 = s.X, M11 = s.Y, M22 = s.Z, M33 = 1f };

    public static Matrix4x4 Rotate(Quaternion q)
    {
        float xx = q.X * q.X, yy = q.Y * q.Y, zz = q.Z * q.Z;
        float xy = q.X * q.Y, xz = q.X * q.Z, yz = q.Y * q.Z;
        float wx = q.W * q.X, wy = q.W * q.Y, wz = q.W * q.Z;
        return new Matrix4x4
        {
            M00 = 1f - (2f * (yy + zz)), M01 = 2f * (xy - wz), M02 = 2f * (xz + wy),
            M10 = 2f * (xy + wz), M11 = 1f - (2f * (xx + zz)), M12 = 2f * (yz - wx),
            M20 = 2f * (xz - wy), M21 = 2f * (yz + wx), M22 = 1f - (2f * (xx + yy)),
            M33 = 1f,
        };
    }

    /// <summary>Translation * Rotation * Scale: scales first, then rotates, then translates.</summary>
    public static Matrix4x4 TRS(Vector3 translation, Quaternion rotation, Vector3 scale)
    {
        var m = Rotate(rotation);
        m.M00 *= scale.X; m.M10 *= scale.X; m.M20 *= scale.X;
        m.M01 *= scale.Y; m.M11 *= scale.Y; m.M21 *= scale.Y;
        m.M02 *= scale.Z; m.M12 *= scale.Z; m.M22 *= scale.Z;
        m.M03 = translation.X; m.M13 = translation.Y; m.M23 = translation.Z;
        return m;
    }

    /// <summary>OpenGL-style orthographic projection mapping the box to clip space [-1, 1] on all axes.</summary>
    public static Matrix4x4 Ortho(float left, float right, float bottom, float top, float near, float far) => new()
    {
        M00 = 2f / (right - left),
        M11 = 2f / (top - bottom),
        M22 = -2f / (far - near),
        M03 = -(right + left) / (right - left),
        M13 = -(top + bottom) / (top - bottom),
        M23 = -(far + near) / (far - near),
        M33 = 1f,
    };

    /// <summary>OpenGL-style perspective projection looking down -Z; <paramref name="fieldOfViewDegrees"/> is vertical.</summary>
    public static Matrix4x4 Perspective(float fieldOfViewDegrees, float aspect, float near, float far)
    {
        var f = 1f / MathF.Tan(fieldOfViewDegrees * Mathf.Deg2Rad * 0.5f);
        return new Matrix4x4
        {
            M00 = f / aspect,
            M11 = f,
            M22 = (far + near) / (near - far),
            M23 = 2f * far * near / (near - far),
            M32 = -1f,
        };
    }

    /// <summary>Transforms a point including the projective divide by w.</summary>
    public readonly Vector3 MultiplyPoint(Vector3 p)
    {
        var v = this * new Vector4(p, 1f);
        return new Vector3(v.X, v.Y, v.Z) / v.W;
    }

    /// <summary>Transforms a point by an affine matrix (ignores the bottom row; faster than <see cref="MultiplyPoint"/>).</summary>
    public readonly Vector3 MultiplyPoint3x4(Vector3 p) => new(
        (M00 * p.X) + (M01 * p.Y) + (M02 * p.Z) + M03,
        (M10 * p.X) + (M11 * p.Y) + (M12 * p.Z) + M13,
        (M20 * p.X) + (M21 * p.Y) + (M22 * p.Z) + M23);

    /// <summary>Transforms a direction (no translation).</summary>
    public readonly Vector3 MultiplyVector(Vector3 v) => new(
        (M00 * v.X) + (M01 * v.Y) + (M02 * v.Z),
        (M10 * v.X) + (M11 * v.Y) + (M12 * v.Z),
        (M20 * v.X) + (M21 * v.Y) + (M22 * v.Z));

    public static Matrix4x4 operator *(Matrix4x4 a, Matrix4x4 b)
    {
        var r = default(Matrix4x4);
        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                r[row, col] = (a[row, 0] * b[0, col]) + (a[row, 1] * b[1, col]) + (a[row, 2] * b[2, col]) + (a[row, 3] * b[3, col]);
            }
        }

        return r;
    }

    public static Vector4 operator *(Matrix4x4 m, Vector4 v) => new(
        (m.M00 * v.X) + (m.M01 * v.Y) + (m.M02 * v.Z) + (m.M03 * v.W),
        (m.M10 * v.X) + (m.M11 * v.Y) + (m.M12 * v.Z) + (m.M13 * v.W),
        (m.M20 * v.X) + (m.M21 * v.Y) + (m.M22 * v.Z) + (m.M23 * v.W),
        (m.M30 * v.X) + (m.M31 * v.Y) + (m.M32 * v.Z) + (m.M33 * v.W));

    public static bool operator ==(Matrix4x4 a, Matrix4x4 b) => a.Equals(b);
    public static bool operator !=(Matrix4x4 a, Matrix4x4 b) => !a.Equals(b);

    public static bool Approximately(Matrix4x4 a, Matrix4x4 b, float tolerance = 1e-5f)
    {
        ReadOnlySpan<float> sa = AsSpan(in a), sb = AsSpan(in b);
        for (var i = 0; i < 16; i++)
        {
            if (!Mathf.Approximately(sa[i], sb[i], tolerance))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The 16 elements in column-major order (GPU upload layout).</summary>
    public static ReadOnlySpan<float> AsSpan(in Matrix4x4 matrix) =>
        MemoryMarshal.CreateReadOnlySpan(in matrix.M00, 16);

    public readonly bool Equals(Matrix4x4 other) => AsSpan(in this).SequenceEqual(AsSpan(in other));
    public override readonly bool Equals(object? obj) => obj is Matrix4x4 other && Equals(other);

    public override readonly int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (var value in AsSpan(in this))
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    public override readonly string ToString() => ToString("G", CultureInfo.InvariantCulture);

    public readonly string ToString(string? format, IFormatProvider? formatProvider)
    {
        var m = this;
        string Row(int r) => string.Join(", ", Enumerable.Range(0, 4).Select(c => m[r, c].ToString(format, formatProvider)));
        return $"[{Row(0)}; {Row(1)}; {Row(2)}; {Row(3)}]";
    }

    private static int Index(int row, int column)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)row, 3u, nameof(row));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)column, 3u, nameof(column));
        return (column * 4) + row;
    }

    private static System.Numerics.Matrix4x4 ToNumerics(Matrix4x4 m) => new(
        m.M00, m.M01, m.M02, m.M03,
        m.M10, m.M11, m.M12, m.M13,
        m.M20, m.M21, m.M22, m.M23,
        m.M30, m.M31, m.M32, m.M33);

    private static Matrix4x4 FromNumerics(System.Numerics.Matrix4x4 n) => new()
    {
        M00 = n.M11, M01 = n.M12, M02 = n.M13, M03 = n.M14,
        M10 = n.M21, M11 = n.M22, M12 = n.M23, M13 = n.M24,
        M20 = n.M31, M21 = n.M32, M22 = n.M33, M23 = n.M34,
        M30 = n.M41, M31 = n.M42, M32 = n.M43, M33 = n.M44,
    };
}
