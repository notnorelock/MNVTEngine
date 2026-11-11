namespace MNVTEngine.Mathematics.Tests;

public class VectorTests
{
    [Fact]
    public void Vector2_BasicArithmetic()
    {
        var a = new Vector2(1f, 2f);
        var b = new Vector2(3f, -4f);
        Assert.Equal(new Vector2(4f, -2f), a + b);
        Assert.Equal(new Vector2(-2f, 6f), a - b);
        Assert.Equal(new Vector2(2f, 4f), a * 2f);
        Assert.Equal(new Vector2(2f, 4f), 2f * a);
        Assert.Equal(new Vector2(0.5f, 1f), a / 2f);
        Assert.Equal(new Vector2(3f, -8f), a * b);
        Assert.Equal(-a, new Vector2(-1f, -2f));
    }

    [Fact]
    public void Vector2_DotCrossMagnitude()
    {
        var a = new Vector2(3f, 4f);
        Assert.Equal(5f, a.Magnitude);
        Assert.Equal(25f, a.SqrMagnitude);
        Assert.Equal(0f, Vector2.Dot(Vector2.Right, Vector2.Up));
        Assert.Equal(1f, Vector2.Cross(Vector2.Right, Vector2.Up));
        Assert.Equal(-1f, Vector2.Cross(Vector2.Up, Vector2.Right));
    }

    [Fact]
    public void Normalize_ZeroVectorStaysZero()
    {
        Assert.Equal(Vector2.Zero, Vector2.Zero.Normalized);
        Assert.Equal(Vector3.Zero, Vector3.Zero.Normalized);
        Assert.Equal(Vector4.Zero, Vector4.Zero.Normalized);
        Assert.True(Mathf.Approximately(1f, new Vector3(1f, 2f, 3f).Normalized.Magnitude));
    }

    [Fact]
    public void Normalize_MutatesInPlace()
    {
        var v = new Vector2(0f, 5f);
        v.Normalize();
        Assert.Equal(Vector2.Up, v);
    }

    [Fact]
    public void Vector2_Angles()
    {
        Assert.True(Mathf.Approximately(90f, Vector2.Angle(Vector2.Right, Vector2.Up)));
        Assert.True(Mathf.Approximately(90f, Vector2.SignedAngle(Vector2.Right, Vector2.Up)));
        Assert.True(Mathf.Approximately(-90f, Vector2.SignedAngle(Vector2.Up, Vector2.Right)));
        Assert.Equal(0f, Vector2.Angle(Vector2.Zero, Vector2.Up));
    }

    [Fact]
    public void Vector2_RotateIsCounterClockwise()
    {
        Assert.True(Vector2.Approximately(Vector2.Up, Vector2.Rotate(Vector2.Right, 90f)));
        Assert.True(Vector2.Approximately(Vector2.Up, Vector2.Right.Perpendicular));
    }

    [Fact]
    public void MoveTowards_StopsAtTarget()
    {
        Assert.Equal(new Vector2(1f, 0f), Vector2.MoveTowards(Vector2.Zero, new Vector2(10f, 0f), 1f));
        Assert.Equal(new Vector2(10f, 0f), Vector2.MoveTowards(new Vector2(9.5f, 0f), new Vector2(10f, 0f), 1f));
        Assert.Equal(Vector3.One, Vector3.MoveTowards(Vector3.One, Vector3.One, 0f));
    }

    [Fact]
    public void ClampMagnitude_PreservesDirection()
    {
        var clamped = Vector2.ClampMagnitude(new Vector2(30f, 40f), 5f);
        Assert.True(Vector2.Approximately(new Vector2(3f, 4f), clamped));
        Assert.Equal(new Vector2(1f, 1f), Vector2.ClampMagnitude(new Vector2(1f, 1f), 5f));
    }

    [Fact]
    public void Reflect_FlipsNormalComponent() =>
        Assert.Equal(new Vector2(1f, 1f), Vector2.Reflect(new Vector2(1f, -1f), Vector2.Up));

    [Fact]
    public void Vector3_CrossIsRightHanded()
    {
        Assert.Equal(Vector3.UnitZ, Vector3.Cross(Vector3.UnitX, Vector3.UnitY));
        Assert.Equal(Vector3.UnitX, Vector3.Cross(Vector3.UnitY, Vector3.UnitZ));
        Assert.Equal(-Vector3.UnitZ, Vector3.Cross(Vector3.UnitY, Vector3.UnitX));
    }

    [Fact]
    public void Vector3_Project()
    {
        Assert.Equal(new Vector3(2f, 0f, 0f), Vector3.Project(new Vector3(2f, 3f, 4f), new Vector3(5f, 0f, 0f)));
        Assert.Equal(Vector3.Zero, Vector3.Project(Vector3.One, Vector3.Zero));
    }

    [Fact]
    public void Lerp_ClampsAndUnclampedExtrapolates()
    {
        Assert.Equal(new Vector3(1f, 1f, 1f), Vector3.Lerp(Vector3.Zero, Vector3.One, 5f));
        Assert.Equal(new Vector3(2f, 2f, 2f), Vector3.LerpUnclamped(Vector3.Zero, Vector3.One, 2f));
        Assert.Equal(new Vector4(0.5f, 0.5f, 0.5f, 0.5f), Vector4.Lerp(Vector4.Zero, Vector4.One, 0.5f));
    }

    [Fact]
    public void Conversions()
    {
        Vector3 v3 = new Vector2(1f, 2f);
        Assert.Equal(new Vector3(1f, 2f, 0f), v3);
        Assert.Equal(new Vector2(1f, 2f), (Vector2)new Vector3(1f, 2f, 3f));
        Assert.Equal(new Vector3(1f, 2f, 3f), (Vector3)new Vector4(1f, 2f, 3f, 4f));
    }

    [Fact]
    public void Equality_IsExactAndHashConsistent()
    {
        var a = new Vector3(0.1f, 0.2f, 0.3f);
        var b = new Vector3(0.1f, 0.2f, 0.3f);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.False(a == new Vector3(0.1f, 0.2f, 0.3000001f));
        Assert.True(Vector3.Approximately(a, new Vector3(0.1f, 0.2f, 0.3000001f)));
    }

    [Fact]
    public void ToString_UsesInvariantCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("(1.5, 2)", new Vector2(1.5f, 2f).ToString());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Deconstruct()
    {
        var (x, y) = new Vector2(1f, 2f);
        Assert.Equal((1f, 2f), (x, y));
    }

    [Fact]
    public void DoublePrecisionHelpers_RoundToFloatOnlyAtTheEnd()
    {
        Assert.Equal(new Vector3(0.1f, 0.2f, 0.3f), new Vector3(0.1, 0.2, 0.3));
        Assert.Equal(new Vector2(1.5f, -2f), new Vector2(1.5, -2.0));
        Assert.Equal(new Vector3(1f, 2f, 3f), new Vector3(2f, 4f, 6f) * 0.5);
        Assert.Equal(new Vector3(1f, 2f, 3f), 0.5 * new Vector3(2f, 4f, 6f));
        Assert.Equal(new Vector2(1f, 2f), new Vector2(3f, 6f) / 3.0);
    }

    [Fact]
    public void InstanceHelpers_MatchTheStaticOnes()
    {
        var (a, b) = (new Vector3(1f, 2f, 3f), new Vector3(-2f, 0.5f, 4f));
        Assert.Equal(Vector3.Dot(a, b), a.Dot(b));
        Assert.Equal(Vector3.Cross(a, b), a.Cross(b));
        Assert.Equal(Vector3.Distance(a, b), a.DistanceTo(b));
        Assert.Equal(Vector3.DistanceSquared(a, b), a.DistanceToSquared(b));
        Assert.Equal(new Vector3(-5f, -1f, 5f), a.LerpUnclamped(b, 2)); // extrapolates past 1
        Assert.Equal(new Vector3(1f, 9f, 3f), a.WithY(9));
        Assert.Equal(new Vector2(1f, 3f), a.Xz);
        Assert.Equal(Math.PI / 2, Vector3.UnitX.AngleTo(Vector3.UnitZ), 6);
        Assert.Equal(Math.PI / 2, Vector3.Zero.AngleTo(Vector3.UnitZ)); // zero vectors: π/2, as three.js
        var (p, q) = (new Vector2(1f, 2f), new Vector2(3f, -1f));
        Assert.Equal((Vector2.Dot(p, q), Vector2.Cross(p, q), Vector2.Distance(p, q)), (p.Dot(q), p.Cross(q), p.DistanceTo(q)));
        Assert.Equal(new Vector2(5f, -4f), p.LerpUnclamped(q, 2));
    }
}
