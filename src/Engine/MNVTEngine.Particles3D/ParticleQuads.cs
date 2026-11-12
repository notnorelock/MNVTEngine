using MNVTEngine.Mathematics;
using MNVTEngine.Particles;

namespace MNVTEngine.Particles3D;

/// <summary>The view a particle quad is built for.</summary>
/// <param name="Right">Camera right, world space.</param>
/// <param name="Up">Camera up, world space.</param>
public readonly record struct ParticleView(Vector3 Position, Vector3 Right, Vector3 Up);

/// <summary>
/// Orients 3D particle quads for each <see cref="ParticleRenderMode"/>: the half-extent axes of a particle's quad
/// (corners are center plus or minus each axis), with the particle's rotation around its facing axis applied.
/// </summary>
public static class ParticleQuads
{
    /// <param name="size">Width (X) and height (Y) in world units.</param>
    /// <param name="rotation">Degrees: Z around the facing axis; all three in <see cref="ParticleRenderMode.WorldOriented"/>.</param>
    /// <param name="velocity">World velocity (stretched mode).</param>
    public static (Vector3 Right, Vector3 Up) Axes(ParticleRenderMode mode, in ParticleView view, Vector3 position, Vector2 size, Vector3 rotation, Vector3 velocity, float lengthScale, float speedScale)
    {
        Vector3 right, up;
        switch (mode)
        {
            case ParticleRenderMode.ViewAligned:
                (right, up) = (view.Right, view.Up);
                break;
            case ParticleRenderMode.HorizontalBillboard:
                (right, up) = (Vector3.Right, Vector3.Forward);
                break;
            case ParticleRenderMode.VerticalBillboard:
            {
                var toCamera = view.Position - position;
                right = Normalized(Vector3.Cross(Vector3.Up, new Vector3(toCamera.X, 0f, toCamera.Z)), view.Right);
                up = Vector3.Up;
                break;
            }

            case ParticleRenderMode.Stretched:
            {
                var speed = velocity.Magnitude;
                if (speed > 1e-4f)
                {
                    var along = velocity * (1f / speed);
                    var side = Normalized(Vector3.Cross(along, view.Position - position), view.Right);
                    var length = (size.Y * lengthScale) + (speed * speedScale);
                    return (side * (size.X * 0.5f), along * (length * 0.5f));
                }

                goto default;
            }

            case ParticleRenderMode.WorldOriented:
            {
                var q = Quaternion.Euler(rotation);
                return (q * Vector3.Right * (size.X * 0.5f), q * Vector3.Up * (size.Y * 0.5f));
            }

            default:
            {
                // Billboard: faces the camera's position (not just its plane), upright with the camera.
                var forward = Normalized(view.Position - position, Vector3.Cross(view.Right, view.Up));
                right = Normalized(Vector3.Cross(view.Up, forward), view.Right);
                up = Vector3.Cross(forward, right);
                break;
            }
        }

        var angle = rotation.Z * Mathf.Deg2Rad;
        var (sin, cos) = MathF.SinCos(angle);
        var rotatedRight = (right * cos) + (up * sin);
        var rotatedUp = (up * cos) - (right * sin);
        return (rotatedRight * (size.X * 0.5f), rotatedUp * (size.Y * 0.5f));
    }

    private static Vector3 Normalized(Vector3 v, Vector3 fallback)
    {
        var length = v.Magnitude;
        return length > 1e-6f ? v * (1f / length) : fallback;
    }
}
