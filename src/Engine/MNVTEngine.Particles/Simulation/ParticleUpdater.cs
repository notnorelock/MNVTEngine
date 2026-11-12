using MNVTEngine.Mathematics;

namespace MNVTEngine.Particles;

/// <summary>
/// Ages and moves particles and applies the lifetime modules, in two phases: an update of every particle (independent
/// per particle, so large systems run it in parallel chunks, see <see cref="ParticleWorkers"/>), then a sweep that
/// removes the dead and records their death events (<see cref="ParticleBuffer.RemoveAt"/>). A disabled module is
/// skipped for the whole step, so it costs nothing per particle.
/// </summary>
public static class ParticleUpdater
{
    /// <summary>Systems with at least this many particles update in parallel (when given workers).</summary>
    public const int ParallelThreshold = 32768;

    /// <param name="events">Where deaths and collisions are recorded when it asks for them; null records nothing.</param>
    /// <param name="time">Seconds the system has run (scrolls the noise field).</param>
    /// <param name="workers">Parallel workers for large systems; null updates on the calling thread.</param>
    /// <returns>Particles that died this step, and the particle-plane collision tests made.</returns>
    public static (int Died, int CollisionChecks) Update(ParticleBuffer particles, ParticleSystemSettings settings, float dt, in EmitterPose pose, ParticleEventBuffer? events = null, float time = 0f, ParticleWorkers? workers = null)
    {
        ArgumentNullException.ThrowIfNull(particles);
        ArgumentNullException.ThrowIfNull(settings);
        var step = new ParticleStep(particles, settings, dt, pose, events, time);

        // Collision events go to one shared buffer: with them the update stays on this thread.
        var checks = workers is not null && particles.Count >= ParallelThreshold && !step.RecordsCollisions
            ? workers.Run(step, particles.Count)
            : step.Update(0, particles.Count);

        var died = 0;
        var recordDeaths = events?.RecordDeaths ?? false;
        for (var i = particles.Count - 1; i >= 0; i--)
        {
            if (particles.Age[i] < particles.Lifetime[i])
            {
                continue;
            }

            if (recordDeaths)
            {
                events!.Add(new ParticleEvent(SubEmitterEvent.Death, step.ToWorld(particles.Position[i]), step.ToWorldDirection(particles.Velocity[i]), particles.Color[i], Vector3.Zero));
            }

            particles.RemoveAt(i); // the last particle moves here; it was checked already (the sweep runs backwards)
            died++;
        }

        return (died, checks);
    }

    /// <summary>The sprite-sheet frame of a particle at normalized age <paramref name="t"/>.</summary>
    public static int Frame(TextureSheetAnimationModule sheet, float t, float age, uint seed)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        var frames = sheet.FrameCount;
        var start = sheet.StartFrame.Evaluate(0f, ParticleRandom.Value(seed, Salt.StartFrame));
        float position;
        if (sheet.TimeMode == TextureSheetTimeMode.FramesPerSecond)
        {
            position = age * Math.Max(0f, sheet.FramesPerSecond);
        }
        else
        {
            var phase = t * Math.Max(0f, sheet.Cycles);
            var within = sheet.Loop ? phase - MathF.Floor(phase) : Math.Min(phase, 0.99999f);
            position = sheet.FrameOverTime.Evaluate(within, ParticleRandom.Value(seed, Salt.FrameOverTime)) * frames;
        }

        var frame = (int)MathF.Floor(start + position);
        return sheet.Loop ? ((frame % frames) + frames) % frames : Math.Clamp(frame, 0, frames - 1);
    }
}

/// <summary>
/// One step's per-particle work. A dying particle is only marked (its age reaches its lifetime); the sweep in
/// <see cref="ParticleUpdater.Update"/> removes it. Particles do not depend on each other, so ranges can run on
/// different threads; only collision events (a shared buffer) require a single thread.
/// </summary>
internal readonly struct ParticleStep
{
    private readonly ParticleBuffer _particles;
    private readonly ParticleSystemSettings _settings;
    private readonly float _dt;
    private readonly float _time;
    private readonly EmitterPose _pose;
    private readonly Quaternion _toLocal;
    private readonly Matrix4x4 _worldToLocal;
    private readonly Vector3 _gravity;
    private readonly bool _local;
    private readonly bool _gravityOn;
    private readonly ParticleEventBuffer? _collisionEvents;

    public ParticleStep(ParticleBuffer particles, ParticleSystemSettings settings, float dt, in EmitterPose pose, ParticleEventBuffer? events, float time)
    {
        (_particles, _settings, _dt, _time, _pose) = (particles, settings, dt, time, pose);
        var main = settings.Main;
        _local = main.SimulationSpace == ParticleSimulationSpace.Local;
        _toLocal = pose.Rotation.Inverse;
        _worldToLocal = _local && settings.Collision.Enabled ? pose.LocalToWorld.Inverse : Matrix4x4.Identity;
        _gravityOn = main.GravityModifier.Mode != CurveMode.Constant || main.GravityModifier.Max != 0f;
        _gravity = _local ? _toLocal * main.Gravity : main.Gravity;
        _collisionEvents = events is { RecordCollisions: true } ? events : null;
    }

    public bool RecordsCollisions => _collisionEvents is not null;

    public Vector3 ToWorld(Vector3 position) => _local ? _pose.LocalToWorld.MultiplyPoint3x4(position) : position;

    public Vector3 ToWorldDirection(Vector3 direction) => _local ? _pose.Rotation * direction : direction;

    /// <summary>Updates particles [start, end); returns the collision tests made.</summary>
    public int Update(int start, int end)
    {
        var particles = _particles;
        var settings = _settings;
        var dt = _dt;
        var main = settings.Main;
        var force = settings.ForceOverLifetime.Enabled ? settings.ForceOverLifetime : null;
        var velocity = settings.VelocityOverLifetime.Enabled ? settings.VelocityOverLifetime : null;
        var color = settings.ColorOverLifetime.Enabled ? settings.ColorOverLifetime.Color : null;
        var size = settings.SizeOverLifetime.Enabled ? settings.SizeOverLifetime : null;
        var rotation = settings.RotationOverLifetime.Enabled ? settings.RotationOverLifetime : null;
        var sheet = settings.TextureSheetAnimation.Enabled ? settings.TextureSheetAnimation : null;
        var noise = settings.Noise.Enabled ? settings.Noise : null;
        var collision = settings.Collision.Enabled && settings.Collision.Planes.Count > 0 ? settings.Collision : null;
        var checks = 0;
        for (var i = start; i < end; i++)
        {
            var age = particles.Age[i] + dt;
            var lifetime = particles.Lifetime[i];
            particles.Age[i] = age;
            if (age >= lifetime)
            {
                continue; // dead: removed by the sweep
            }

            var t = age / lifetime;
            var seed = particles.Seed[i];
            var v = particles.Velocity[i];
            if (_gravityOn)
            {
                v += _gravity * (main.GravityModifier.Evaluate(t, ParticleRandom.Value(seed, Salt.Gravity)) * dt);
            }

            if (force is not null)
            {
                var f = new Vector3(force.X.Evaluate(t, ParticleRandom.Value(seed, Salt.Force)), force.Y.Evaluate(t, ParticleRandom.Value(seed, Salt.ForceY)), force.Z.Evaluate(t, ParticleRandom.Value(seed, Salt.ForceZ)));
                v += InSimulationSpace(f, force.Space) * dt;
            }

            particles.Velocity[i] = v;
            if (velocity is not null)
            {
                var extra = new Vector3(velocity.X.Evaluate(t, ParticleRandom.Value(seed, Salt.Velocity)), velocity.Y.Evaluate(t, ParticleRandom.Value(seed, Salt.VelocityY)), velocity.Z.Evaluate(t, ParticleRandom.Value(seed, Salt.VelocityZ)));
                v = (v + InSimulationSpace(extra, velocity.Space)) * velocity.SpeedModifier.Evaluate(t, ParticleRandom.Value(seed, Salt.SpeedModifier));
            }

            if (noise is not null)
            {
                var r = ParticleRandom.Value(seed, Salt.Noise);
                var x = noise.Strength.Evaluate(t, r);
                var strength = noise.SeparateAxes ? new Vector3(x, noise.StrengthY.Evaluate(t, r), noise.StrengthZ.Evaluate(t, r)) : new Vector3(x, x, x);
                var sample = ParticleNoise.Vector((particles.Position[i] * noise.Frequency) + new Vector3(0f, 0f, _time * noise.ScrollSpeed), 0f);
                v += Vector3.Scale(sample, strength);
            }

            particles.Position[i] += v * dt;
            if (collision is not null)
            {
                checks += collision.Planes.Count;
                if (Collide(i, collision))
                {
                    continue; // died on impact
                }
            }

            if (rotation is not null)
            {
                var r = ParticleRandom.Value(seed, Salt.RotationOverLifetime);
                var spin = rotation.SeparateAxes
                    ? new Vector3(rotation.X.Evaluate(t, r), rotation.Y.Evaluate(t, r), rotation.AngularVelocity.Evaluate(t, r))
                    : new Vector3(0f, 0f, rotation.AngularVelocity.Evaluate(t, r));
                particles.AngularVelocity[i] = spin;
                particles.Rotation[i] += spin * dt;
            }

            if (size is not null)
            {
                var r = ParticleRandom.Value(seed, Salt.SizeOverLifetime);
                var x = size.Size.Evaluate(t, r);
                particles.Size[i] = Vector3.Scale(particles.StartSize[i], size.SeparateAxes ? new Vector3(x, size.Y.Evaluate(t, r), size.Z.Evaluate(t, r)) : new Vector3(x, x, x));
            }

            if (color is not null)
            {
                var start2 = particles.StartColor[i];
                var c = color.Evaluate(t, ParticleRandom.Value(seed, Salt.ColorOverLifetime));
                particles.Color[i] = new Color(start2.R * c.R, start2.G * c.G, start2.B * c.B, start2.A * c.A);
            }

            if (sheet is not null)
            {
                particles.Frame[i] = ParticleUpdater.Frame(sheet, t, age, seed);
            }
        }

        return checks;
    }

    /// <summary>
    /// Pushes particle <paramref name="i"/> out of every plane it entered and reflects its velocity. Returns true when it
    /// died of the hit (lifetime loss or minimum kill speed): its age is set to its lifetime for the sweep.
    /// </summary>
    private bool Collide(int i, CollisionModule collision)
    {
        var particles = _particles;
        var position = ToWorld(particles.Position[i]);
        var velocity = ToWorldDirection(particles.Velocity[i]);
        var radius = Math.Max(0f, collision.RadiusScale) * 0.5f * Math.Max(particles.Size[i].X, particles.Size[i].Y);
        var hit = false;
        foreach (var plane in collision.Planes)
        {
            var length = plane.Normal.Magnitude;
            if (length < 1e-6f)
            {
                continue;
            }

            var normal = plane.Normal * (1f / length);
            var depth = Vector3.Dot(position - plane.Point, normal) - radius;
            var into = Vector3.Dot(velocity, normal);
            if (depth >= 0f || into >= 0f)
            {
                continue;
            }

            position -= normal * depth;
            var along = velocity - (normal * into);
            velocity = (along * (1f - Math.Clamp(collision.Dampen, 0f, 1f))) - (normal * (into * Math.Clamp(collision.Bounce, 0f, 1f)));
            hit = true;
            _collisionEvents?.Add(new ParticleEvent(SubEmitterEvent.Collision, position, velocity, particles.Color[i], normal));
        }

        if (!hit)
        {
            return false;
        }

        particles.Position[i] = _local ? _worldToLocal.MultiplyPoint3x4(position) : position;
        particles.Velocity[i] = _local ? _toLocal * velocity : velocity;
        particles.Age[i] += particles.Lifetime[i] * Math.Clamp(collision.LifetimeLoss, 0f, 1f);
        if (particles.Age[i] >= particles.Lifetime[i] || velocity.Magnitude < collision.MinKillSpeed)
        {
            particles.Age[i] = particles.Lifetime[i];
            return true;
        }

        return false;
    }

    /// <summary>A module vector given in <paramref name="space"/>, in the simulation's space.</summary>
    private Vector3 InSimulationSpace(Vector3 v, ParticleSpace space) =>
        (space, _local) switch
        {
            (ParticleSpace.Local, false) => _pose.Rotation * v,
            (ParticleSpace.World, true) => _toLocal * v,
            _ => v,
        };
}

/// <summary>
/// Runs a step's particle update in parallel chunks on the thread pool, for systems over
/// <see cref="ParticleUpdater.ParallelThreshold"/> particles. One per simulation. Work items and the completion check are
/// reused, so a parallel step allocates nothing; the calling thread runs a chunk too and then waits for every chunk,
/// so the particles are never read and written at the same time. An exception in a chunk is rethrown on the caller.
/// </summary>
public sealed class ParticleWorkers
{
    private const int ChunkSize = 4096;
    private readonly Func<bool> _finished;
    private int _remaining;
    private WorkItem[] _items = [];
    private ParticleStep _step;
    private int _count;
    private int _checks;
    private System.Runtime.ExceptionServices.ExceptionDispatchInfo? _error;

    public ParticleWorkers() => _finished = () => Volatile.Read(ref _remaining) == 0;

    internal int Run(in ParticleStep step, int count)
    {
        var chunks = (count + ChunkSize - 1) / ChunkSize;
        (_step, _count, _checks, _error) = (step, count, 0, null);
        if (_items.Length < chunks)
        {
            var grown = new WorkItem[chunks];
            _items.CopyTo(grown, 0);
            for (var i = _items.Length; i < chunks; i++)
            {
                grown[i] = new WorkItem(this, i);
            }

            _items = grown;
        }

        Volatile.Write(ref _remaining, chunks);
        for (var i = 1; i < chunks; i++)
        {
            ThreadPool.UnsafeQueueUserWorkItem(_items[i], preferLocal: false);
        }

        _items[0].Execute();
        SpinWait.SpinUntil(_finished); // spins briefly, then yields: chunks take well under a frame
        _error?.Throw();
        return _checks;
    }

    private void Chunk(int index)
    {
        try
        {
            var start = index * ChunkSize;
            Interlocked.Add(ref _checks, _step.Update(start, Math.Min(start + ChunkSize, _count)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _error ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); // rethrown on the calling thread
        }
        finally
        {
            Interlocked.Decrement(ref _remaining);
        }
    }

    private sealed class WorkItem(ParticleWorkers owner, int index) : IThreadPoolWorkItem
    {
        public void Execute() => owner.Chunk(index);
    }
}
