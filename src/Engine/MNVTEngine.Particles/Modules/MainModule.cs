using MNVTEngine.Mathematics;

namespace MNVTEngine.Particles;

/// <summary>Where particles live: moving the emitter moves <see cref="Local"/> particles with it; <see cref="World"/> particles stay where they were emitted.</summary>
public enum ParticleSimulationSpace
{
    Local,
    World,
}

/// <summary>What a system does when it has finished (not looping, duration over, no particles left).</summary>
public enum ParticleStopAction
{
    None,

    /// <summary>Disables the game object.</summary>
    Disable,

    /// <summary>Destroys the game object.</summary>
    Destroy,
}

/// <summary>The system's timing and the start values of every particle.</summary>
public sealed class MainModule
{
    /// <summary>Length of one cycle in seconds.</summary>
    public float Duration { get; set; } = 5f;

    public bool Loop { get; set; } = true;

    /// <summary>Seconds between Play and the first emission.</summary>
    public float StartDelay { get; set; }

    /// <summary>Seconds a particle lives. Evaluated over the cycle (0 to 1) at emission.</summary>
    public MinMaxCurve StartLifetime { get; set; } = 5f;

    /// <summary>Units per second along the emission direction.</summary>
    public MinMaxCurve StartSpeed { get; set; } = 5f;

    /// <summary>Uniform size, or the X size with <see cref="StartSize3D"/>.</summary>
    public MinMaxCurve StartSize { get; set; } = 1f;

    /// <summary>Separate sizes per axis.</summary>
    public bool StartSize3D { get; set; }

    public MinMaxCurve StartSizeY { get; set; } = 1f;

    public MinMaxCurve StartSizeZ { get; set; } = 1f;

    /// <summary>Degrees around the particle's facing axis (2D: Z).</summary>
    public MinMaxCurve StartRotation { get; set; } = 0f;

    public MinMaxGradient StartColor { get; set; } = Color.White;

    /// <summary>Scales <see cref="Gravity"/>; 0 ignores gravity.</summary>
    public MinMaxCurve GravityModifier { get; set; } = 0f;

    /// <summary>World gravity in units per second squared.</summary>
    public Vector3 Gravity { get; set; } = new(0f, -9.81f, 0f);

    /// <summary>Particles alive at once, at most. Emission stops at the limit; nothing is reused early.</summary>
    public int MaxParticles { get; set; } = 1000;

    public ParticleSimulationSpace SimulationSpace { get; set; } = ParticleSimulationSpace.Local;

    /// <summary>Multiplies the time step: 2 runs the system twice as fast.</summary>
    public float SimulationSpeed { get; set; } = 1f;

    public bool PlayOnAwake { get; set; } = true;

    /// <summary>Runs on unscaled time: keeps going while the game is paused (Time.TimeScale 0).</summary>
    public bool UseUnscaledTime { get; set; }

    /// <summary>Seed of the system's random numbers when <see cref="AutoRandomSeed"/> is off: the same seed plays the same particles.</summary>
    public uint RandomSeed { get; set; }

    /// <summary>A new seed on every Play.</summary>
    public bool AutoRandomSeed { get; set; } = true;

    public ParticleStopAction StopAction { get; set; }
}
