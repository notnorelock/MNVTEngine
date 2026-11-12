using MNVTEngine.Scenes;

namespace MNVTEngine.Particles;

/// <summary>The "particles" engine module: the particle system component (simulation only; particles.2d and particles.3d draw).</summary>
public sealed class ParticlesModule : IEngineModule, IDiagnosticsIntegration
{
    public string Id => "particles";

    public void Register(ComponentRegistry components) => ParticleComponents.Register(components);

    public IEnumerable<Diagnostics.IMetricsProvider> CreateMetricsProviders(Func<Scene?> scene) => [new ParticleMetrics(scene)];

    /// <summary>Simulation metrics summed over the running scene's systems (rendering metrics come from the passes).</summary>
    private sealed class ParticleMetrics(Func<Scene?> scene) : Diagnostics.IMetricsProvider
    {
        public string Name => "Particles";

        public void Collect(Diagnostics.MetricsSnapshot metrics)
        {
            if (scene() is not { } current)
            {
                return;
            }

            int systems = 0, alive = 0, capacity = 0, emitted = 0, recycled = 0, modules = 0, checks = 0;
            long memory = 0;
            var time = 0.0;
            foreach (var system in current.World.Pool<ParticleSystem>().Components)
            {
                if (!system.IsAlive)
                {
                    continue;
                }

                var simulation = system.Simulation;
                systems++;
                alive += simulation.Particles.Count;
                capacity += system.Main.MaxParticles;
                emitted += simulation.EmittedLastStep;
                recycled += simulation.DiedLastStep;
                checks += simulation.CollisionChecksLastStep;
                memory += simulation.Particles.MemoryBytes;
                time += simulation.LastStepMilliseconds;
                modules += system.Settings.EnabledModules().Count();
            }

            metrics.Set(Diagnostics.Metrics.ParticleSystems, systems);
            metrics.Set(Diagnostics.Metrics.ParticlesAlive, alive);
            metrics.Set(Diagnostics.Metrics.ParticleCapacity, capacity);
            metrics.Set(Diagnostics.Metrics.ParticlesEmitted, emitted);
            metrics.Set(Diagnostics.Metrics.ParticlesRecycled, recycled);
            metrics.Set(Diagnostics.Metrics.ParticleModules, modules);
            metrics.Set(Diagnostics.Metrics.ParticleCollisionChecks, checks);
            metrics.Set(Diagnostics.Metrics.ParticleMemory, memory);
            metrics.Set(Diagnostics.Metrics.ParticleSimulationTime, time);
            metrics.SetUnsupported(Diagnostics.Metrics.ParticleGpuSimulationTime); // no GPU simulation: never reported as 0
        }
    }
}

public static class ParticleComponents
{
    public static ComponentRegistry Register(ComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Register<ParticleSystem>();
    }
}
