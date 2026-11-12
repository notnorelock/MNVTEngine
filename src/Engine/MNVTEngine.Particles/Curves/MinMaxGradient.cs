using MNVTEngine.Mathematics;

namespace MNVTEngine.Particles;

public enum GradientValueMode
{
    /// <summary><see cref="MinMaxGradient.ColorMax"/>.</summary>
    Color,

    /// <summary>Between the two colors, chosen per particle.</summary>
    RandomBetweenColors,

    /// <summary><see cref="MinMaxGradient.GradientMax"/> at the time.</summary>
    Gradient,

    /// <summary>Between the two gradients at the time, chosen per particle.</summary>
    RandomBetweenGradients,

    /// <summary>A random point of <see cref="MinMaxGradient.GradientMax"/>, chosen per particle (ignores the time).</summary>
    RandomColor,
}

/// <summary>A particle color: a color, a random one between two, a gradient, a random one between two gradients, or a random point of a gradient.</summary>
public sealed class MinMaxGradient
{
    public MinMaxGradient()
    {
    }

    public MinMaxGradient(Color color) => ColorMax = color;

    public MinMaxGradient(ParticleGradient gradient) => (Mode, GradientMax) = (GradientValueMode.Gradient, gradient);

    public GradientValueMode Mode { get; set; }

    public Color ColorMin { get; set; } = Color.White;

    /// <summary>Also the value in <see cref="GradientValueMode.Color"/> mode.</summary>
    public Color ColorMax { get; set; } = Color.White;

    public ParticleGradient? GradientMin { get; set; }

    public ParticleGradient? GradientMax { get; set; }

    public static implicit operator MinMaxGradient(Color color) => new(color);

    public Color Evaluate(float time, float random = 0.5f) => Mode switch
    {
        GradientValueMode.RandomBetweenColors => Color.LerpUnclamped(ColorMin, ColorMax, random),
        GradientValueMode.Gradient => GradientMax?.Evaluate(time) ?? Color.White,
        GradientValueMode.RandomBetweenGradients => Color.LerpUnclamped(GradientMin?.Evaluate(time) ?? Color.White, GradientMax?.Evaluate(time) ?? Color.White, random),
        GradientValueMode.RandomColor => GradientMax?.Evaluate(random) ?? Color.White,
        _ => ColorMax,
    };
}
