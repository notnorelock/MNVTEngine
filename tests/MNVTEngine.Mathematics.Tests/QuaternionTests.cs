namespace MNVTEngine.Mathematics.Tests;

public class QuaternionTests
{
    [Fact]
    public void Identity_DoesNotRotate() =>
        Assert.Equal(new Vector3(1f, 2f, 3f), Quaternion.Identity * new Vector3(1f, 2f, 3f));

    [Fact]
    public void PositiveZRotation_IsCounterClockwiseInXYPlane()
    {
        var q = Quaternion.Euler(0f, 0f, 90f);
        Assert.True(Vector3.Approximately(Vector3.UnitY, q * Vector3.UnitX));
    }

    [Fact]
    public void AngleAxis_RightHandRule()
    {
        Assert.True(Vector3.Approximately(Vector3.UnitZ, Quaternion.AngleAxis(90f, Vector3.UnitX) * Vector3.UnitY));
        Assert.True(Vector3.Approximately(Vector3.UnitX, Quaternion.AngleAxis(90f, Vector3.UnitY) * Vector3.UnitZ));
    }

    [Fact]
    public void AngleAxis_ZeroAxisIsIdentity() => Assert.Equal(Quaternion.Identity, Quaternion.AngleAxis(45f, Vector3.Zero));

    [Fact]
    public void Multiplication_AppliesRightOperandFirst()
    {
        var rotateZ = Quaternion.AngleAxis(90f, Vector3.UnitZ); // X -> Y
        var rotateX = Quaternion.AngleAxis(90f, Vector3.UnitX); // Y -> Z
        var combined = rotateX * rotateZ;
        Assert.True(Vector3.Approximately(Vector3.UnitZ, combined * Vector3.UnitX));
        Assert.True(Vector3.Approximately(rotateX * (rotateZ * Vector3.UnitX), combined * Vector3.UnitX));
    }

    [Fact]
    public void Euler_OrderIsZThenXThenY()
    {
        var expected = Quaternion.AngleAxis(30f, Vector3.UnitY) * Quaternion.AngleAxis(20f, Vector3.UnitX) * Quaternion.AngleAxis(10f, Vector3.UnitZ);
        Assert.True(Quaternion.Approximately(expected, Quaternion.Euler(20f, 30f, 10f)));
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(10f, 20f, 30f)]
    [InlineData(45f, 0f, 0f)]
    [InlineData(0f, 270f, 0f)]
    [InlineData(300f, 100f, 200f)]
    [InlineData(0f, 0f, 359f)]
    public void EulerAngles_RoundTripRotation(float x, float y, float z)
    {
        var q = Quaternion.Euler(x, y, z);
        var restored = Quaternion.Euler(q.EulerAngles);
        Assert.True(Quaternion.Approximately(q, restored, 0.01f), $"{q} vs {restored}");
    }

    [Theory]
    [InlineData(90f, 30f, 40f)]
    [InlineData(-90f, 30f, 40f)]
    public void EulerAngles_GimbalLockStillReproducesRotation(float x, float y, float z)
    {
        var q = Quaternion.Euler(x, y, z);
        Assert.True(Quaternion.Approximately(q, Quaternion.Euler(q.EulerAngles), 0.05f));
    }

    [Fact]
    public void EulerAngles_AreInZeroTo360() =>
        Assert.True(Vector3.Approximately(new Vector3(0f, 0f, 350f), Quaternion.Euler(0f, 0f, -10f).EulerAngles, 1e-3f));

    [Fact]
    public void Inverse_UndoesRotation()
    {
        var q = Quaternion.Euler(10f, 20f, 30f);
        var v = new Vector3(1f, 2f, 3f);
        Assert.True(Vector3.Approximately(v, q.Inverse * (q * v), 1e-4f));
        Assert.True(Quaternion.Approximately(Quaternion.Identity, q * q.Inverse));
    }

    [Fact]
    public void Angle_BetweenRotations()
    {
        Assert.True(Mathf.Approximately(90f, Quaternion.Angle(Quaternion.Identity, Quaternion.Euler(0f, 0f, 90f)), 1e-3f));
        Assert.Equal(0f, Quaternion.Angle(Quaternion.Identity, Quaternion.Identity));
    }

    [Fact]
    public void Slerp_HalfwayIsHalfAngle()
    {
        var mid = Quaternion.Slerp(Quaternion.Identity, Quaternion.Euler(0f, 0f, 90f), 0.5f);
        Assert.True(Quaternion.Approximately(Quaternion.Euler(0f, 0f, 45f), mid));
    }

    [Fact]
    public void Slerp_TakesShortestPathForNegatedQuaternion()
    {
        var b = Quaternion.Euler(0f, 0f, 90f);
        var negated = new Quaternion(-b.X, -b.Y, -b.Z, -b.W);
        var mid = Quaternion.Slerp(Quaternion.Identity, negated, 0.5f);
        Assert.True(Quaternion.Approximately(Quaternion.Euler(0f, 0f, 45f), mid));
    }

    [Fact]
    public void Slerp_NearlyEqualFallsBackSafely()
    {
        var a = Quaternion.Euler(0f, 0f, 10f);
        var b = Quaternion.Euler(0f, 0f, 10.001f);
        var r = Quaternion.Slerp(a, b, 0.5f);
        Assert.False(float.IsNaN(r.W));
        Assert.True(Quaternion.Approximately(a, r, 0.01f));
    }

    [Fact]
    public void RotateTowards_LimitsStep()
    {
        var r = Quaternion.RotateTowards(Quaternion.Identity, Quaternion.Euler(0f, 0f, 90f), 30f);
        Assert.True(Mathf.Approximately(30f, Quaternion.Angle(Quaternion.Identity, r), 1e-2f));
        Assert.Equal(Quaternion.Identity, Quaternion.RotateTowards(Quaternion.Identity, Quaternion.Identity, 30f));
    }
}
