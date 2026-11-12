using MNVTEngine.Mathematics;

namespace MNVTEngine.Particles;

/// <summary>
/// Ages and moves particles and applies the lifetime modules. A disabled module is skipped for the whole step, so it
/// costs nothing per particle. Dead particles are recycled in place (<see cref="ParticleBuffer.RemoveAt"/>).
/// </summary>
public static class ParticleUpdater
{
    /// <param name="events">Where deaths and collisions are recorded when it asks for them; null records nothing.</param>
    /// <param name="time">Seconds the system has run (scrolls the noise field).</param>
    /// <returns>Particles that died this step, and the particle-plane collision tests made.</returns>
    public static (int Died, int CollisionChecks) Update(ParticleBuffer particles, ParticleSystemSettings settings, float dt, in EmitterPose pose, ParticleEventBuffer? events = null, float time = 0f)
    {
        ArgumentNullException.ThrowIfNull(particles);
        ArgumentNullException.ThrowIfNull(settings);
        var main = settings.Main;
        var local = main.SimulationSpace == ParticleSimulationSpace.Local;
        var toLocal = pose.Rotation.Inverse;
        var gravityOn = main.GravityModifier.Mode != CurveMode.Constant || main.GravityModifier.Max != 0f;
        var gravity = local ? toLocal * main.Gravity : main.Gravity;
        var force = settings.ForceOverLifetime.Enabled ? settings.ForceOverLifetime : null;
        var velocity = settings.VelocityOverLifetime.Enabled ? settings.VelocityOverLifetime : null;
        var color = settings.ColorOverLifetime.Enabled ? settings.ColorOverLifetime.Color : null;
        var size = settings.SizeOverLifetime.Enabled ? settings.SizeOverLifetime : null;
        var rotation = settings.RotationOverLifetime.Enabled ? settings.RotationOverLifetime : null;
        var sheet = settings.TextureSheetAnimation.Enabled ? settings.TextureSheetAnimation : null;
        var noise = settings.Noise.Enabled ? settings.Noise : null;
        var collision = settings.Collision.Enabled && settings.Collision.Planes.Count > 0 ? settings.Collision : null;
        var recordDeaths = events?.RecordDeaths ?? false;
        var recordCollisions = events?.RecordCollisions ?? false;
        var checks = 0;

        var died = 0;
        for (var i = particles.Count - 1; i >= 0; i--)
        {
            var age = particles.Age[i] + dt;
            var lifetime = particles.Lifetime[i];
            if (age >= lifetime)
            {
                if (recordDeaths)
                {
                    events!.Add(new ParticleEvent(SubEmitterEvent.Death, ToWorld(particles.Position[i], local, pose), ToWorldDirection(particles.Velocity[i], local, pose), particles.Color[i], Vector3.Zero));
                }

                particles.RemoveAt(i); // the last particle moves here; it was updated already (the loop runs backwards)
                died++;
                continue;
            }

            particles.Age[i] = age;
            var t = age / lifetime;
            var seed = particles.Seed[i];
            var v = particles.Velocity[i];
            if (gravityOn)
            {
                v += gravity * (main.GravityModifier.Evaluate(t, ParticleRandom.Value(seed, Salt.Gravity)) * dt);
            }

            if (force is not null)
            {
                var f = new Vector3(force.X.Evaluate(t, ParticleRandom.Value(seed, Salt.Force)), force.Y.Evaluate(t, ParticleRandom.Value(seed, Salt.ForceY)), force.Z.Evaluate(t, ParticleRandom.Value(seed, Salt.ForceZ)));
                v += InSimulationSpace(f, force.Space, local, pose.Rotation, toLocal) * dt;
            }

            particles.Velocity[i] = v;
            if (velocity is not null)
            {
                var extra = new Vector3(velocity.X.Evaluate(t, ParticleRandom.Value(seed, Salt.Velocity)), velocity.Y.Evaluate(t, ParticleRandom.Value(seed, Salt.VelocityY)), velocity.Z.Evaluate(t, ParticleRandom.Value(seed, Salt.VelocityZ)));
                v = (v + InSimulationSpace(extra, velocity.Space, local, pose.Rotation, toLocal)) * velocity.SpeedModifier.Evaluate(t, ParticleRandom.Value(seed, Salt.SpeedModifier));
            }

            if (noise is not null)
            {
                var r = ParticleRandom.Value(seed, Salt.Noise);
                var x = noise.Strength.Evaluate(t, r);
                var strength = noise.SeparateAxes ? new Vector3(x, noise.StrengthY.Evaluate(t, r), noise.StrengthZ.Evaluate(t, r)) : new Vector3(x, x, x);
                var sample = ParticleNoise.Vector((particles.Position[i] * noise.Frequency) + new Vector3(0f, 0f, time * noise.ScrollSpeed), 0f);
                v += Vector3.Scale(sample, strength);
            }

            particles.Position[i] += v * dt;
            if (collision is not null)
            {
                checks += collision.Planes.Count;
                if (Collide(particles, i, collision, local, pose, toLocal, recordCollisions ? events : null))
                {
                    died++; // removed on impact; the particle swapped into its place was already updated (the loop runs backwards)
                    continue;
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
                var start = particles.StartColor[i];
                var c = color.Evaluate(t, ParticleRandom.Value(seed, Salt.ColorOverLifetime));
                particles.Color[i] = new Color(start.R * c.R, start.G * c.G, start.B * c.B, start.A * c.A);
            }

            if (sheet is not null)
            {
                particles.Frame[i] = Frame(sheet, t, age, seed);
            }
        }

        return (died, checks);
    }

    /// <summary>
    /// Pushes particle <paramref name="i"/> out of every plane it entered and reflects its velocity. Returns true when it
    /// died of the hit (lifetime loss or minimum kill speed) and was removed.
    /// </summary>
    private static bool Collide(ParticleBuffer particles, int i, CollisionModule collision, bool local, in EmitterPose pose, Quaternion toLocal, ParticleEventBuffer? events)
    {
        var position = ToWorld(particles.Position[i], local, pose);
        var velocity = ToWorldDirection(particles.Velocity[i], local, pose);
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
            events?.Add(new ParticleEvent(SubEmitterEvent.Collision, position, velocity, particles.Color[i], normal));
        }

        if (!hit)
        {
            return false;
        }

        particles.Position[i] = local ? pose.LocalToWorld.Inverse.MultiplyPoint3x4(position) : position;
        particles.Velocity[i] = local ? toLocal * velocity : velocity;
        particles.Age[i] += particles.Lifetime[i] * Math.Clamp(collision.LifetimeLoss, 0f, 1f);
        if (particles.Age[i] >= particles.Lifetime[i] || velocity.Magnitude < collision.MinKillSpeed)
        {
            particles.RemoveAt(i);
            return true;
        }

        return false;
    }

    private static Vector3 ToWorld(Vector3 position, bool local, in EmitterPose pose) => local ? pose.LocalToWorld.MultiplyPoint3x4(position) : position;

    private static Vector3 ToWorldDirection(Vector3 direction, bool local, in EmitterPose pose) => local ? pose.Rotation * direction : direction;

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

    /// <summary>A module vector given in <paramref name="space"/>, in the simulation's space.</summary>
    private static Vector3 InSimulationSpace(Vector3 v, ParticleSpace space, bool localSimulation, Quaternion emitterRotation, Quaternion toLocal) =>
        (space, localSimulation) switch
        {
            (ParticleSpace.Local, false) => emitterRotation * v,
            (ParticleSpace.World, true) => toLocal * v,
            _ => v,
        };
}
