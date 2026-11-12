using MNVTEngine.Mathematics;

namespace MNVTEngine.Assets.Models;

/// <summary>
/// Evaluates <see cref="AnimationChannel"/> keyframes at a time, the glTF way: before the first key the first value,
/// after the last key the last value; step holds the previous key, linear interpolates (rotations by shortest-path
/// slerp), cubic spline is a Hermite curve whose tangents (stored around each value) are scaled by the key interval.
/// </summary>
public static class AnimationSampling
{
    /// <summary>A translation or scale channel at <paramref name="time"/>.</summary>
    public static Vector3 SampleVector3(AnimationChannel channel, float time)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var v = channel.Values;
        var stride = channel.Interpolation == AnimationInterpolation.CubicSpline ? 9 : 3;
        var offset = channel.Interpolation == AnimationInterpolation.CubicSpline ? 3 : 0;
        Vector3 At(int key, int part = 0) => new(v[(key * stride) + offset + part], v[(key * stride) + offset + part + 1], v[(key * stride) + offset + part + 2]);
        var (k, t, dt) = Locate(channel.Times, time);
        if (t <= 0f || k + 1 >= channel.Times.Length)
        {
            return At(k);
        }

        return channel.Interpolation switch
        {
            AnimationInterpolation.Step => At(k),
            AnimationInterpolation.CubicSpline => Hermite(At(k), At(k, 3) * dt, At(k + 1), At(k + 1, -3) * dt, t),
            _ => Vector3.LerpUnclamped(At(k), At(k + 1), t),
        };
    }

    /// <summary>A rotation channel at <paramref name="time"/>, normalized.</summary>
    public static Quaternion SampleRotation(AnimationChannel channel, float time)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var v = channel.Values;
        var stride = channel.Interpolation == AnimationInterpolation.CubicSpline ? 12 : 4;
        var offset = channel.Interpolation == AnimationInterpolation.CubicSpline ? 4 : 0;
        Quaternion At(int key, int part = 0)
        {
            var i = (key * stride) + offset + part;
            return new Quaternion(v[i], v[i + 1], v[i + 2], v[i + 3]);
        }

        var (k, t, dt) = Locate(channel.Times, time);
        if (t <= 0f || k + 1 >= channel.Times.Length)
        {
            return At(k).Normalized;
        }

        switch (channel.Interpolation)
        {
            case AnimationInterpolation.Step:
                return At(k).Normalized;
            case AnimationInterpolation.CubicSpline:
            {
                var a = At(k);
                var outTangent = At(k, 4);
                var b = At(k + 1);
                var inTangent = At(k + 1, -4);
                var t2 = t * t;
                var t3 = t2 * t;
                var (h00, h10, h01, h11) = ((2f * t3) - (3f * t2) + 1f, (t3 - (2f * t2) + t) * dt, (-2f * t3) + (3f * t2), (t3 - t2) * dt);
                return new Quaternion(
                    (h00 * a.X) + (h10 * outTangent.X) + (h01 * b.X) + (h11 * inTangent.X),
                    (h00 * a.Y) + (h10 * outTangent.Y) + (h01 * b.Y) + (h11 * inTangent.Y),
                    (h00 * a.Z) + (h10 * outTangent.Z) + (h01 * b.Z) + (h11 * inTangent.Z),
                    (h00 * a.W) + (h10 * outTangent.W) + (h01 * b.W) + (h11 * inTangent.W)).Normalized;
            }

            default:
                return Quaternion.Slerp(At(k).Normalized, At(k + 1).Normalized, t);
        }
    }

    /// <summary>A blend shape weights channel at <paramref name="time"/> into <paramref name="weights"/> (one per blend shape).</summary>
    public static void SampleWeights(AnimationChannel channel, float time, Span<float> weights)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var cubic = channel.Interpolation == AnimationInterpolation.CubicSpline;
        var count = channel.Values.Length / (channel.Times.Length * (cubic ? 3 : 1));
        var stride = count * (cubic ? 3 : 1);
        var offset = cubic ? count : 0;
        var v = channel.Values;
        var (k, t, dt) = Locate(channel.Times, time);
        for (var i = 0; i < Math.Min(count, weights.Length); i++)
        {
            var a = v[(k * stride) + offset + i];
            if (t <= 0f || k + 1 >= channel.Times.Length || channel.Interpolation == AnimationInterpolation.Step)
            {
                weights[i] = a;
                continue;
            }

            var b = v[((k + 1) * stride) + offset + i];
            if (!cubic)
            {
                weights[i] = a + ((b - a) * t);
                continue;
            }

            var outTangent = v[(k * stride) + offset + count + i] * dt;
            var inTangent = v[((k + 1) * stride) + i] * dt;
            var t2 = t * t;
            var t3 = t2 * t;
            weights[i] = (a * ((2f * t3) - (3f * t2) + 1f)) + (outTangent * (t3 - (2f * t2) + t)) + (b * ((-2f * t3) + (3f * t2))) + (inTangent * (t3 - t2));
        }
    }

    /// <summary>The key at or before <paramref name="time"/>, the fraction towards the next one, and the interval.</summary>
    private static (int Key, float T, float Interval) Locate(float[] times, float time)
    {
        if (times.Length == 0)
        {
            throw new ArgumentException("A channel needs at least one key.", nameof(times));
        }

        if (time <= times[0])
        {
            return (0, 0f, 0f);
        }

        if (time >= times[^1])
        {
            return (times.Length - 1, 0f, 0f);
        }

        var index = Array.BinarySearch(times, time);
        var key = index >= 0 ? index : ~index - 1;
        var interval = times[key + 1] - times[key];
        return (key, interval > 0f ? (time - times[key]) / interval : 0f, interval);
    }

    private static Vector3 Hermite(Vector3 a, Vector3 outTangent, Vector3 b, Vector3 inTangent, float t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        return (a * ((2f * t3) - (3f * t2) + 1f)) + (outTangent * (t3 - (2f * t2) + t)) + (b * ((-2f * t3) + (3f * t2))) + (inTangent * (t3 - t2));
    }
}
