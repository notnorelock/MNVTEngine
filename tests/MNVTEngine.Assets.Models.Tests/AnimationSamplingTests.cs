using MNVTEngine.Mathematics;

namespace MNVTEngine.Assets.Models.Tests;

public sealed class AnimationSamplingTests
{
    private static AnimationChannel Translation(AnimationInterpolation interpolation, float[] times, params float[] values) =>
        new(0, AnimationPath.Translation, interpolation, times, values);

    [Fact]
    public void Linear_Interpolates_AndClampsOutsideTheKeys()
    {
        var channel = Translation(AnimationInterpolation.Linear, [1f, 3f], 0f, 0f, 0f, 10f, 20f, 30f);
        Assert.Equal(new Vector3(5f, 10f, 15f), AnimationSampling.SampleVector3(channel, 2f));
        Assert.Equal(Vector3.Zero, AnimationSampling.SampleVector3(channel, 0f));                // before the first key
        Assert.Equal(new Vector3(10f, 20f, 30f), AnimationSampling.SampleVector3(channel, 9f));  // after the last key
    }

    [Fact]
    public void Step_HoldsThePreviousKey()
    {
        var channel = Translation(AnimationInterpolation.Step, [0f, 1f, 2f], 1f, 0f, 0f, 2f, 0f, 0f, 3f, 0f, 0f);
        Assert.Equal(1f, AnimationSampling.SampleVector3(channel, 0.99f).X);
        Assert.Equal(2f, AnimationSampling.SampleVector3(channel, 1f).X);
        Assert.Equal(2f, AnimationSampling.SampleVector3(channel, 1.5f).X);
    }

    [Fact]
    public void CubicSpline_PassesThroughTheKeys_AndFollowsTheTangents()
    {
        // Per key: in-tangent, value, out-tangent. Zero tangents give a smooth step: 0.5 at the middle, flat at the ends.
        var flat = Translation(AnimationInterpolation.CubicSpline, [0f, 2f], 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0);
        Assert.Equal(0f, AnimationSampling.SampleVector3(flat, 0f).X);
        Assert.Equal(2f, AnimationSampling.SampleVector3(flat, 1f).X, 4);
        Assert.Equal(4f, AnimationSampling.SampleVector3(flat, 2f).X);

        // Tangents equal to the slope (2 units per second) reproduce a straight line.
        var line = Translation(AnimationInterpolation.CubicSpline, [0f, 2f], 2, 0, 0, 0, 0, 0, 2, 0, 0, 2, 0, 0, 4, 0, 0, 2, 0, 0);
        Assert.Equal(1f, AnimationSampling.SampleVector3(line, 0.5f).X, 4);
    }

    [Fact]
    public void Rotation_TakesTheShortestPath_AndIsNormalized()
    {
        var a = Quaternion.AngleAxis(10f, Vector3.Up);
        var b = Quaternion.AngleAxis(50f, Vector3.Up);
        var negated = new Quaternion(-b.X, -b.Y, -b.Z, -b.W); // the same rotation as b, on the other hemisphere
        var channel = new AnimationChannel(0, AnimationPath.Rotation, AnimationInterpolation.Linear, [0f, 1f], [a.X, a.Y, a.Z, a.W, negated.X, negated.Y, negated.Z, negated.W]);

        var middle = AnimationSampling.SampleRotation(channel, 0.5f);
        Assert.True(Quaternion.Approximately(Quaternion.AngleAxis(30f, Vector3.Up), middle, 0.01f), $"expected 30 degrees, got {middle}");
    }
}
