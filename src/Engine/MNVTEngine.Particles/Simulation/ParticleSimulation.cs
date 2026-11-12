using MNVTEngine.Mathematics;

namespace MNVTEngine.Particles;

public enum ParticlePlayState
{
    Stopped,
    Playing,
    Paused,
}

public enum ParticleStopBehavior
{
    /// <summary>No new particles; alive ones finish their life.</summary>
    StopEmitting,

    /// <summary>No new particles, and alive ones are removed.</summary>
    StopEmittingAndClear,
}

/// <summary>Where the emitter is this step: its transform, rotation and position in the world.</summary>
public readonly record struct EmitterPose(Matrix4x4 LocalToWorld, Quaternion Rotation, Vector3 Position)
{
    public static EmitterPose Identity => new(Matrix4x4.Identity, Quaternion.Identity, Vector3.Zero);
}

/// <summary>
/// One particle system's simulation on the CPU: play state, cycle time, emission (rate over time and distance through
/// accumulators, bursts) and the particle update (<see cref="ParticleUpdater"/>). Deterministic for a seed and the
/// same steps. Allocation-free after the buffer has grown to its working size. Not thread-safe: step and read on the
/// thread that owns the scene.
/// </summary>
public sealed class ParticleSimulation
{
    /// <summary>
    /// Continuous emission covers at most this much time per step: after a hitch the system resumes instead of emitting
    /// the backlog all at once.
    /// </summary>
    public const float MaxCatchUpSeconds = 0.25f;

    private const int MaxCyclesPerStep = 4;
    private int[] _burstsFired = [];
    private ParticleRandom _random = new(1);
    private float _cycleTime;
    private float _delayLeft;
    private float _rateAccumulator;
    private float _distanceAccumulator;
    private Vector3 _lastEmitterPosition;
    private bool _hasLastPosition;
    private bool _emissionDone;

    public ParticleSimulation(ParticleSystemSettings settings) => Settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public ParticleSystemSettings Settings { get; set; }

    public ParticleBuffer Particles { get; } = new();

    public ParticlePlayState State { get; private set; }

    /// <summary>Playing and still emitting (past the start delay counts; a finished non-looping cycle does not).</summary>
    public bool IsEmitting => State == ParticlePlayState.Playing && !_emissionDone;

    /// <summary>Seconds into the current cycle.</summary>
    public float CycleTime => _cycleTime;

    /// <summary>The seed of the current play (the configured one, or a new one per Play with auto seeding).</summary>
    public uint Seed { get; private set; }

    public int EmittedLastStep { get; private set; }

    /// <summary>Particles that died and were recycled in the last step.</summary>
    public int DiedLastStep { get; private set; }

    public long TotalEmitted { get; private set; }

    /// <summary>Raised when a non-looping system has emitted its cycle and its last particle died.</summary>
    public event Action? Finished;

    /// <summary>Starts from the beginning, or resumes when paused.</summary>
    public void Play()
    {
        if (State == ParticlePlayState.Paused)
        {
            State = ParticlePlayState.Playing;
            return;
        }

        if (State == ParticlePlayState.Playing)
        {
            return;
        }

        var main = Settings.Main;
        Seed = main.AutoRandomSeed ? (uint)Random.Shared.Next(1, int.MaxValue) : main.RandomSeed;
        _random = new ParticleRandom(Seed);
        _cycleTime = 0f;
        _delayLeft = Math.Max(0f, main.StartDelay);
        _rateAccumulator = _distanceAccumulator = 0f;
        _hasLastPosition = false;
        _emissionDone = false;
        Array.Clear(_burstsFired);
        State = ParticlePlayState.Playing;
    }

    public void Pause()
    {
        if (State == ParticlePlayState.Playing)
        {
            State = ParticlePlayState.Paused;
        }
    }

    public void Stop(ParticleStopBehavior behavior = ParticleStopBehavior.StopEmitting)
    {
        State = ParticlePlayState.Stopped;
        _emissionDone = true;
        if (behavior == ParticleStopBehavior.StopEmittingAndClear)
        {
            Particles.Clear();
        }
    }

    public void Clear() => Particles.Clear();

    public void Restart()
    {
        Stop(ParticleStopBehavior.StopEmittingAndClear);
        Play();
    }

    /// <summary>Emits <paramref name="count"/> particles now, whatever the play state (up to the maximum).</summary>
    public int Emit(int count, in EmitterPose pose)
    {
        var emitted = Spawn(count, pose);
        EmittedLastStep += emitted;
        return emitted;
    }

    /// <summary>Advances the system by <paramref name="deltaSeconds"/> (before <see cref="MainModule.SimulationSpeed"/>).</summary>
    public void Step(float deltaSeconds, in EmitterPose pose)
    {
        EmittedLastStep = 0;
        DiedLastStep = 0;
        var main = Settings.Main;
        var dt = Math.Max(0f, deltaSeconds * Math.Max(0f, main.SimulationSpeed));
        if (State == ParticlePlayState.Paused || dt <= 0f)
        {
            return;
        }

        if (Particles.Count > main.MaxParticles)
        {
            Particles.Truncate(main.MaxParticles);
        }

        if (State == ParticlePlayState.Playing && !_emissionDone)
        {
            var remaining = dt;
            if (_delayLeft > 0f)
            {
                var wait = Math.Min(_delayLeft, remaining);
                _delayLeft -= wait;
                remaining -= wait;
            }

            if (remaining > 0f && _delayLeft <= 0f)
            {
                EmittedLastStep += EmitOver(remaining, pose);
            }
        }

        DiedLastStep = ParticleUpdater.Update(Particles, Settings, dt, pose);
        if (State == ParticlePlayState.Playing && _emissionDone && Particles.Count == 0)
        {
            State = ParticlePlayState.Stopped;
            Finished?.Invoke();
        }
    }

    private int EmitOver(float dt, in EmitterPose pose)
    {
        var main = Settings.Main;
        var emission = Settings.Emission;
        var duration = Math.Max(1e-3f, main.Duration);
        if (_burstsFired.Length != emission.Bursts.Count)
        {
            Array.Resize(ref _burstsFired, emission.Bursts.Count);
        }

        var count = 0;
        if (emission.Enabled)
        {
            var rate = emission.RateOverTime.Evaluate(_cycleTime / duration, _random.NextFloat());
            _rateAccumulator += Math.Max(0f, rate) * Math.Min(dt, MaxCatchUpSeconds);
            if (_hasLastPosition)
            {
                var moved = Math.Min(Vector3.Distance(pose.Position, _lastEmitterPosition), 1e4f);
                _distanceAccumulator += Math.Max(0f, emission.RateOverDistance.Evaluate(_cycleTime / duration, _random.NextFloat())) * moved;
            }
        }

        _lastEmitterPosition = pose.Position;
        _hasLastPosition = true;

        var end = _cycleTime + dt;
        for (var cycle = 0; cycle < MaxCyclesPerStep; cycle++)
        {
            if (emission.Enabled)
            {
                count += Bursts(Math.Min(end, duration), duration);
            }

            if (end < duration)
            {
                _cycleTime = end;
                break;
            }

            if (!main.Loop)
            {
                _cycleTime = duration;
                _emissionDone = true;
                break;
            }

            end -= duration;
            _cycleTime = 0f;
            Array.Clear(_burstsFired);
        }

        var continuous = (int)_rateAccumulator + (int)_distanceAccumulator;
        _rateAccumulator -= (int)_rateAccumulator;
        _distanceAccumulator -= (int)_distanceAccumulator;
        return Spawn(count + continuous, pose);
    }

    /// <summary>Burst particles due up to <paramref name="until"/> seconds into the cycle.</summary>
    private int Bursts(float until, float duration)
    {
        var count = 0;
        var bursts = Settings.Emission.Bursts;
        for (var i = 0; i < bursts.Count; i++)
        {
            var burst = bursts[i];
            var interval = Math.Max(1e-3f, burst.Interval);
            while ((burst.Cycles <= 0 || _burstsFired[i] < burst.Cycles) && burst.Time + (_burstsFired[i] * interval) is var at && at <= until && at < duration)
            {
                _burstsFired[i]++;
                if (burst.Probability >= 1f || _random.NextFloat() < burst.Probability)
                {
                    count += Math.Max(0, (int)MathF.Round(burst.Count.Evaluate(at / duration, _random.NextFloat())));
                }
            }
        }

        return count;
    }

    /// <summary>Creates up to <paramref name="count"/> particles; returns how many fit under the maximum.</summary>
    private int Spawn(int count, in EmitterPose pose)
    {
        var main = Settings.Main;
        var particles = Particles;
        var normalized = _cycleTime / Math.Max(1e-3f, main.Duration);
        var world = main.SimulationSpace == ParticleSimulationSpace.World;
        var emitted = 0;
        for (; emitted < count; emitted++)
        {
            var i = particles.Add(Math.Max(0, main.MaxParticles));
            if (i < 0)
            {
                break; // full: the rest of this step's particles are dropped, not saved up
            }

            var seed = _random.NextUInt() | 1u;
            var (position, direction) = ShapeSampler.Sample(Settings.Shape, ref _random);
            var velocity = direction * main.StartSpeed.Evaluate(normalized, ParticleRandom.Value(seed, Salt.Speed));
            if (world)
            {
                position = pose.LocalToWorld.MultiplyPoint3x4(position);
                velocity = pose.Rotation * velocity;
            }

            var size = main.StartSize.Evaluate(normalized, ParticleRandom.Value(seed, Salt.Size));
            particles.Position[i] = position;
            particles.Velocity[i] = velocity;
            particles.StartSize[i] = main.StartSize3D
                ? new Vector3(size, main.StartSizeY.Evaluate(normalized, ParticleRandom.Value(seed, Salt.SizeY)), main.StartSizeZ.Evaluate(normalized, ParticleRandom.Value(seed, Salt.SizeZ)))
                : new Vector3(size, size, size);
            particles.Size[i] = particles.StartSize[i];
            particles.Rotation[i] = new Vector3(0f, 0f, main.StartRotation.Evaluate(normalized, ParticleRandom.Value(seed, Salt.Rotation)));
            particles.AngularVelocity[i] = Vector3.Zero;
            particles.StartColor[i] = particles.Color[i] = main.StartColor.Evaluate(normalized, ParticleRandom.Value(seed, Salt.Color));
            particles.Age[i] = 0f;
            particles.Lifetime[i] = Math.Max(1e-3f, main.StartLifetime.Evaluate(normalized, ParticleRandom.Value(seed, Salt.Lifetime)));
            particles.Frame[i] = 0;
            particles.Seed[i] = seed;
        }

        _rateAccumulator = emitted < count ? 0f : _rateAccumulator;
        TotalEmitted += emitted;
        return emitted;
    }
}
