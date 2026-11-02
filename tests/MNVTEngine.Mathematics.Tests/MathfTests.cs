namespace MNVTEngine.Mathematics.Tests;

public class MathfTests
{
    [Theory]
    [InlineData(-1f, 0f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(2f, 1f)]
    public void Clamp01_ClampsToUnitRange(float input, float expected) => Assert.Equal(expected, Mathf.Clamp01(input));

    [Fact]
    public void Lerp_ClampsT_LerpUnclampedDoesNot()
    {
        Assert.Equal(10f, Mathf.Lerp(0f, 10f, 2f));
        Assert.Equal(20f, Mathf.LerpUnclamped(0f, 10f, 2f));
        Assert.Equal(5f, Mathf.Lerp(0f, 10f, 0.5f));
    }

    [Fact]
    public void InverseLerp_DegenerateRangeReturnsZero() => Assert.Equal(0f, Mathf.InverseLerp(3f, 3f, 3f));

    [Fact]
    public void Remap_MapsBetweenRanges() => Assert.Equal(75f, Mathf.Remap(0.5f, 0f, 1f, 50f, 100f));

    [Fact]
    public void MoveTowards_DoesNotOvershoot()
    {
        Assert.Equal(1f, Mathf.MoveTowards(0f, 10f, 1f));
        Assert.Equal(10f, Mathf.MoveTowards(9.5f, 10f, 1f));
        Assert.Equal(-1f, Mathf.MoveTowards(0f, -10f, 1f));
    }

    [Fact]
    public void SmoothStep_HitsEndpointsAndMidpoint()
    {
        Assert.Equal(0f, Mathf.SmoothStep(0f, 1f, 0f));
        Assert.Equal(1f, Mathf.SmoothStep(0f, 1f, 1f));
        Assert.Equal(0.5f, Mathf.SmoothStep(0f, 1f, 0.5f));
        Assert.True(Mathf.SmoothStep(0f, 1f, 0.1f) < 0.1f, "SmoothStep eases in.");
    }

    [Theory]
    [InlineData(370f, 360f, 10f)]
    [InlineData(-10f, 360f, 350f)]
    [InlineData(5f, 2f, 1f)]
    public void Repeat_WrapsIntoRange(float t, float length, float expected) =>
        Assert.True(Mathf.Approximately(expected, Mathf.Repeat(t, length)));

    [Fact]
    public void PingPong_Bounces()
    {
        Assert.Equal(1f, Mathf.PingPong(1f, 2f));
        Assert.Equal(1f, Mathf.PingPong(3f, 2f));
        Assert.Equal(0f, Mathf.PingPong(4f, 2f));
    }

    [Theory]
    [InlineData(10f, 350f, -20f)]
    [InlineData(350f, 10f, 20f)]
    [InlineData(0f, 180f, 180f)]
    [InlineData(0f, 540f, 180f)]
    public void DeltaAngle_IsShortestSignedDifference(float a, float b, float expected) =>
        Assert.True(Mathf.Approximately(expected, Mathf.DeltaAngle(a, b)));

    [Fact]
    public void LerpAngle_TakesShortestPath() => Assert.True(Mathf.Approximately(0f, Mathf.Repeat(Mathf.LerpAngle(350f, 10f, 0.5f), 360f)));

    [Fact]
    public void MoveTowardsAngle_WrapsAround() => Assert.True(Mathf.Approximately(355f, Mathf.MoveTowardsAngle(350f, 10f, 5f)));

    [Fact]
    public void SmoothDamp_ConvergesWithoutOvershoot()
    {
        float value = 0f, velocity = 0f;
        for (var i = 0; i < 600; i++)
        {
            value = Mathf.SmoothDamp(value, 10f, ref velocity, 0.3f, 1f / 60f);
            Assert.True(value <= 10f + 1e-4f, $"Overshot at step {i}: {value}");
        }

        Assert.True(Mathf.Approximately(10f, value, 1e-3f));
    }

    [Fact]
    public void Approximately_UsesRelativeTolerance()
    {
        Assert.True(Mathf.Approximately(1_000_000f, 1_000_001f));
        Assert.False(Mathf.Approximately(1f, 1.001f));
        Assert.True(Mathf.Approximately(0f, 1e-7f));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(3, 4)]
    [InlineData(64, 64)]
    [InlineData(65, 128)]
    public void NextPowerOfTwo(int input, int expected) => Assert.Equal(expected, Mathf.NextPowerOfTwo(input));

    [Fact]
    public void IsPowerOfTwo_RejectsZeroAndNegatives()
    {
        Assert.False(Mathf.IsPowerOfTwo(0));
        Assert.False(Mathf.IsPowerOfTwo(-4));
        Assert.True(Mathf.IsPowerOfTwo(1024));
    }

    [Fact]
    public void Sign_ReturnsZeroForZero() => Assert.Equal(0f, Mathf.Sign(0f));
}
