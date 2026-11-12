using MNVTEngine.Mathematics;

namespace MNVTEngine.Particles;

/// <summary>Samples a start position and direction from a <see cref="ShapeModule"/>, in emitter-local space.</summary>
public static class ShapeSampler
{
    /// <returns>Position and unit direction relative to the emitter (shape position, rotation and scale applied).</returns>
    public static (Vector3 Position, Vector3 Direction) Sample(ShapeModule shape, ref ParticleRandom random)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var (position, direction) = shape.Enabled ? SampleShape(shape, ref random) : (Vector3.Zero, Vector3.Up);
        if (shape.Enabled && shape.RandomizeDirection > 0f)
        {
            var planar = ShapeModule.IsPlanar(shape.Kind);
            var randomDirection = planar ? OnCircle(ref random) : OnSphere(ref random);
            direction = Normalized(Vector3.LerpUnclamped(direction, randomDirection, Math.Clamp(shape.RandomizeDirection, 0f, 1f)), direction);
        }

        if (!shape.Enabled)
        {
            return (position, direction);
        }

        var rotation = Quaternion.Euler(shape.Rotation);
        position = shape.Position + (rotation * Vector3.Scale(position, shape.Scale));
        direction = Normalized(rotation * Vector3.Scale(direction, Sign(shape.Scale)), direction);
        return (position, direction);
    }

    private static (Vector3, Vector3) SampleShape(ShapeModule shape, ref ParticleRandom random)
    {
        var radius = Math.Max(0f, shape.Radius);
        switch (shape.Kind)
        {
            case ParticleShapeKind.Circle:
            case ParticleShapeKind.CircleEdge:
            {
                var angle = Arc(shape, ref random);
                var outward = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f);
                var distance = shape.Kind == ParticleShapeKind.CircleEdge ? radius : radius * Thick(shape, MathF.Sqrt(random.NextFloat()));
                return (outward * distance, outward);
            }

            case ParticleShapeKind.Rectangle:
                return (new Vector3((random.NextFloat() - 0.5f) * shape.Size.X, (random.NextFloat() - 0.5f) * shape.Size.Y, 0f), Vector3.Up);

            case ParticleShapeKind.RectangleEdge:
            {
                var (w, h) = (shape.Size.X, shape.Size.Y);
                var along = random.NextFloat() * 2f * (w + h);
                Vector3 point, normal;
                if (along < w)
                {
                    (point, normal) = (new Vector3(along - (w / 2f), h / 2f, 0f), Vector3.Up);
                }
                else if (along < w + h)
                {
                    (point, normal) = (new Vector3(w / 2f, (h / 2f) - (along - w), 0f), Vector3.Right);
                }
                else if (along < (2f * w) + h)
                {
                    (point, normal) = (new Vector3((w / 2f) - (along - w - h), -h / 2f, 0f), Vector3.Down);
                }
                else
                {
                    (point, normal) = (new Vector3(-w / 2f, (-h / 2f) + (along - (2f * w) - h), 0f), Vector3.Left);
                }

                return (point, normal);
            }

            case ParticleShapeKind.Line:
                return (new Vector3((random.NextFloat() - 0.5f) * shape.Length, 0f, 0f), Vector3.Up);

            case ParticleShapeKind.Sphere:
            case ParticleShapeKind.SphereSurface:
            case ParticleShapeKind.Hemisphere:
            {
                var outward = OnSphere(ref random);
                if (shape.Kind == ParticleShapeKind.Hemisphere && outward.Y < 0f)
                {
                    outward = new Vector3(outward.X, -outward.Y, outward.Z);
                }

                var distance = shape.Kind == ParticleShapeKind.SphereSurface ? radius : radius * Thick(shape, MathF.Cbrt(random.NextFloat()));
                return (outward * distance, outward);
            }

            case ParticleShapeKind.Cone:
            {
                var angle = Arc(shape, ref random);
                var spoke = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
                var t = Thick(shape, MathF.Sqrt(random.NextFloat()));
                var spread = MathF.Sin(Math.Clamp(shape.Angle, 0f, 90f) * Mathf.Deg2Rad) * t;
                var direction = Normalized(Vector3.Up * MathF.Sqrt(Math.Max(0f, 1f - (spread * spread))) + (spoke * spread), Vector3.Up);
                return (spoke * (radius * t), direction);
            }

            case ParticleShapeKind.Box:
                return (new Vector3((random.NextFloat() - 0.5f) * shape.Size.X, (random.NextFloat() - 0.5f) * shape.Size.Y, (random.NextFloat() - 0.5f) * shape.Size.Z), Vector3.Up);

            case ParticleShapeKind.BoxSurface:
            {
                var half = shape.Size * 0.5f;
                var face = (int)(random.NextFloat() * 6f) % 6;
                var u = random.NextFloat() - 0.5f;
                var v = random.NextFloat() - 0.5f;
                return face switch
                {
                    0 => (new Vector3(half.X, u * shape.Size.Y, v * shape.Size.Z), Vector3.Right),
                    1 => (new Vector3(-half.X, u * shape.Size.Y, v * shape.Size.Z), Vector3.Left),
                    2 => (new Vector3(u * shape.Size.X, half.Y, v * shape.Size.Z), Vector3.Up),
                    3 => (new Vector3(u * shape.Size.X, -half.Y, v * shape.Size.Z), Vector3.Down),
                    4 => (new Vector3(u * shape.Size.X, v * shape.Size.Y, half.Z), Vector3.Back),
                    _ => (new Vector3(u * shape.Size.X, v * shape.Size.Y, -half.Z), Vector3.Forward),
                };
            }

            default:
                return (Vector3.Zero, Vector3.Up);
        }
    }

    /// <summary>A random angle in radians within the shape's arc.</summary>
    private static float Arc(ShapeModule shape, ref ParticleRandom random) => random.NextFloat() * Math.Clamp(shape.Arc, 0f, 360f) * Mathf.Deg2Rad;

    /// <summary>Maps a uniform 0-1 distance into the shell given by <see cref="ShapeModule.RadiusThickness"/>.</summary>
    private static float Thick(ShapeModule shape, float uniform) => 1f - (Math.Clamp(shape.RadiusThickness, 0f, 1f) * (1f - uniform));

    private static Vector3 OnCircle(ref ParticleRandom random)
    {
        var angle = random.NextFloat() * Mathf.Tau;
        return new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f);
    }

    private static Vector3 OnSphere(ref ParticleRandom random)
    {
        var z = (random.NextFloat() * 2f) - 1f;
        var angle = random.NextFloat() * Mathf.Tau;
        var r = MathF.Sqrt(Math.Max(0f, 1f - (z * z)));
        return new Vector3(r * MathF.Cos(angle), r * MathF.Sin(angle), z);
    }

    private static Vector3 Sign(Vector3 v) => new(v.X < 0f ? -1f : 1f, v.Y < 0f ? -1f : 1f, v.Z < 0f ? -1f : 1f);

    private static Vector3 Normalized(Vector3 v, Vector3 fallback)
    {
        var length = v.Magnitude;
        return length > 1e-6f ? v * (1f / length) : fallback;
    }
}
