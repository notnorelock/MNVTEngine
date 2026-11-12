using MNVTEngine.Mathematics;

namespace MNVTEngine.Particles.Tests;

public sealed class CurveAndGradientTests
{
    [Fact]
    public void Curve_InterpolatesThroughKeys_ClampsOutside_AndSteps()
    {
        var linear = ParticleCurve.Linear(2f, 4f);
        Assert.Equal(3f, linear.Evaluate(0.5f), 4);
        Assert.Equal(2f, linear.Evaluate(-1f));
        Assert.Equal(4f, linear.Evaluate(5f));

        // Flat tangents: smooth (ease in/out) between keys, exact at the keys.
        var ease = new ParticleCurve(new CurveKey(0f, 0f), new CurveKey(1f, 1f));
        Assert.Equal(0.5f, ease.Evaluate(0.5f), 4);
        Assert.True(ease.Evaluate(0.1f) < 0.1f);

        var step = new ParticleCurve(new CurveKey(0f, 1f, 0f, float.PositiveInfinity), new CurveKey(1f, 5f));
        Assert.Equal(1f, step.Evaluate(0.99f));
        Assert.Equal(5f, step.Evaluate(1f));

        var unsorted = new ParticleCurve(new CurveKey(1f, 10f, 0f, 0f), new CurveKey(0f, 0f, 0f, 0f), new CurveKey(0.5f, 5f, 10f, 10f));
        Assert.Equal([0f, 0.5f, 1f], unsorted.Keys.Select(k => k.Time));
        Assert.Equal(5f, unsorted.Evaluate(0.5f));
        Assert.Equal(0f, new ParticleCurve().Evaluate(0.3f));
    }

    [Fact]
    public void MinMaxCurve_Modes()
    {
        Assert.Equal(3f, new MinMaxCurve(3f).Evaluate(0.7f, 0.9f));
        Assert.Equal(15f, new MinMaxCurve(10f, 20f).Evaluate(0f, 0.5f));
        Assert.Equal(4f, new MinMaxCurve(ParticleCurve.Linear(0f, 1f), 8f).Evaluate(0.5f));
        var between = new MinMaxCurve { Mode = CurveMode.RandomBetweenCurves, CurveMin = ParticleCurve.Constant(0f), CurveMax = ParticleCurve.Linear(0f, 10f), Multiplier = 2f };
        Assert.Equal(5f, between.Evaluate(0.5f, 0.5f), 4);
        Assert.True(between.VariesOverTime);
        Assert.NotEmpty(new MinMaxCurve(float.NaN).Validate("x"));
    }

    [Fact]
    public void Gradient_BlendsColorAndAlphaSeparately_AndFixedHolds()
    {
        var gradient = new ParticleGradient([new(0f, Color.Red), new(1f, Color.Blue)], [new(0f, 1f), new(0.5f, 0f)]);
        var middle = gradient.Evaluate(0.5f);
        Assert.Equal((0.5f, 0f, 0.5f, 0f), (middle.R, middle.G, middle.B, middle.A));
        Assert.Equal(0.5f, gradient.Evaluate(0.25f).A, 4);
        Assert.Equal(Color.Blue.B, gradient.Evaluate(2f).B);

        gradient.Mode = GradientMode.Fixed;
        Assert.Equal(Color.Red.R, gradient.Evaluate(0.9f).R);
        Assert.Equal(Color.White, new ParticleGradient().Evaluate(0.3f));

        var random = new MinMaxGradient { Mode = GradientValueMode.RandomBetweenColors, ColorMin = Color.Black, ColorMax = Color.White };
        Assert.Equal(0.25f, random.Evaluate(0f, 0.25f).R, 4);
        Assert.Equal(Color.Red.R, new MinMaxGradient { Mode = GradientValueMode.RandomColor, GradientMax = ParticleGradient.Between(Color.Red, Color.Blue) }.Evaluate(0.9f, 0f).R);
    }

    [Fact]
    public void Random_IsDeterministic_AndPerParticleValuesAreStable()
    {
        var a = new ParticleRandom(42);
        var b = new ParticleRandom(42);
        var first = Enumerable.Range(0, 100).Select(_ => a.NextFloat()).ToList();
        Assert.Equal(first, Enumerable.Range(0, 100).Select(_ => b.NextFloat()));
        Assert.All(first, f => Assert.InRange(f, 0f, 0.9999999f));
        Assert.Equal(ParticleRandom.Value(7, 3), ParticleRandom.Value(7, 3));
        Assert.NotEqual(ParticleRandom.Value(7, 3), ParticleRandom.Value(7, 4));
        Assert.InRange(Enumerable.Range(1, 2000).Average(i => ParticleRandom.Value((uint)i, 1)), 0.45, 0.55);
    }
}
