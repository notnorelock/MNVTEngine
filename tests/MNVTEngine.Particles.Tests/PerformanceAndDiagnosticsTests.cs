using MNVTEngine.Diagnostics;
using MNVTEngine.Scenes;

namespace MNVTEngine.Particles.Tests;

public sealed class PerformanceAndDiagnosticsTests
{
    private static ParticleSimulation Large()
    {
        var settings = new ParticleSystemSettings();
        settings.Main.MaxParticles = 50_000;
        settings.Main.AutoRandomSeed = false;
        settings.Main.StartLifetime = new MinMaxCurve(0.5f, 3f);
        settings.Main.GravityModifier = 1f;
        settings.Emission.RateOverTime = 0f;
        settings.Shape.Kind = ParticleShapeKind.Sphere;
        settings.ColorOverLifetime.Enabled = settings.SizeOverLifetime.Enabled = settings.Noise.Enabled = settings.Collision.Enabled = true;
        settings.SubEmitters.Enabled = true; // death events recorded in the serial sweep, in both modes
        settings.SubEmitters.Emitters = [new SubEmitter { Event = SubEmitterEvent.Death, Emitter = "none" }];
        var simulation = new ParticleSimulation(settings);
        simulation.Play();
        simulation.Emit(50_000, EmitterPose.Identity);
        simulation.Events.Configure(settings);
        return simulation;
    }

    [Fact]
    public void ParallelUpdate_GivesExactlyTheSerialResult()
    {
        var serial = Large();
        var parallel = Large();
        var workers = new ParticleWorkers();
        for (var i = 0; i < 30; i++)
        {
            serial.Events.Clear();
            parallel.Events.Clear();
            var a = ParticleUpdater.Update(serial.Particles, serial.Settings, 0.05f, EmitterPose.Identity, serial.Events, i * 0.05f);
            var b = ParticleUpdater.Update(parallel.Particles, parallel.Settings, 0.05f, EmitterPose.Identity, parallel.Events, i * 0.05f, workers);
            Assert.Equal(a, b);
            Assert.Equal(serial.Events.Count, parallel.Events.Count);
        }

        Assert.True(serial.Particles.Count > ParticleUpdater.ParallelThreshold / 2);
        Assert.Equal(serial.Particles.Position.Take(serial.Particles.Count), parallel.Particles.Position.Take(parallel.Particles.Count));
        Assert.Equal(serial.Particles.Color.Take(serial.Particles.Count), parallel.Particles.Color.Take(parallel.Particles.Count));
    }

    [Fact]
    public void Steps_DoNotAllocate_InSerialOrParallel()
    {
        foreach (var count in new[] { 1_000, 50_000 })
        {
            var settings = new ParticleSystemSettings();
            settings.Main.MaxParticles = count;
            settings.Main.StartLifetime = 100f;
            settings.Emission.RateOverTime = 0f;
            settings.ColorOverLifetime.Enabled = settings.SizeOverLifetime.Enabled = settings.RotationOverLifetime.Enabled = true;
            var simulation = new ParticleSimulation(settings);
            simulation.Play();
            simulation.Emit(count, EmitterPose.Identity);
            for (var i = 0; i < 5; i++)
            {
                simulation.Step(0.016f, EmitterPose.Identity); // warm up
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 20; i++)
            {
                simulation.Step(0.016f, EmitterPose.Identity);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }

    [Fact]
    public void Metrics_SumTheScenesSystems_AndGpuTimeIsUnsupported()
    {
        var scene = new Scene();
        var system = scene.CreateGameObject("Fire").AddComponent<ParticleSystem>();
        system.MaxParticles = 300;
        system.Settings.Noise.Enabled = true;
        system.Emit(40);
        system.Simulation.Step(0.01f, system.Pose());
        var provider = Assert.Single(new ParticlesModule().CreateMetricsProviders(() => scene));
        var metrics = new MetricsSnapshot { Frame = 1 };
        provider.Collect(metrics);

        Assert.Equal(1, metrics.Get(Metrics.ParticleSystems).Value);
        Assert.Equal(40, metrics.Get(Metrics.ParticlesAlive).Value);
        Assert.Equal(300, metrics.Get(Metrics.ParticleCapacity).Value);
        Assert.Equal(1, metrics.Get(Metrics.ParticleModules).Value);
        Assert.True(metrics.Get(Metrics.ParticleMemory).Value > 0);
        Assert.Equal(MetricStatus.Measured, metrics.Get(Metrics.ParticleSimulationTime).Status);
        Assert.Equal(MetricStatus.Unsupported, metrics.Get(Metrics.ParticleGpuSimulationTime).Status); // never a fake 0
    }
}
