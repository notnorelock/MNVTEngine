namespace MNVTEngine.Particles;

/// <summary>
/// Deterministic random numbers (xorshift32) for particle systems: the same seed gives the same particles on every
/// machine and run. <see cref="Value"/> derives stable per-particle numbers from a particle's seed, so a particle keeps
/// the same "random between" choice for each property through its life without storing one number per property.
/// </summary>
public struct ParticleRandom
{
    private uint _state;

    public ParticleRandom(uint seed) => _state = seed == 0 ? 0x9E3779B9u : seed;

    public uint NextUInt()
    {
        var x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return x;
    }

    /// <summary>0 (inclusive) to 1 (exclusive).</summary>
    public float NextFloat() => (NextUInt() >> 8) * (1f / 16777216f);

    /// <summary>A stable number from 0 to 1 for a particle's <paramref name="seed"/> and a property (<paramref name="salt"/>).</summary>
    public static float Value(uint seed, uint salt)
    {
        var x = seed ^ (salt * 0x9E3779B9u);
        x ^= x >> 16;
        x *= 0x7FEB352Du;
        x ^= x >> 15;
        x *= 0x846CA68Bu;
        x ^= x >> 16;
        return (x >> 8) * (1f / 16777216f);
    }
}

/// <summary>Salts of the per-particle random numbers, one per property.</summary>
internal static class Salt
{
    public const uint Lifetime = 1, Speed = 2, Size = 3, SizeY = 4, SizeZ = 5, Rotation = 6, Color = 7, Velocity = 8, Force = 9,
        ColorOverLifetime = 10, SizeOverLifetime = 11, RotationOverLifetime = 12, StartFrame = 13, FrameOverTime = 14,
        VelocityY = 15, VelocityZ = 16, ForceY = 17, ForceZ = 18, Gravity = 19, SpeedModifier = 20;
}
