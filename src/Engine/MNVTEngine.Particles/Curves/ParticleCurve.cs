using Newtonsoft.Json;

namespace MNVTEngine.Particles;

/// <summary>A curve key: value at a time, with the slopes the curve has arriving at and leaving it.</summary>
/// <param name="InTangent">Slope arriving at the key; infinity holds the previous key's value up to it (a step).</param>
/// <param name="OutTangent">Slope leaving the key; infinity holds this key's value until the next key (a step).</param>
public readonly record struct CurveKey(float Time, float Value, float InTangent = 0f, float OutTangent = 0f);

/// <summary>
/// A keyframed curve evaluated with cubic Hermite interpolation between keys and clamped outside them (the first key's
/// value before it, the last key's after it). Particle modules evaluate it over a particle's normalized age (0 to 1);
/// it does not depend on particles and can serve other systems. Evaluation does not allocate.
/// </summary>
public sealed class ParticleCurve
{
    private List<CurveKey> _keys = [];

    public ParticleCurve()
    {
    }

    public ParticleCurve(params CurveKey[] keys) => Keys = [.. keys];

    /// <summary>Keys ordered by time (setting sorts them).</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)] // a loaded list replaces the default one instead of adding to it
    public List<CurveKey> Keys
    {
        get => _keys;
        set => _keys = [.. (value ?? []).OrderBy(k => k.Time)];
    }

    /// <summary>A flat curve.</summary>
    public static ParticleCurve Constant(float value) => new(new CurveKey(0f, value), new CurveKey(1f, value));

    /// <summary>A straight line from <paramref name="from"/> at 0 to <paramref name="to"/> at 1.</summary>
    public static ParticleCurve Linear(float from, float to) => new(new CurveKey(0f, from, to - from, to - from), new CurveKey(1f, to, to - from, to - from));

    public float Evaluate(float time)
    {
        var keys = _keys;
        var count = keys.Count;
        if (count == 0)
        {
            return 0f;
        }

        if (count == 1 || time <= keys[0].Time)
        {
            return keys[0].Value;
        }

        if (time >= keys[count - 1].Time)
        {
            return keys[count - 1].Value;
        }

        // Last key at or before time (binary search: no allocation, O(log n)).
        int low = 0, high = count - 1;
        while (high - low > 1)
        {
            var middle = (low + high) >> 1;
            if (keys[middle].Time <= time)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        var a = keys[low];
        var b = keys[high];
        var span = b.Time - a.Time;
        if (span <= 0f || float.IsInfinity(a.OutTangent) || float.IsInfinity(b.InTangent))
        {
            return a.Value;
        }

        var s = (time - a.Time) / span;
        var s2 = s * s;
        var s3 = s2 * s;
        return ((2f * s3) - (3f * s2) + 1f) * a.Value
            + (s3 - (2f * s2) + s) * span * a.OutTangent
            + ((-2f * s3) + (3f * s2)) * b.Value
            + (s3 - s2) * span * b.InTangent;
    }

    /// <summary>Problems that make the curve unusable (non-finite times or values); empty when valid.</summary>
    public IEnumerable<string> Validate(string path)
    {
        for (var i = 0; i < _keys.Count; i++)
        {
            if (!float.IsFinite(_keys[i].Time) || !float.IsFinite(_keys[i].Value))
            {
                yield return $"{path}.keys[{i}]: time and value must be finite numbers.";
            }
        }
    }
}
