namespace MNVTEngine.Mathematics;

/// <summary>
/// Scalar math helpers. Trigonometric functions take and return radians;
/// angle helpers named *Angle work in degrees.
/// </summary>
public static class Mathf
{
    public const float PI = MathF.PI;
    public const float Tau = MathF.Tau;
    public const float Deg2Rad = MathF.PI / 180f;
    public const float Rad2Deg = 180f / MathF.PI;

    /// <summary>Tolerance used by vector normalization and other near-zero checks.</summary>
    public const float Epsilon = 1e-6f;

    public static float Sin(float radians) => MathF.Sin(radians);
    public static float Cos(float radians) => MathF.Cos(radians);
    public static float Tan(float radians) => MathF.Tan(radians);
    public static float Asin(float value) => MathF.Asin(value);
    public static float Acos(float value) => MathF.Acos(value);
    public static float Atan(float value) => MathF.Atan(value);
    public static float Atan2(float y, float x) => MathF.Atan2(y, x);
    public static float Sqrt(float value) => MathF.Sqrt(value);
    public static float Abs(float value) => MathF.Abs(value);
    public static float Pow(float value, float power) => MathF.Pow(value, power);
    public static float Floor(float value) => MathF.Floor(value);
    public static float Ceil(float value) => MathF.Ceiling(value);
    public static float Round(float value) => MathF.Round(value, MidpointRounding.ToEven);
    public static int FloorToInt(float value) => (int)MathF.Floor(value);
    public static int CeilToInt(float value) => (int)MathF.Ceiling(value);
    public static int RoundToInt(float value) => (int)MathF.Round(value, MidpointRounding.ToEven);

    /// <summary>Returns -1, 0 or 1.</summary>
    public static float Sign(float value) => MathF.Sign(value);

    public static float Min(float a, float b) => a < b ? a : b;
    public static float Max(float a, float b) => a > b ? a : b;

    public static float Clamp(float value, float min, float max) =>
        value < min ? min : value > max ? max : value;

    public static int Clamp(int value, int min, int max) =>
        value < min ? min : value > max ? max : value;

    public static float Clamp01(float value) => Clamp(value, 0f, 1f);

    /// <summary>Linear interpolation with <paramref name="t"/> clamped to [0, 1].</summary>
    public static float Lerp(float a, float b, float t) => a + ((b - a) * Clamp01(t));

    public static float LerpUnclamped(float a, float b, float t) => a + ((b - a) * t);

    /// <summary>Returns where <paramref name="value"/> lies between a and b, clamped to [0, 1]. Returns 0 when a == b.</summary>
    public static float InverseLerp(float a, float b, float value) =>
        a == b ? 0f : Clamp01((value - a) / (b - a));

    /// <summary>Maps <paramref name="value"/> from [inMin, inMax] to [outMin, outMax] without clamping.</summary>
    public static float Remap(float value, float inMin, float inMax, float outMin, float outMax) =>
        inMin == inMax ? outMin : outMin + ((value - inMin) / (inMax - inMin) * (outMax - outMin));

    /// <summary>Moves <paramref name="current"/> towards <paramref name="target"/> by at most <paramref name="maxDelta"/>.</summary>
    public static float MoveTowards(float current, float target, float maxDelta)
    {
        var delta = target - current;
        return MathF.Abs(delta) <= maxDelta ? target : current + (MathF.Sign(delta) * maxDelta);
    }

    /// <summary>Hermite interpolation between a and b; <paramref name="t"/> is clamped to [0, 1].</summary>
    public static float SmoothStep(float a, float b, float t)
    {
        t = Clamp01(t);
        t = t * t * (3f - (2f * t));
        return a + ((b - a) * t);
    }

    /// <summary>Wraps <paramref name="t"/> into [0, length).</summary>
    public static float Repeat(float t, float length) =>
        Clamp(t - (MathF.Floor(t / length) * length), 0f, length);

    /// <summary>Bounces <paramref name="t"/> between 0 and <paramref name="length"/>.</summary>
    public static float PingPong(float t, float length)
    {
        t = Repeat(t, length * 2f);
        return length - MathF.Abs(t - length);
    }

    /// <summary>Shortest signed difference between two angles in degrees, in (-180, 180].</summary>
    public static float DeltaAngle(float currentDegrees, float targetDegrees)
    {
        var delta = Repeat(targetDegrees - currentDegrees, 360f);
        return delta > 180f ? delta - 360f : delta;
    }

    /// <summary>Interpolates between two angles in degrees along the shortest path.</summary>
    public static float LerpAngle(float aDegrees, float bDegrees, float t) =>
        aDegrees + (DeltaAngle(aDegrees, bDegrees) * Clamp01(t));

    /// <summary>Like <see cref="MoveTowards"/> but wraps correctly around 360 degrees.</summary>
    public static float MoveTowardsAngle(float currentDegrees, float targetDegrees, float maxDelta)
    {
        var delta = DeltaAngle(currentDegrees, targetDegrees);
        return -maxDelta < delta && delta < maxDelta
            ? currentDegrees + delta
            : MoveTowards(currentDegrees, currentDegrees + delta, maxDelta);
    }

    /// <summary>
    /// Critically damped spring towards <paramref name="target"/>. <paramref name="velocity"/> is state the caller keeps between frames.
    /// </summary>
    public static float SmoothDamp(float current, float target, ref float velocity, float smoothTime, float deltaTime, float maxSpeed = float.PositiveInfinity)
    {
        smoothTime = MathF.Max(0.0001f, smoothTime);
        var omega = 2f / smoothTime;
        var x = omega * deltaTime;
        var exp = 1f / (1f + x + (0.48f * x * x) + (0.235f * x * x * x));

        var originalTarget = target;
        var maxChange = maxSpeed * smoothTime;
        var change = Clamp(current - target, -maxChange, maxChange);
        target = current - change;

        var temp = (velocity + (omega * change)) * deltaTime;
        velocity = (velocity - (omega * temp)) * exp;
        var result = target + ((change + temp) * exp);

        // Prevent overshooting the original target.
        if (originalTarget - current > 0f == result > originalTarget)
        {
            result = originalTarget;
            velocity = 0f;
        }

        return result;
    }

    /// <summary>Relative + absolute tolerance comparison suitable for single-precision results.</summary>
    public static bool Approximately(float a, float b, float tolerance = 1e-5f) =>
        MathF.Abs(b - a) <= MathF.Max(tolerance * MathF.Max(MathF.Abs(a), MathF.Abs(b)), tolerance);

    public static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    /// <summary>Smallest power of two greater than or equal to <paramref name="value"/> (1 for values &lt;= 1).</summary>
    public static int NextPowerOfTwo(int value) =>
        value <= 1 ? 1 : (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)value);
}
