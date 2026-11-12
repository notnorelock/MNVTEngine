using MNVTEngine.Mathematics;

namespace MNVTEngine.Particles;

/// <summary>A color key of a gradient (its alpha is ignored: alpha has keys of its own).</summary>
public readonly record struct GradientColorKey(float Time, Color Color);

public readonly record struct GradientAlphaKey(float Time, float Alpha);

public enum GradientMode
{
    /// <summary>Linear interpolation between keys.</summary>
    Blend,

    /// <summary>The value of the last key at or before the time.</summary>
    Fixed,
}

/// <summary>
/// A color gradient over 0 to 1: color keys and alpha keys interpolated separately, clamped outside the keys.
/// Without color keys the color is white; without alpha keys the alpha is 1. Evaluation does not allocate.
/// </summary>
public sealed class ParticleGradient
{
    private List<GradientColorKey> _colorKeys = [];
    private List<GradientAlphaKey> _alphaKeys = [];

    public ParticleGradient()
    {
    }

    public ParticleGradient(IEnumerable<GradientColorKey> colors, IEnumerable<GradientAlphaKey> alphas)
    {
        ColorKeys = [.. colors];
        AlphaKeys = [.. alphas];
    }

    /// <summary>From one color to another, alpha included.</summary>
    public static ParticleGradient Between(Color from, Color to) =>
        new([new(0f, from), new(1f, to)], [new(0f, from.A), new(1f, to.A)]);

    public List<GradientColorKey> ColorKeys
    {
        get => _colorKeys;
        set => _colorKeys = [.. (value ?? []).OrderBy(k => k.Time)];
    }

    public List<GradientAlphaKey> AlphaKeys
    {
        get => _alphaKeys;
        set => _alphaKeys = [.. (value ?? []).OrderBy(k => k.Time)];
    }

    public GradientMode Mode { get; set; }

    public Color Evaluate(float time)
    {
        var color = Color.White;
        var keys = _colorKeys;
        if (keys.Count > 0)
        {
            var b = 0;
            while (b < keys.Count && keys[b].Time < time)
            {
                b++;
            }

            color = b == 0 ? keys[0].Color
                : b == keys.Count || Mode == GradientMode.Fixed ? keys[b - 1].Color
                : Color.LerpUnclamped(keys[b - 1].Color, keys[b].Color, Fraction(keys[b - 1].Time, keys[b].Time, time));
        }

        var alpha = 1f;
        var alphas = _alphaKeys;
        if (alphas.Count > 0)
        {
            var b = 0;
            while (b < alphas.Count && alphas[b].Time < time)
            {
                b++;
            }

            alpha = b == 0 ? alphas[0].Alpha
                : b == alphas.Count || Mode == GradientMode.Fixed ? alphas[b - 1].Alpha
                : alphas[b - 1].Alpha + ((alphas[b].Alpha - alphas[b - 1].Alpha) * Fraction(alphas[b - 1].Time, alphas[b].Time, time));
        }

        return new Color(color.R, color.G, color.B, alpha);
    }

    private static float Fraction(float from, float to, float time) => to > from ? (time - from) / (to - from) : 0f;
}
