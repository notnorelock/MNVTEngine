using MNVTEngine.Mathematics;

namespace MNVTEngine.Particles.Tests;

public sealed class SimulationTests
{
    private static ParticleSystemSettings Settings(Action<ParticleSystemSettings>? configure = null)
    {
        var settings = new ParticleSystemSettings();
        settings.Main.AutoRandomSeed = false;
        settings.Main.RandomSeed = 1234;
        settings.Main.StartLifetime = 10f;
        settings.Emission.RateOverTime = 0f;
        settings.Shape.Kind = ParticleShapeKind.Point;
        configure?.Invoke(settings);
        return settings;
    }

    private static ParticleSimulation Run(ParticleSystemSettings settings, float seconds, float step = 0.02f, EmitterPose? pose = null)
    {
        var simulation = new ParticleSimulation(settings);
        simulation.Play();
        for (var t = 0f; t < seconds - 1e-4f; t += step)
        {
            simulation.Step(step, pose ?? EmitterPose.Identity);
        }

        return simulation;
    }

    [Fact]
    public void RateOverTime_EmitsContinuously_WithAnAccumulator()
    {
        var simulation = Run(Settings(s => s.Emission.RateOverTime = 30f), 1f);
        Assert.InRange(simulation.Particles.Count, 29, 30); // fractional particles carry over between steps
        Assert.Equal(simulation.Particles.Count, simulation.TotalEmitted);

        var hitch = new ParticleSimulation(Settings(s => s.Emission.RateOverTime = 100f));
        hitch.Play();
        hitch.Step(5f, EmitterPose.Identity); // a 5 s frame emits at most the catch-up window, not 500 particles
        Assert.Equal((int)(100f * ParticleSimulation.MaxCatchUpSeconds), hitch.Particles.Count);
    }

    [Fact]
    public void Bursts_FireAtTheirTime_RepeatAndLoop()
    {
        var settings = Settings(s =>
        {
            s.Main.Duration = 1f;
            s.Emission.AddBurst(new ParticleBurst { Time = 0.5f, Count = 50 });
            s.Emission.AddBurst(new ParticleBurst { Time = 0f, Count = 2, Cycles = 3, Interval = 0.1f });
        });
        var simulation = new ParticleSimulation(settings);
        simulation.Play();
        simulation.Step(0.01f, EmitterPose.Identity);
        Assert.Equal(2, simulation.Particles.Count);
        simulation.Step(0.45f, EmitterPose.Identity);
        Assert.Equal(6, simulation.Particles.Count); // all three repeats, not the 0.5 s burst yet
        simulation.Step(0.05f, EmitterPose.Identity);
        Assert.Equal(56, simulation.Particles.Count);
        simulation.Step(0.5f, EmitterPose.Identity); // the next cycle starts: the repeating burst fires again
        Assert.Equal(58, simulation.Particles.Count);

        var never = Run(Settings(s => s.Emission.AddBurst(new ParticleBurst { Count = 10, Probability = 0f })), 0.1f);
        Assert.Equal(0, never.Particles.Count);
    }

    [Fact]
    public void Lifetime_RecyclesDeadParticles_AndNonLoopingSystemsFinish()
    {
        var settings = Settings(s =>
        {
            s.Main.Duration = 0.5f;
            s.Main.Loop = false;
            s.Main.StartLifetime = 0.25f;
            s.Emission.AddBurst(new ParticleBurst { Count = 10 });
        });
        var simulation = new ParticleSimulation(settings);
        var finished = 0;
        simulation.Finished += () => finished++;
        simulation.Play();
        simulation.Step(0.1f, EmitterPose.Identity);
        Assert.Equal(10, simulation.Particles.Count);
        simulation.Step(0.2f, EmitterPose.Identity);
        Assert.Equal((0, 10), (simulation.Particles.Count, simulation.DiedLastStep));
        Assert.Equal(ParticlePlayState.Playing, simulation.State); // the cycle is not over yet
        simulation.Step(0.3f, EmitterPose.Identity);
        Assert.Equal((ParticlePlayState.Stopped, 1, false), (simulation.State, finished, simulation.IsEmitting));
    }

    [Fact]
    public void MaxParticles_CapsEmission_AndStopModesBehave()
    {
        var simulation = Run(Settings(s => { s.Main.MaxParticles = 20; s.Emission.RateOverTime = 1000f; }), 0.5f);
        Assert.Equal(20, simulation.Particles.Count);
        Assert.Equal(20, simulation.Particles.Capacity); // grows only to the limit

        simulation.Stop();
        simulation.Step(0.1f, EmitterPose.Identity);
        Assert.Equal((20, false), (simulation.Particles.Count, simulation.IsEmitting)); // alive particles keep living
        simulation.Stop(ParticleStopBehavior.StopEmittingAndClear);
        Assert.Equal(0, simulation.Particles.Count);

        Assert.Equal(5, new ParticleSimulation(Settings(s => s.Main.MaxParticles = 5)).Emit(100, EmitterPose.Identity));
    }

    [Fact]
    public void SimulationSpeed_Pause_AndStartDelay()
    {
        var fast = Run(Settings(s => { s.Emission.RateOverTime = 10f; s.Main.SimulationSpeed = 2f; }), 1f);
        Assert.InRange(fast.Particles.Count, 19, 20);

        var paused = new ParticleSimulation(Settings(s => s.Emission.RateOverTime = 10f));
        paused.Play();
        paused.Pause();
        paused.Step(1f, EmitterPose.Identity);
        Assert.Equal((0, ParticlePlayState.Paused), (paused.Particles.Count, paused.State));
        paused.Play();
        Assert.Equal(ParticlePlayState.Playing, paused.State);

        var delayed = Run(Settings(s => { s.Main.StartDelay = 0.5f; s.Emission.AddBurst(new ParticleBurst { Count = 3 }); }), 0.4f);
        Assert.Equal(0, delayed.Particles.Count);
        delayed.Step(0.2f, EmitterPose.Identity);
        Assert.Equal(3, delayed.Particles.Count);
    }

    [Fact]
    public void SameSeed_SameParticles_DifferentSeed_DifferentParticles()
    {
        ParticleSystemSettings Random(uint seed) => Settings(s =>
        {
            s.Main.RandomSeed = seed;
            s.Main.StartSpeed = new MinMaxCurve(1f, 5f);
            s.Shape.Kind = ParticleShapeKind.Sphere;
            s.Emission.RateOverTime = 50f;
        });

        var a = Run(Random(7), 1f);
        var b = Run(Random(7), 1f);
        var c = Run(Random(8), 1f);
        Assert.Equal(a.Particles.Position.Take(a.Particles.Count), b.Particles.Position.Take(b.Particles.Count));
        Assert.NotEqual(a.Particles.Position.Take(a.Particles.Count), c.Particles.Position.Take(c.Particles.Count));
    }

    [Fact]
    public void LocalSpace_MovesWithTheEmitter_WorldSpace_DoesNot()
    {
        var moved = new EmitterPose(Matrix4x4.Translate(new Vector3(10f, 0f, 0f)), Quaternion.Identity, new Vector3(10f, 0f, 0f));
        ParticleSimulation Emit(ParticleSimulationSpace space)
        {
            var simulation = new ParticleSimulation(Settings(s => { s.Main.SimulationSpace = space; s.Main.StartSpeed = 0f; }));
            simulation.Emit(1, moved);
            return simulation;
        }

        Assert.Equal(Vector3.Zero, Emit(ParticleSimulationSpace.Local).Particles.Position[0]); // relative to the emitter
        Assert.Equal(new Vector3(10f, 0f, 0f), Emit(ParticleSimulationSpace.World).Particles.Position[0]);

        // World space: velocity follows the emitter's rotation at emission.
        var turned = new EmitterPose(Matrix4x4.Identity, Quaternion.Euler(0f, 0f, -90f), Vector3.Zero);
        var world = new ParticleSimulation(Settings(s => { s.Main.SimulationSpace = ParticleSimulationSpace.World; s.Main.StartSpeed = 1f; }));
        world.Emit(1, turned);
        Assert.True(Vector3.Distance(world.Particles.Velocity[0], Vector3.Right) < 1e-4f);
    }

    [Fact]
    public void Velocity_Gravity_AndForce()
    {
        var simulation = Run(Settings(s => { s.Main.StartSpeed = 2f; s.Emission.AddBurst(new ParticleBurst { Count = 1 }); }), 1f, 0.01f);
        Assert.Equal(2f, simulation.Particles.Position[0].Y, 1); // point shape: +Y at 2 units per second

        var falling = Run(Settings(s => { s.Main.StartSpeed = 0f; s.Main.GravityModifier = 1f; s.Emission.AddBurst(new ParticleBurst { Count = 1 }); }), 1f, 0.001f);
        Assert.Equal(-9.81f, falling.Particles.Velocity[0].Y, 1);
        Assert.Equal(-4.9f, falling.Particles.Position[0].Y, 1);

        var pushed = Run(Settings(s =>
        {
            s.Main.StartSpeed = 0f;
            s.ForceOverLifetime.Enabled = true;
            s.ForceOverLifetime.X = 2f;
            s.VelocityOverLifetime.Enabled = true;
            s.VelocityOverLifetime.Z = 1f;
            s.Emission.AddBurst(new ParticleBurst { Count = 1 });
        }), 1f, 0.001f);
        var p = pushed.Particles;
        Assert.Equal(2f, p.Velocity[0].X, 2); // forces change velocity; velocity over lifetime does not
        Assert.Equal(0f, p.Velocity[0].Z, 2);
        Assert.Equal(1f, p.Position[0].X, 1);
        Assert.Equal(1f, p.Position[0].Z, 1);
    }

    [Fact]
    public void ColorSizeRotationAndFrames_OverLifetime()
    {
        var settings = Settings(s =>
        {
            s.Main.StartLifetime = 1f;
            s.Main.StartSize = 2f;
            s.Main.StartColor = Color.Red;
            s.ColorOverLifetime.Enabled = true; // default: white to transparent
            s.SizeOverLifetime.Enabled = true; // default: 1 to 0
            s.RotationOverLifetime.Enabled = true;
            s.RotationOverLifetime.AngularVelocity = 90f;
            s.TextureSheetAnimation.Enabled = true;
            s.TextureSheetAnimation.Columns = 2;
            s.TextureSheetAnimation.Rows = 2;
            s.Emission.AddBurst(new ParticleBurst { Count = 1 });
        });
        var simulation = Run(settings, 0.5f, 0.01f);
        var p = simulation.Particles;
        Assert.Equal(1f, p.Color[0].R, 2);
        Assert.Equal(0f, p.Color[0].G, 2);
        Assert.Equal(0.5f, p.Color[0].A, 1);
        Assert.Equal(1f, p.Size[0].X, 1);
        Assert.Equal(45f, p.Rotation[0].Z, 0);
        Assert.InRange(p.Frame[0], 1, 2); // halfway through four frames (on the boundary between frames 1 and 2)
        Assert.Equal(3, ParticleUpdater.Frame(settings.TextureSheetAnimation, 0.8f, 0.8f, p.Seed[0]));
        Assert.Equal(0, ParticleUpdater.Frame(settings.TextureSheetAnimation, 0.1f, 0.1f, p.Seed[0]));

        var noModules = Run(Settings(s => { s.Main.StartSize = 2f; s.Emission.AddBurst(new ParticleBurst { Count = 1 }); }), 0.5f);
        Assert.Equal((2f, 1f), (noModules.Particles.Size[0].X, noModules.Particles.Color[0].A)); // disabled modules leave the start values
    }
}
