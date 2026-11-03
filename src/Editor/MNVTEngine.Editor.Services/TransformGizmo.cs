using MNVTEngine.Graphics;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Editor.Services;

public enum GizmoTool
{
    Move,
    Rotate,
    Scale,
}

public enum GizmoHandle
{
    None,
    AxisX,
    AxisY,
    Free,
    Ring,
}

/// <summary>
/// Move/rotate/scale handles for one Transform. All sizes are given in screen pixels and converted with the
/// view's world-units-per-pixel, so handles keep a constant on-screen size at any zoom. Move uses world axes;
/// scale uses the object's local axes.
/// </summary>
public sealed class TransformGizmo
{
    public const float AxisLengthPixels = 80f;
    public const float RingRadiusPixels = 70f;
    public const float HitTolerancePixels = 7f;
    public const float FreeHandlePixels = 14f;

    private Vector2 _startMouse;
    private Vector2 _startPosition;
    private float _startRotation;
    private Vector2 _startScale;
    private float _handleLength = 1f;

    public GizmoTool Tool { get; set; } = GizmoTool.Move;

    public GizmoHandle ActiveHandle { get; private set; }

    public bool IsDragging => ActiveHandle != GizmoHandle.None;

    public GizmoHandle HitTest(Transform transform, Vector2 world, float unitsPerPixel)
    {
        ArgumentNullException.ThrowIfNull(transform);
        var origin = transform.Position;
        var tolerance = HitTolerancePixels * unitsPerPixel;
        var length = AxisLengthPixels * unitsPerPixel;

        if (Tool == GizmoTool.Rotate)
        {
            return MathF.Abs(Vector2.Distance(origin, world) - (RingRadiusPixels * unitsPerPixel)) <= tolerance ? GizmoHandle.Ring : GizmoHandle.None;
        }

        var half = FreeHandlePixels * 0.5f * unitsPerPixel;
        var offset = world - origin;
        if (MathF.Abs(offset.X) <= half && MathF.Abs(offset.Y) <= half)
        {
            return GizmoHandle.Free;
        }

        var (xAxis, yAxis) = Axes(transform);
        if (NearSegment(world, origin, origin + (xAxis * length), tolerance))
        {
            return GizmoHandle.AxisX;
        }

        return NearSegment(world, origin, origin + (yAxis * length), tolerance) ? GizmoHandle.AxisY : GizmoHandle.None;
    }

    public void BeginDrag(Transform transform, GizmoHandle handle, Vector2 world, float unitsPerPixel)
    {
        ArgumentNullException.ThrowIfNull(transform);
        ActiveHandle = handle;
        _handleLength = AxisLengthPixels * unitsPerPixel;
        _startMouse = world;
        _startPosition = transform.Position;
        _startRotation = transform.Rotation;
        _startScale = transform.LocalScale;
    }

    /// <summary>Applies the drag. Returns true if the transform changed.</summary>
    public bool Drag(Transform transform, Vector2 world)
    {
        ArgumentNullException.ThrowIfNull(transform);
        var delta = world - _startMouse;
        var (xAxis, yAxis) = Axes(transform);
        switch (ActiveHandle)
        {
            case GizmoHandle.None:
                return false;
            case GizmoHandle.Ring:
                var angle = Vector2.SignedAngle(_startMouse - _startPosition, world - _startPosition);
                transform.Rotation = _startRotation + angle;
                return true;
            case GizmoHandle.Free when Tool == GizmoTool.Scale:
                transform.LocalScale = _startScale * ScaleFactor(Vector2.Dot(delta, (xAxis + yAxis).Normalized));
                return true;
            case GizmoHandle.Free:
                transform.Position = _startPosition + delta;
                return true;
            case GizmoHandle.AxisX when Tool == GizmoTool.Scale:
                transform.LocalScale = new Vector2(_startScale.X * ScaleFactor(Vector2.Dot(delta, xAxis)), _startScale.Y);
                return true;
            case GizmoHandle.AxisY when Tool == GizmoTool.Scale:
                transform.LocalScale = new Vector2(_startScale.X, _startScale.Y * ScaleFactor(Vector2.Dot(delta, yAxis)));
                return true;
            case GizmoHandle.AxisX:
                transform.Position = _startPosition + (xAxis * Vector2.Dot(delta, xAxis));
                return true;
            case GizmoHandle.AxisY:
                transform.Position = _startPosition + (yAxis * Vector2.Dot(delta, yAxis));
                return true;
            default:
                return false;
        }
    }

    public void EndDrag() => ActiveHandle = GizmoHandle.None;

    /// <summary>Draws the handles into an active batch.</summary>
    public void Draw(SpriteBatch batch, Transform transform, float unitsPerPixel, GizmoHandle hovered)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(transform);
        var origin = transform.Position;
        var thickness = 2f * unitsPerPixel;
        Color Tint(GizmoHandle handle, Color baseColor) => handle == ActiveHandle || handle == hovered ? Color.Yellow : baseColor;

        if (Tool == GizmoTool.Rotate)
        {
            batch.DrawCircle(origin, RingRadiusPixels * unitsPerPixel, Tint(GizmoHandle.Ring, new Color(0.3f, 0.6f, 1f)), thickness, 64, depth: -100f);
            return;
        }

        var (xAxis, yAxis) = Axes(transform);
        var length = AxisLengthPixels * unitsPerPixel;
        var tip = 8f * unitsPerPixel;
        DrawAxis(origin, xAxis, Tint(GizmoHandle.AxisX, new Color(0.95f, 0.3f, 0.3f)));
        DrawAxis(origin, yAxis, Tint(GizmoHandle.AxisY, new Color(0.4f, 0.9f, 0.4f)));
        var half = FreeHandlePixels * 0.5f * unitsPerPixel;
        batch.FillRectangle(Rect.FromMinMax(origin - new Vector2(half, half), origin + new Vector2(half, half)), Tint(GizmoHandle.Free, new Color(1f, 1f, 1f, 0.6f)), depth: -100f);

        void DrawAxis(Vector2 from, Vector2 axis, Color color)
        {
            var end = from + (axis * length);
            batch.DrawLine(from, end, color, thickness, depth: -100f);
            if (Tool == GizmoTool.Scale)
            {
                batch.Draw(batch.WhitePixel, end, new Vector2(tip, tip), color, Vector2.SignedAngle(Vector2.Right, axis), depth: -100f);
            }
            else
            {
                batch.DrawLine(end, end - Vector2.Rotate(axis, 25f) * tip, color, thickness, depth: -100f);
                batch.DrawLine(end, end - Vector2.Rotate(axis, -25f) * tip, color, thickness, depth: -100f);
            }
        }
    }

    private (Vector2 X, Vector2 Y) Axes(Transform transform) =>
        Tool == GizmoTool.Scale ? (transform.Right, transform.Up) : (Vector2.Right, Vector2.Up);

    /// <summary>Dragging one handle length outward doubles the scale; inward approaches (never reaches) zero.</summary>
    private float ScaleFactor(float dragAlongAxis) => MathF.Max(0.01f, 1f + (dragAlongAxis / _handleLength));

    private static bool NearSegment(Vector2 point, Vector2 a, Vector2 b, float tolerance)
    {
        var ab = b - a;
        var t = Mathf.Clamp01(Vector2.Dot(point - a, ab) / ab.SqrMagnitude);
        return Vector2.Distance(point, a + (ab * t)) <= tolerance;
    }
}
