using MNVTEngine.Mathematics;

namespace MNVTEngine.Particles;

/// <summary>
/// Ages and moves particles and applies the lifetime modules. A disabled module is skipped for the whole step, so it
/// costs nothing per particle. Dead particles are recycled in place (<see cref="ParticleBuffer.RemoveAt"/>).
/// </summary>
public static class ParticleUpdater
{
    /// <returns>Particles that died this step.</returns>
    public static int Update(ParticleBuffer particles, ParticleSystemSettings settings, float dt, in EmitterPose pose)
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

        var died = 0;
        for (var i = particles.Count - 1; i >= 0; i--)
        {
            var age = particles.Age[i] + dt;
            var lifetime = particles.Lifetime[i];
            if (age >= lifetime)
            {
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

            particles.Position[i] += v * dt;

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

        return died;
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

    /// <summary>A module vector given in <paramref name="space"/>, in the simulation's space.</summary>
    private static Vector3 InSimulationSpace(Vector3 v, ParticleSpace space, bool localSimulation, Quaternion emitterRotation, Quaternion toLocal) =>
        (space, localSimulation) switch
        {
            (ParticleSpace.Local, false) => emitterRotation * v,
            (ParticleSpace.World, true) => toLocal * v,
            _ => v,
        };
}
