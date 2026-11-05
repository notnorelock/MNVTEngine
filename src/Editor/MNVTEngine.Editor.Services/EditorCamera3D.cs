using MNVTEngine.Mathematics;

namespace MNVTEngine.Editor.Services;

/// <summary>
/// The Scene view's 3D camera, Unity-style: it orbits a pivot (Alt + left drag, or middle drag with Alt), pans
/// (middle drag), zooms towards the pivot (wheel), looks and flies (right drag with WASD/QE, Shift faster), and frames
/// a selection (F). It is a real <see cref="MNVTEngine.Camera"/> on a private scene that never runs, so the renderer,
/// picking rays and projections are the same as in games.
/// </summary>
public sealed class EditorCamera3D
{
    private readonly Scene _scene = new() { Name = "Editor camera" };
    private Vector3 _pivot;

    public EditorCamera3D()
    {
        Camera = _scene.CreateGameObject("Editor Camera").AddComponent<Camera>();
        (Camera.Projection, Camera.FieldOfView, Camera.NearClip, Camera.FarClip) = (CameraProjection.Perspective, 60f, 0.05f, 2000f);
        Apply();
    }

    public Camera Camera { get; }

    /// <summary>The point the camera orbits and zooms towards.</summary>
    public Vector3 Pivot
    {
        get => _pivot;
        set
        {
            _pivot = value;
            Apply();
        }
    }

    /// <summary>Distance from the pivot (world units).</summary>
    public float Distance { get; private set; } = 12f;

    /// <summary>Degrees around world up; 0 looks down -Z.</summary>
    public float Yaw { get; private set; } = -30f;

    /// <summary>Degrees above (positive) or below the horizon; negative looks down.</summary>
    public float Pitch { get; private set; } = -25f;

    public Vector3 Position => Pivot - (Rotation * Vector3.Forward * Distance);

    public Quaternion Rotation => Quaternion.Euler(Pitch, Yaw, 0f);

    public void SetViewport(int width, int height)
    {
        Camera.ViewportSize = new Vector2(Math.Max(1, width), Math.Max(1, height));
        Apply();
    }

    /// <summary>Rotates around the pivot by a mouse movement in pixels.</summary>
    public void Orbit(Vector2 pixels)
    {
        Yaw -= pixels.X * 0.3f;
        Pitch = Math.Clamp(Pitch - (pixels.Y * 0.3f), -89f, 89f);
        Apply();
    }

    /// <summary>Turns the camera in place (the pivot moves with it).</summary>
    public void Look(Vector2 pixels)
    {
        var position = Position;
        Yaw -= pixels.X * 0.25f;
        Pitch = Math.Clamp(Pitch - (pixels.Y * 0.25f), -89f, 89f);
        Pivot = position + (Rotation * Vector3.Forward * Distance);
        Apply();
    }

    /// <summary>Moves the camera and pivot so the scene follows the mouse (pixels at the pivot's depth).</summary>
    public void Pan(Vector2 pixels)
    {
        var unitsPerPixel = 2f * Distance * MathF.Tan(Camera.FieldOfView * 0.5f * Mathf.Deg2Rad) / Camera.ViewportSize.Y;
        Pivot += Rotation * new Vector3(-pixels.X * unitsPerPixel, pixels.Y * unitsPerPixel, 0f);
        Apply();
    }

    /// <summary>Moves towards (positive) or away from the pivot by wheel notches.</summary>
    public void Zoom(float notches)
    {
        Distance = Math.Clamp(Distance * MathF.Pow(0.85f, notches), 0.05f, 5000f);
        Apply();
    }

    /// <summary>Flies along the camera's axes (x right, y up, z forward) for <paramref name="seconds"/>.</summary>
    public void Fly(Vector3 direction, float seconds, bool fast)
    {
        if (direction == Vector3.Zero)
        {
            return;
        }

        var speed = MathF.Max(2f, Distance) * (fast ? 3f : 1f);
        Pivot += Rotation * new Vector3(direction.X, direction.Y, -direction.Z) * speed * seconds;
        Apply();
    }

    /// <summary>Centers on a box and backs off until it fills the view.</summary>
    public void Frame(Bounds bounds)
    {
        Pivot = bounds.Center;
        var radius = MathF.Max(bounds.Extents.Magnitude, 0.25f);
        Distance = Math.Clamp(radius / MathF.Sin(Camera.FieldOfView * 0.5f * Mathf.Deg2Rad) * 1.1f, 0.5f, 5000f);
        Apply();
    }

    /// <summary>The picking ray through a pixel of the view (origin top-left).</summary>
    public Ray ScreenRay(Vector2 pixel) => Camera.ScreenPointToRay(pixel);

    /// <summary>A world point in view pixels; Z is the depth in front of the camera (negative behind).</summary>
    public Vector3 WorldToScreen(Vector3 world) => Camera.WorldToScreenPoint(world);

    /// <summary>World units per pixel at a point, so overlays (gizmos, icons) keep a constant size on screen.</summary>
    public float UnitsPerPixel(Vector3 world)
    {
        var depth = MathF.Max(0.01f, -Camera.ViewMatrix.MultiplyPoint3x4(world).Z);
        return 2f * depth * MathF.Tan(Camera.FieldOfView * 0.5f * Mathf.Deg2Rad) / Camera.ViewportSize.Y;
    }

    private void Apply()
    {
        Camera.Transform.Rotation = Rotation;
        Camera.Transform.Position = Position;
    }
}
