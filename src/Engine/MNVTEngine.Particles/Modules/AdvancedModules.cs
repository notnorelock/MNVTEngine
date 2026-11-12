using MNVTEngine.Mathematics;
using Newtonsoft.Json;

namespace MNVTEngine.Particles;

/// <summary>Moves particles through procedural 3D noise (turbulence) without changing their own velocity.</summary>
public sealed class NoiseModule
{
    public bool Enabled { get; set; }

    /// <summary>Units per second at full noise, over the particle's life; the X strength with <see cref="SeparateAxes"/>.</summary>
    public MinMaxCurve Strength { get; set; } = 1f;

    public bool SeparateAxes { get; set; }

    public MinMaxCurve StrengthY { get; set; } = 1f;

    public MinMaxCurve StrengthZ { get; set; } = 1f;

    /// <summary>Noise cells per unit: higher is finer turbulence.</summary>
    public float Frequency { get; set; } = 0.5f;

    /// <summary>How fast the noise field moves, in cells per second.</summary>
    public float ScrollSpeed { get; set; }
}

/// <summary>An infinite plane particles collide with, in world space.</summary>
public sealed class CollisionPlane
{
    public Vector3 Point { get; set; }

    /// <summary>The side particles stay on (normalized when used).</summary>
    public Vector3 Normal { get; set; } = Vector3.Up;
}

/// <summary>
/// Collisions with world planes (floors, walls, water surfaces). Physics colliders are not used: the particle module
/// does not depend on the physics modules.
/// </summary>
public sealed class CollisionModule
{
    public bool Enabled { get; set; }

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)] // a loaded list replaces the default one instead of adding to it
    public List<CollisionPlane> Planes { get; set; } = [new CollisionPlane()];

    /// <summary>Velocity kept along the normal after a hit: 0 stops, 1 bounces fully.</summary>
    public float Bounce { get; set; } = 0.5f;

    /// <summary>Velocity lost along the surface on a hit, 0 to 1.</summary>
    public float Dampen { get; set; }

    /// <summary>Part of its lifetime a particle loses per hit, 0 to 1 (1 kills it on the first hit).</summary>
    public float LifetimeLoss { get; set; }

    /// <summary>Particles slower than this after a hit die.</summary>
    public float MinKillSpeed { get; set; }

    /// <summary>Collision radius as a multiple of half the particle's size (0 collides at the center).</summary>
    public float RadiusScale { get; set; } = 1f;

    /// <summary>Raise <see cref="ParticleSystem.Collided"/> per hit and allow collision sub-emitters.</summary>
    public bool SendCollisionEvents { get; set; }
}

/// <summary>When a sub-emitter emits.</summary>
public enum SubEmitterEvent
{
    Birth,
    Death,
    Collision,
}

/// <summary>Another particle system that emits where this system's particles are born, die or collide.</summary>
public sealed class SubEmitter
{
    public SubEmitterEvent Event { get; set; } = SubEmitterEvent.Death;

    /// <summary>Name of a child object (at any depth) with the particle system to emit from. Children only, so emitters cannot form cycles.</summary>
    public string Emitter { get; set; } = "";

    /// <summary>Particles emitted per event.</summary>
    public int Count { get; set; } = 5;

    /// <summary>Chance (0 to 1) of emitting for each event.</summary>
    public float Probability { get; set; } = 1f;

    /// <summary>Part of the parent particle's velocity added to the emitted particles, 0 to 1.</summary>
    public float InheritVelocity { get; set; }

    /// <summary>Tints the emitted particles with the parent particle's color.</summary>
    public bool InheritColor { get; set; }
}

public sealed class SubEmittersModule
{
    public bool Enabled { get; set; }

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)] // a loaded list replaces the default one instead of adding to it
    public List<SubEmitter> Emitters { get; set; } = [];

    /// <summary>At most this many particles emitted by sub-emitters per step (runaway protection).</summary>
    public int MaxEmitsPerStep { get; set; } = 1000;
}
