using MNVTEngine.Mathematics;

namespace MNVTEngine.Editor.Services;

public enum Gizmo3DHandle
{
    None,
    X,
    Y,
    Z,

    /// <summary>Move in the plane of two axes (named by them).</summary>
    XY,
    YZ,
    XZ,

    /// <summary>Scale: all axes together (the center square).</summary>
    Uniform,
}

/// <summary>
/// Move, rotate and scale handles in the 3D Scene view. Handles have a constant size on screen (pixels converted with the
/// camera's units per pixel at the object). Hit tests run in screen space against the projected handles; drags run in
/// world space, so a dragged point stays under the mouse:
/// <list type="bullet">
/// <item>Move: arrows along world X, Y, Z (closest point between the mouse ray and the axis), and squares for the XY,
/// YZ and XZ planes (the mouse ray hitting the plane).</item>
/// <item>Rotate: rings around world X, Y, Z (angle swept in the ring's plane).</item>
/// <item>Scale: local axes, and the center square for uniform scale.</item>
/// </list>
/// Snapping (Ctrl): 0.5 units, 15 degrees, 0.1 scale steps. UI-free.
/// </summary>
public sealed class TransformGizmo3D
{
    public const float AxisLengthPixels = 90f;
    public const float RingRadiusPixels = 80f;
    public const float HitTolerancePixels = 8f;
    public const float PlaneHandleFrom = 0.25f;
    public const float PlaneHandleTo = 0.45f;
    public const float UniformHandlePixels = 9f;

    private Vector3 _startPosition;
    private Quaternion _startRotation;
    private Vector3 _startScale;
    private Vector3 _startPoint;
    private float _startParameter;
    private Vector2 _startMouse;

    public GizmoTool Tool { get; set; } = GizmoTool.Move;

    /// <summary>Move and rotate along the object's own axes instead of the world's (scale always uses its own).</summary>
    public bool LocalSpace { get; set; }

    /// <summary>Snap increments: units for move, degrees for rotate, factor steps for scale.</summary>
    public float MoveSnap { get; set; } = 0.5f;

    public float RotateSnap { get; set; } = 15f;

    public float ScaleSnap { get; set; } = 0.1f;

    public Gizmo3DHandle ActiveHandle { get; private set; }

    public bool IsDragging => ActiveHandle != Gizmo3DHandle.None;

    /// <summary>The world direction of an axis handle (local axes for scale).</summary>
    public Vector3 Axis(Transform transform, Gizmo3DHandle handle)
    {
        ArgumentNullException.ThrowIfNull(transform);
        var axis = handle switch
        {
            Gizmo3DHandle.X or Gizmo3DHandle.YZ => Vector3.UnitX,
            Gizmo3DHandle.Y or Gizmo3DHandle.XZ => Vector3.UnitY,
            _ => Vector3.UnitZ,
        };
        return Tool == GizmoTool.Scale || LocalSpace ? (transform.Rotation * axis).Normalized : axis;
    }

    /// <summary>World length of the axis handles at the object.</summary>
    public static float HandleLength(Transform transform, EditorCamera3D camera) =>
        AxisLengthPixels * camera.UnitsPerPixel(transform.Position);

    public Gizmo3DHandle HitTest(Transform transform, EditorCamera3D camera, Vector2 mouse)
    {
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentNullException.ThrowIfNull(camera);
        var origin = transform.Position;
        var length = HandleLength(transform, camera);
        Vector2? Screen(Vector3 world) => camera.WorldToScreen(world) is { Z: > 0f } s ? new Vector2(s.X, s.Y) : null;
        if (Screen(origin) is not { } center)
        {
            return Gizmo3DHandle.None;
        }

        var best = Gizmo3DHandle.None;
        var bestDistance = HitTolerancePixels;
        void Consider(Gizmo3DHandle handle, float distance)
        {
            if (distance <= bestDistance)
            {
                (best, bestDistance) = (handle, distance);
            }
        }

        Gizmo3DHandle[] axes = [Gizmo3DHandle.X, Gizmo3DHandle.Y, Gizmo3DHandle.Z];
        switch (Tool)
        {
            case GizmoTool.Rotate:
                foreach (var handle in axes)
                {
                    var ring = Ring(origin, Axis(transform, handle), RingRadiusPixels * camera.UnitsPerPixel(origin));
                    for (var i = 0; i < ring.Length; i++)
                    {
                        if (Screen(ring[i]) is { } a && Screen(ring[(i + 1) % ring.Length]) is { } b)
                        {
                            Consider(handle, DistanceToSegment(mouse, a, b));
                        }
                    }
                }

                return best;

            case GizmoTool.Scale when Vector2.Distance(mouse, center) <= UniformHandlePixels:
                return Gizmo3DHandle.Uniform;
        }

        foreach (var handle in axes)
        {
            if (Screen(origin + (Axis(transform, handle) * length)) is { } end)
            {
                Consider(handle, DistanceToSegment(mouse, center, end));
            }
        }

        if (Tool == GizmoTool.Move && best == Gizmo3DHandle.None)
        {
            foreach (var (plane, u, v) in new[] { (Gizmo3DHandle.XY, Vector3.UnitX, Vector3.UnitY), (Gizmo3DHandle.YZ, Vector3.UnitY, Vector3.UnitZ), (Gizmo3DHandle.XZ, Vector3.UnitX, Vector3.UnitZ) })
            {
                var corners = PlaneSquare(origin, u, v, length).Select(Screen).ToArray();
                if (corners.All(c => c is not null) && InsideQuad(mouse, [.. corners.Select(c => c!.Value)]))
                {
                    return plane;
                }
            }
        }

        return best;
    }

    /// <summary>Starts dragging a handle at the mouse position.</summary>
    public void BeginDrag(Transform transform, Gizmo3DHandle handle, EditorCamera3D camera, Vector2 mouse)
    {
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentNullException.ThrowIfNull(camera);
        ActiveHandle = handle;
        (_startPosition, _startRotation, _startScale, _startMouse) = (transform.Position, transform.Rotation, transform.LocalScale, mouse);
        var ray = camera.ScreenRay(mouse);
        switch (handle)
        {
            case Gizmo3DHandle.X or Gizmo3DHandle.Y or Gizmo3DHandle.Z when Tool != GizmoTool.Rotate:
                _startParameter = ClosestOnAxis(_startPosition, Axis(transform, handle), ray) ?? 0f;
                break;
            case Gizmo3DHandle.XY or Gizmo3DHandle.YZ or Gizmo3DHandle.XZ:
                _startPoint = OnPlane(ray, _startPosition, Axis(transform, handle)) ?? _startPosition;
                break;
            case Gizmo3DHandle.X or Gizmo3DHandle.Y or Gizmo3DHandle.Z:
                _startPoint = OnPlane(ray, _startPosition, Axis(transform, handle)) ?? _startPosition;
                break;
        }
    }

    /// <summary>Applies the drag at the current mouse position; true when the transform changed.</summary>
    public bool Drag(Transform transform, EditorCamera3D camera, Vector2 mouse, bool snap)
    {
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentNullException.ThrowIfNull(camera);
        if (!IsDragging)
        {
            return false;
        }

        var ray = camera.ScreenRay(mouse);
        var handle = ActiveHandle;
        var axis = Axis(transform, handle);
        switch (Tool, handle)
        {
            case (GizmoTool.Move, Gizmo3DHandle.X or Gizmo3DHandle.Y or Gizmo3DHandle.Z):
            {
                if (ClosestOnAxis(_startPosition, axis, ray) is not { } s)
                {
                    return false;
                }

                var moved = Snap(s - _startParameter, snap ? MoveSnap : 0f);
                return Set(transform, _startPosition + (axis * moved));
            }

            case (GizmoTool.Move, _):
            {
                if (OnPlane(ray, _startPosition, axis) is not { } point)
                {
                    return false;
                }

                var delta = point - _startPoint;
                if (snap)
                {
                    delta = new Vector3(Snap(delta.X, MoveSnap), Snap(delta.Y, MoveSnap), Snap(delta.Z, MoveSnap));
                }

                return Set(transform, _startPosition + delta);
            }

            case (GizmoTool.Rotate, _):
            {
                if (OnPlane(ray, _startPosition, axis) is not { } point)
                {
                    return false;
                }

                var from = (_startPoint - _startPosition).Normalized;
                var to = (point - _startPosition).Normalized;
                var angle = MathF.Atan2(Vector3.Dot(Vector3.Cross(from, to), axis), Vector3.Dot(from, to)) * Mathf.Rad2Deg;
                var rotation = Quaternion.AngleAxis(Snap(angle, snap ? RotateSnap : 0f), axis) * _startRotation;
                if (Quaternion.Approximately(rotation, transform.Rotation))
                {
                    return false;
                }

                transform.Rotation = rotation;
                return true;
            }

            case (GizmoTool.Scale, Gizmo3DHandle.Uniform):
            {
                var factor = MathF.Max(0.01f, 1f + ((mouse.X - _startMouse.X - (mouse.Y - _startMouse.Y)) / 100f));
                return SetScale(transform, _startScale * Snap(factor, snap ? ScaleSnap : 0f));
            }

            default:
            {
                if (ClosestOnAxis(_startPosition, axis, ray) is not { } s || MathF.Abs(_startParameter) < 1e-5f)
                {
                    return false;
                }

                var factor = MathF.Max(0.01f, Snap(s / _startParameter, snap ? ScaleSnap : 0f));
                var scale = _startScale;
                scale = handle switch
                {
                    Gizmo3DHandle.X => scale with { X = scale.X * factor },
                    Gizmo3DHandle.Y => scale with { Y = scale.Y * factor },
                    _ => scale with { Z = scale.Z * factor },
                };
                return SetScale(transform, scale);
            }
        }
    }

    public void EndDrag() => ActiveHandle = Gizmo3DHandle.None;

    /// <summary>The handle lines to draw (world space): arrows, plane squares or rings, per handle.</summary>
    public IReadOnlyList<(Gizmo3DHandle Handle, Vector3 From, Vector3 To)> Lines(Transform transform, EditorCamera3D camera)
    {
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentNullException.ThrowIfNull(camera);
        var origin = transform.Position;
        var length = HandleLength(transform, camera);
        var lines = new List<(Gizmo3DHandle, Vector3, Vector3)>();
        Gizmo3DHandle[] axes = [Gizmo3DHandle.X, Gizmo3DHandle.Y, Gizmo3DHandle.Z];
        if (Tool == GizmoTool.Rotate)
        {
            foreach (var handle in axes)
            {
                var ring = Ring(origin, Axis(transform, handle), RingRadiusPixels * camera.UnitsPerPixel(origin));
                for (var i = 0; i < ring.Length; i++)
                {
                    lines.Add((handle, ring[i], ring[(i + 1) % ring.Length]));
                }
            }

            return lines;
        }

        foreach (var handle in axes)
        {
            lines.Add((handle, origin, origin + (Axis(transform, handle) * length)));
        }

        if (Tool == GizmoTool.Move)
        {
            foreach (var (plane, u, v) in new[] { (Gizmo3DHandle.XY, Vector3.UnitX, Vector3.UnitY), (Gizmo3DHandle.YZ, Vector3.UnitY, Vector3.UnitZ), (Gizmo3DHandle.XZ, Vector3.UnitX, Vector3.UnitZ) })
            {
                var c = PlaneSquare(origin, u, v, length);
                for (var i = 0; i < 4; i++)
                {
                    lines.Add((plane, c[i], c[(i + 1) % 4]));
                }
            }
        }

        return lines;
    }

    /// <summary>The parameter along the axis line (from <paramref name="origin"/>) closest to the ray; null when they are parallel.</summary>
    public static float? ClosestOnAxis(Vector3 origin, Vector3 axis, Ray ray)
    {
        var w = origin - ray.Origin;
        var b = Vector3.Dot(axis, ray.Direction);
        var denominator = 1f - (b * b);
        if (denominator < 1e-4f)
        {
            return null;
        }

        return ((b * Vector3.Dot(ray.Direction, w)) - Vector3.Dot(axis, w)) / denominator;
    }

    /// <summary>Where the ray hits the plane through <paramref name="point"/> with <paramref name="normal"/>; null when parallel or behind.</summary>
    public static Vector3? OnPlane(Ray ray, Vector3 point, Vector3 normal)
    {
        var denominator = Vector3.Dot(normal, ray.Direction);
        if (MathF.Abs(denominator) < 1e-5f)
        {
            return null;
        }

        var t = Vector3.Dot(point - ray.Origin, normal) / denominator;
        return t < 0f ? null : ray.GetPoint(t);
    }

    private static bool Set(Transform transform, Vector3 position)
    {
        if (transform.Position == position)
        {
            return false;
        }

        transform.Position = position;
        return true;
    }

    private static bool SetScale(Transform transform, Vector3 scale)
    {
        if (transform.LocalScale == scale)
        {
            return false;
        }

        transform.LocalScale = scale;
        return true;
    }

    private static float Snap(float value, float step) => step > 0f ? MathF.Round(value / step) * step : value;

    private static Vector3[] Ring(Vector3 center, Vector3 axis, float radius)
    {
        var u = Vector3.Cross(axis, MathF.Abs(axis.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY).Normalized;
        var v = Vector3.Cross(axis, u);
        var points = new Vector3[48];
        for (var i = 0; i < points.Length; i++)
        {
            var a = i * MathF.Tau / points.Length;
            points[i] = center + (((u * MathF.Cos(a)) + (v * MathF.Sin(a))) * radius);
        }

        return points;
    }

    private static Vector3[] PlaneSquare(Vector3 origin, Vector3 u, Vector3 v, float length)
    {
        var (a, b) = (PlaneHandleFrom * length, PlaneHandleTo * length);
        return [origin + (u * a) + (v * a), origin + (u * b) + (v * a), origin + (u * b) + (v * b), origin + (u * a) + (v * b)];
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var t = ab.SqrMagnitude > 0f ? Math.Clamp(Vector2.Dot(p - a, ab) / ab.SqrMagnitude, 0f, 1f) : 0f;
        return Vector2.Distance(p, a + (ab * t));
    }

    private static bool InsideQuad(Vector2 p, Vector2[] quad)
    {
        // Convex quad: p is on the same side of every edge.
        var sign = 0;
        for (var i = 0; i < 4; i++)
        {
            var a = quad[i];
            var b = quad[(i + 1) % 4];
            var cross = ((b.X - a.X) * (p.Y - a.Y)) - ((b.Y - a.Y) * (p.X - a.X));
            var s = Math.Sign(cross);
            if (s != 0 && sign != 0 && s != sign)
            {
                return false;
            }

            sign = s != 0 ? s : sign;
        }

        return true;
    }
}
