using System.Runtime.InteropServices;

namespace MNVTEngine.Mathematics.Tests;

public class Matrix4x4Tests
{
    [Fact]
    public void Layout_IsColumnMajorAndSixteenFloats()
    {
        Assert.Equal(64, Marshal.SizeOf<Matrix4x4>());
        var m = Matrix4x4.Translate(new Vector3(7f, 8f, 9f));
        var span = Matrix4x4.AsSpan(in m);
        Assert.Equal(7f, span[12]);
        Assert.Equal(8f, span[13]);
        Assert.Equal(9f, span[14]);
        Assert.Equal(7f, m[0, 3]);
    }

    [Fact]
    public void Indexer_ReadWrite()
    {
        var m = Matrix4x4.Zero;
        m[2, 1] = 5f;
        Assert.Equal(5f, m.M21);
        Assert.Throws<ArgumentOutOfRangeException>(() => m[4, 0]);
    }

    [Fact]
    public void Translate_MovesPointsNotVectors()
    {
        var m = Matrix4x4.Translate(new Vector3(1f, 2f, 3f));
        Assert.Equal(new Vector3(2f, 3f, 4f), m.MultiplyPoint3x4(Vector3.One));
        Assert.Equal(new Vector3(2f, 3f, 4f), m.MultiplyPoint(Vector3.One));
        Assert.Equal(Vector3.One, m.MultiplyVector(Vector3.One));
        Assert.Equal(new Vector3(1f, 2f, 3f), m.Translation);
    }

    [Fact]
    public void Multiplication_AppliesRightOperandFirst()
    {
        var translate = Matrix4x4.Translate(new Vector3(10f, 0f, 0f));
        var scale = Matrix4x4.Scale(new Vector3(2f, 2f, 2f));
        // Scale first, then translate.
        Assert.Equal(new Vector3(12f, 2f, 2f), (translate * scale).MultiplyPoint3x4(Vector3.One));
        // Translate first, then scale.
        Assert.Equal(new Vector3(22f, 2f, 2f), (scale * translate).MultiplyPoint3x4(Vector3.One));
    }

    [Fact]
    public void Identity_IsMultiplicativeNeutral()
    {
        var m = Matrix4x4.TRS(new Vector3(1f, 2f, 3f), Quaternion.Euler(10f, 20f, 30f), new Vector3(2f, 3f, 4f));
        Assert.Equal(m, Matrix4x4.Identity * m);
        Assert.Equal(m, m * Matrix4x4.Identity);
    }

    [Fact]
    public void TRS_EqualsComposedMatrices()
    {
        var t = new Vector3(1f, 2f, 3f);
        var r = Quaternion.Euler(10f, 20f, 30f);
        var s = new Vector3(2f, 3f, 4f);
        var expected = Matrix4x4.Translate(t) * Matrix4x4.Rotate(r) * Matrix4x4.Scale(s);
        Assert.True(Matrix4x4.Approximately(expected, Matrix4x4.TRS(t, r, s)));
    }

    [Fact]
    public void Rotate_MatchesQuaternionRotation()
    {
        var q = Quaternion.Euler(15f, 25f, 35f);
        var v = new Vector3(1f, -2f, 0.5f);
        Assert.True(Vector3.Approximately(q * v, Matrix4x4.Rotate(q).MultiplyVector(v), 1e-4f));
    }

    [Fact]
    public void Inverse_TimesOriginalIsIdentity()
    {
        var m = Matrix4x4.TRS(new Vector3(5f, -3f, 2f), Quaternion.Euler(30f, 45f, 60f), new Vector3(2f, 0.5f, 3f));
        Assert.True(Matrix4x4.Approximately(Matrix4x4.Identity, m * m.Inverse, 1e-4f));
        Assert.True(Matrix4x4.Approximately(Matrix4x4.Identity, m.Inverse * m, 1e-4f));
    }

    [Fact]
    public void Inverse_SingularMatrixFails()
    {
        Assert.False(Matrix4x4.TryInvert(Matrix4x4.Scale(new Vector3(1f, 0f, 1f)), out _));
        Assert.Throws<InvalidOperationException>(() => Matrix4x4.Zero.Inverse);
    }

    [Fact]
    public void Determinant_OfScaleIsProduct() =>
        Assert.True(Mathf.Approximately(24f, Matrix4x4.Scale(new Vector3(2f, 3f, 4f)).Determinant));

    [Fact]
    public void Transposed_SwapsRowsAndColumns()
    {
        var m = Matrix4x4.Translate(new Vector3(1f, 2f, 3f)).Transposed;
        Assert.Equal(1f, m.M30);
        Assert.Equal(0f, m.M03);
        Assert.Equal(Matrix4x4.Translate(new Vector3(1f, 2f, 3f)), m.Transposed);
    }

    [Fact]
    public void Ortho_MapsBoxToClipSpace()
    {
        var m = Matrix4x4.Ortho(0f, 800f, 0f, 600f, -1f, 1f);
        Assert.True(Vector3.Approximately(new Vector3(-1f, -1f, 0f), m.MultiplyPoint(new Vector3(0f, 0f, 0f))));
        Assert.True(Vector3.Approximately(new Vector3(1f, 1f, 0f), m.MultiplyPoint(new Vector3(800f, 600f, 0f))));
        Assert.True(Vector3.Approximately(Vector3.Zero, m.MultiplyPoint(new Vector3(400f, 300f, 0f))));
    }

    [Fact]
    public void Perspective_MapsNearAndFarPlanes()
    {
        var m = Matrix4x4.Perspective(90f, 1f, 1f, 100f);
        Assert.True(Mathf.Approximately(-1f, m.MultiplyPoint(new Vector3(0f, 0f, -1f)).Z));
        Assert.True(Mathf.Approximately(1f, m.MultiplyPoint(new Vector3(0f, 0f, -100f)).Z, 1e-4f));
        // 90 degree FOV: a point at 45 degrees up lands on the top edge.
        Assert.True(Mathf.Approximately(1f, m.MultiplyPoint(new Vector3(0f, 5f, -5f)).Y));
    }
}
