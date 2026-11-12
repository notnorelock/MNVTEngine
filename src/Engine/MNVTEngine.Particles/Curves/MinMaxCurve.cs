namespace MNVTEngine.Particles;

/// <summary>How a <see cref="MinMaxCurve"/> produces its value.</summary>
public enum CurveMode
{
    /// <summary><see cref="MinMaxCurve.Max"/>.</summary>
    Constant,

    /// <summary>Between <see cref="MinMaxCurve.Min"/> and <see cref="MinMaxCurve.Max"/>, chosen per particle.</summary>
    RandomBetweenConstants,

    /// <summary><see cref="MinMaxCurve.CurveMax"/> times <see cref="MinMaxCurve.Multiplier"/>.</summary>
    Curve,

    /// <summary>Between the two curves at the same time, chosen per particle, times <see cref="MinMaxCurve.Multiplier"/>.</summary>
    RandomBetweenCurves,
}

/// <summary>
/// A particle property: a constant, a random value between two constants, a curve, or a random value between two
/// curves. The random choice is a per-particle number (0 to 1) that stays the same over the particle's life, so a
/// particle keeps its place between the two bounds.
/// </summary>
public sealed class MinMaxCurve
{
    public MinMaxCurve()
    {
    }

    public MinMaxCurve(float constant) => Max = constant;

    public MinMaxCurve(float min, float max) => (Mode, Min, Max) = (CurveMode.RandomBetweenConstants, min, max);

    public MinMaxCurve(ParticleCurve curve, float multiplier = 1f) => (Mode, CurveMax, Multiplier) = (CurveMode.Curve, curve, multiplier);

    public CurveMode Mode { get; set; }

    public float Min { get; set; }

    /// <summary>Also the value in <see cref="CurveMode.Constant"/> mode.</summary>
    public float Max { get; set; }

    public ParticleCurve? CurveMin { get; set; }

    public ParticleCurve? CurveMax { get; set; }

    /// <summary>Scales the curve modes.</summary>
    public float Multiplier { get; set; } = 1f;

    public static implicit operator MinMaxCurve(float constant) => new(constant);

    /// <param name="time">Normalized time, usually the particle's age over its lifetime.</param>
    /// <param name="random">The particle's random number for this property, 0 to 1.</param>
    public float Evaluate(float time, float random = 0.5f) => Mode switch
    {
        CurveMode.RandomBetweenConstants => Min + ((Max - Min) * random),
        CurveMode.Curve => (CurveMax?.Evaluate(time) ?? 0f) * Multiplier,
        CurveMode.RandomBetweenCurves => Lerp(CurveMin?.Evaluate(time) ?? 0f, CurveMax?.Evaluate(time) ?? 0f, random) * Multiplier,
        _ => Max,
    };

    /// <summary>True when the value can change with time (curve modes): such properties are evaluated every step.</summary>
    public bool VariesOverTime => Mode is CurveMode.Curve or CurveMode.RandomBetweenCurves;

    public IEnumerable<string> Validate(string path)
    {
        if (!float.IsFinite(Min) || !float.IsFinite(Max) || !float.IsFinite(Multiplier))
        {
            yield return $"{path}: min, max and multiplier must be finite numbers.";
        }

        foreach (var problem in (CurveMin?.Validate(path + ".curveMin") ?? []).Concat(CurveMax?.Validate(path + ".curveMax") ?? []))
        {
            yield return problem;
        }
    }

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);
}
