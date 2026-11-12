using MNVTEngine.Mathematics;

namespace MNVTEngine.Particles;

/// <summary>Deterministic 3D gradient (Perlin) noise in about -1 to 1. Static tables, no allocation.</summary>
public static class ParticleNoise
{
    private static readonly byte[] Permutation = CreatePermutation();

    public static float Sample(float x, float y, float z)
    {
        var xi = (int)MathF.Floor(x);
        var yi = (int)MathF.Floor(y);
        var zi = (int)MathF.Floor(z);
        var (xf, yf, zf) = (x - xi, y - yi, z - zi);
        var (u, v, w) = (Fade(xf), Fade(yf), Fade(zf));
        int X = xi & 255, Y = yi & 255, Z = zi & 255;
        var p = Permutation;
        int a = p[X] + Y, aa = p[a & 255] + Z, ab = p[(a + 1) & 255] + Z;
        int b = p[(X + 1) & 255] + Y, ba = p[b & 255] + Z, bb = p[(b + 1) & 255] + Z;
        return Lerp(w,
            Lerp(v, Lerp(u, Grad(p[aa & 255], xf, yf, zf), Grad(p[ba & 255], xf - 1, yf, zf)), Lerp(u, Grad(p[ab & 255], xf, yf - 1, zf), Grad(p[bb & 255], xf - 1, yf - 1, zf))),
            Lerp(v, Lerp(u, Grad(p[(aa + 1) & 255], xf, yf, zf - 1), Grad(p[(ba + 1) & 255], xf - 1, yf, zf - 1)), Lerp(u, Grad(p[(ab + 1) & 255], xf, yf - 1, zf - 1), Grad(p[(bb + 1) & 255], xf - 1, yf - 1, zf - 1))));
    }

    /// <summary>A noise vector: three decorrelated samples around <paramref name="position"/>.</summary>
    public static Vector3 Vector(Vector3 position, float offset) => new(
        Sample(position.X + offset, position.Y, position.Z),
        Sample(position.X + 31.7f, position.Y + offset, position.Z + 47.3f),
        Sample(position.X + 71.1f, position.Y + 13.9f, position.Z + offset));

    private static float Fade(float t) => t * t * t * ((t * ((t * 6f) - 15f)) + 10f);

    private static float Lerp(float t, float a, float b) => a + (t * (b - a));

    private static float Grad(int hash, float x, float y, float z)
    {
        var h = hash & 15;
        var u = h < 8 ? x : y;
        var v = h < 4 ? y : h is 12 or 14 ? x : z;
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }

    private static byte[] CreatePermutation()
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            table[i] = (byte)i;
        }

        var random = new ParticleRandom(0xC0FFEE);
        for (var i = 255; i > 0; i--)
        {
            var j = (int)(random.NextUInt() % (uint)(i + 1));
            (table[i], table[j]) = (table[j], table[i]);
        }

        return table;
    }
}
