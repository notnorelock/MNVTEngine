using Newtonsoft.Json;

namespace MNVTEngine.Particles;

/// <summary>A number of particles emitted at once, once or repeatedly.</summary>
public sealed class ParticleBurst
{
    /// <summary>Seconds into the cycle.</summary>
    public float Time { get; set; }

    public MinMaxCurve Count { get; set; } = 30f;

    /// <summary>How many times the burst fires per cycle; 0 repeats until the cycle ends.</summary>
    public int Cycles { get; set; } = 1;

    /// <summary>Seconds between repeats.</summary>
    public float Interval { get; set; } = 0.1f;

    /// <summary>Chance (0 to 1) that each repeat fires.</summary>
    public float Probability { get; set; } = 1f;
}

/// <summary>How many particles are emitted: continuously over time and over distance moved, and in bursts.</summary>
public sealed class EmissionModule
{
    public bool Enabled { get; set; } = true;

    /// <summary>Particles per second, evaluated over the cycle (0 to 1).</summary>
    public MinMaxCurve RateOverTime { get; set; } = 10f;

    /// <summary>Particles per unit the emitter moves.</summary>
    public MinMaxCurve RateOverDistance { get; set; } = 0f;

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)] // a loaded list replaces the default one instead of adding to it
    public List<ParticleBurst> Bursts { get; set; } = [];

    public EmissionModule AddBurst(ParticleBurst burst)
    {
        ArgumentNullException.ThrowIfNull(burst);
        Bursts.Add(burst);
        return this;
    }
}
