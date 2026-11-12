using MNVTEngine.Core.Timing;
using MNVTEngine.Mathematics;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;

namespace MNVTEngine.Particles.Tests;

public sealed class AdvancedModuleTests
{
    private static ParticleSystemSettings Still(Action<ParticleSystemSettings> configure)
    {
        var settings = new ParticleSystemSettings();
        settings.Main.AutoRandomSeed = false;
        settings.Main.RandomSeed = 5;
        settings.Main.StartLifetime = 10f;
        settings.Main.StartSpeed = 0f;
        settings.Main.SimulationSpace = ParticleSimulationSpace.World;
        settings.Emission.RateOverTime = 0f;
        settings.Shape.Kind = ParticleShapeKind.Point;
        configure(settings);
        return settings;
    }

    private static ParticleSimulation Run(ParticleSystemSettings settings, float seconds, int emit = 1, float step = 0.01f)
    {
        var simulation = new ParticleSimulation(settings);
        simulation.Play();
        simulation.Emit(emit, EmitterPose.Identity);
        for (var t = 0f; t < seconds - 1e-4f; t += step)
        {
            simulation.Step(step, EmitterPose.Identity);
        }

        return simulation;
    }

    [Fact]
    public void Noise_IsDeterministic_SmoothAndMovesParticles()
    {
        Assert.Equal(ParticleNoise.Sample(1.3f, 2.7f, 0.4f), ParticleNoise.Sample(1.3f, 2.7f, 0.4f));
        Assert.Equal(0f, ParticleNoise.Sample(3f, 4f, 5f), 5); // zero at lattice points
        Assert.True(Math.Abs(ParticleNoise.Sample(1.30f, 2.7f, 0.4f) - ParticleNoise.Sample(1.31f, 2.7f, 0.4f)) < 0.05f);
        var samples = Enumerable.Range(0, 1000).Select(i => ParticleNoise.Sample(i * 0.37f, i * 0.11f, i * 0.07f)).ToList();
        Assert.All(samples, s => Assert.InRange(s, -1.1f, 1.1f));
        Assert.True(samples.Max() - samples.Min() > 0.8f);

        var turbulent = Run(Still(s => { s.Noise.Enabled = true; s.Noise.Strength = 2f; s.Shape.Kind = ParticleShapeKind.Sphere; s.Shape.Radius = 3f; }), 1f, emit: 20);
        Assert.Contains(turbulent.Particles.Velocity.Take(20), v => v == Vector3.Zero); // their own velocity is untouched...
        var still = Run(Still(s => { s.Shape.Kind = ParticleShapeKind.Sphere; s.Shape.Radius = 3f; }), 1f, emit: 20);
        Assert.NotEqual(still.Particles.Position.Take(20), turbulent.Particles.Position.Take(20)); // ...but they moved
    }

    [Fact]
    public void Collision_BouncesOffPlanes_LosesLifetime_AndRaisesEvents()
    {
        var settings = Still(s =>
        {
            s.Main.GravityModifier = 1f;
            s.Main.StartSize = 0f;
            s.Collision.Enabled = true;
            s.Collision.Planes = [new CollisionPlane { Point = new Vector3(0f, -1f, 0f), Normal = Vector3.Up }];
            s.Collision.Bounce = 1f;
            s.Collision.SendCollisionEvents = true;
        });
        var simulation = new ParticleSimulation(settings);
        simulation.Play();
        simulation.Emit(1, EmitterPose.Identity);
        var hits = 0;
        var lowest = 0f;
        for (var i = 0; i < 200; i++)
        {
            simulation.Step(0.01f, EmitterPose.Identity);
            hits += simulation.Events.AsSpan().ToArray().Count(e => e.Kind == SubEmitterEvent.Collision);
            lowest = Math.Min(lowest, simulation.Particles.Position[0].Y);
            Assert.Equal(1, simulation.CollisionChecksLastStep);
        }

        Assert.True(hits >= 1, "the falling particle hit the floor");
        Assert.True(lowest >= -1.0001f, $"never below the floor (lowest {lowest})");
        Assert.True(simulation.Particles.Position[0].Y > -0.9f || simulation.Particles.Velocity[0].Y > 0f); // bounced back up

        var killed = Run(Still(s =>
        {
            s.Main.GravityModifier = 1f;
            s.Main.StartSize = 0f;
            s.Collision.Enabled = true;
            s.Collision.Planes = [new CollisionPlane { Point = new Vector3(0f, -0.5f, 0f) }];
            s.Collision.LifetimeLoss = 1f;
        }), 1f);
        Assert.Equal(0, killed.Particles.Count); // the first hit used up its life

        var sticky = Run(Still(s =>
        {
            s.Main.GravityModifier = 1f;
            s.Collision.Enabled = true;
            s.Collision.Planes = [new CollisionPlane { Point = new Vector3(0f, -0.5f, 0f) }];
            s.Collision.Bounce = 0f;
            s.Collision.MinKillSpeed = 0.5f;
        }), 1f);
        Assert.Equal(0, sticky.Particles.Count); // stopped dead on impact, below the kill speed
    }

    [Fact]
    public void SubEmitters_EmitFromChildren_OnDeathAndCollision_WithinTheirBudget()
    {
        var scene = new Scene();
        var rocket = scene.CreateGameObject("Rocket").AddComponent<ParticleSystem>();
        rocket.Settings = Still(s =>
        {
            s.Main.StartLifetime = 0.2f;
            s.Main.StartColor = Color.Red;
            s.SubEmitters.Enabled = true;
            s.SubEmitters.Emitters = [new SubEmitter { Event = SubEmitterEvent.Death, Emitter = "Burst", Count = 30, InheritColor = true }];
        });
        var burst = scene.CreateGameObject("Burst", rocket.Transform).AddComponent<ParticleSystem>();
        burst.Settings = Still(s => s.Main.PlayOnAwake = false);
        scene.Start();
        rocket.Emit(2);
        for (var i = 0; i < 5; i++)
        {
            scene.Update(new FrameTime(0.1f, 0.1f, 0.1 * (i + 1), 0.1 * (i + 1), i + 1));
        }

        Assert.Equal(0, rocket.ParticleCount);
        Assert.Equal(60, burst.ParticleCount); // 30 per dying rocket particle
        Assert.Equal(Color.Red, burst.Simulation.Particles.Color[0]); // tinted by the parent particle

        // Runaway protection: at most MaxEmitsPerStep per step.
        rocket.Settings.SubEmitters.MaxEmitsPerStep = 10;
        burst.Clear();
        rocket.Emit(5);
        for (var i = 0; i < 5; i++)
        {
            scene.Update(new FrameTime(0.1f, 0.1f, 1 + (0.1 * i), 1 + (0.1 * i), 10 + i));
        }

        Assert.Equal(10, burst.ParticleCount);

        // A name that is no child emits nothing (no cycles through parents or siblings).
        rocket.Settings.SubEmitters.Emitters[0].Emitter = "Rocket";
        burst.Clear();
        rocket.Emit(1);
        for (var i = 0; i < 5; i++)
        {
            scene.Update(new FrameTime(0.1f, 0.1f, 2 + (0.1 * i), 2 + (0.1 * i), 20 + i));
        }

        Assert.Equal(0, burst.ParticleCount);
    }

    [Fact]
    public void AdvancedModules_RoundTripAndValidate()
    {
        var settings = new ParticleSystemSettings();
        settings.Noise.Enabled = true;
        settings.Collision.Enabled = true;
        settings.Collision.Planes = [new CollisionPlane { Point = new Vector3(0f, 2f, 0f), Normal = Vector3.Down }];
        settings.SubEmitters.Enabled = true;
        settings.SubEmitters.Emitters = [new SubEmitter { Emitter = "Sparks", Event = SubEmitterEvent.Collision }];
        var loaded = ParticleSystemAsset.Parse(ParticleSystemAsset.Serialize(settings), "advanced");
        Assert.Equal(new Vector3(0f, 2f, 0f), Assert.Single(loaded.Collision.Planes).Point); // replaced the default plane, not added to it
        Assert.Equal(("Sparks", SubEmitterEvent.Collision), (loaded.SubEmitters.Emitters[0].Emitter, loaded.SubEmitters.Emitters[0].Event));
        Assert.Equal(["noise", "collision", "subEmitters"], loaded.EnabledModules());

        settings.Collision.Bounce = 2f;
        settings.Collision.Planes[0].Normal = Vector3.Zero;
        var bad = Assert.Throws<SerializationException>(() => ParticleSystemAsset.Parse(ParticleSystemAsset.Serialize(settings), "bad"));
        Assert.Contains(bad.Diagnostics, d => d.Path == "collision");
        Assert.Contains(bad.Diagnostics, d => d.Path == "collision.planes");
    }
}
