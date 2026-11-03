using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics;

/// <summary>
/// Orthographic 2D camera. World space is Y-up; <see cref="OrthographicSize"/> is half the visible
/// height in world units at zoom 1. Screen space is pixels with the origin at the top-left (Y-down),
/// matching mouse coordinates.
/// </summary>
public sealed class Camera2D
{
    private float _zoom = 1f;
    private float _orthographicSize = 5f;

    public Camera2D(int viewportWidth, int viewportHeight)
    {
        SetViewportSize(viewportWidth, viewportHeight);
    }

    /// <summary>World position at the center of the view.</summary>
    public Vector2 Position { get; set; }

    /// <summary>Camera rotation in degrees, counter-clockwise. The world appears to rotate clockwise.</summary>
    public float Rotation { get; set; }

    public float Zoom
    {
        get => _zoom;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _zoom = value;
        }
    }

    public float OrthographicSize
    {
        get => _orthographicSize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _orthographicSize = value;
        }
    }

    public int ViewportWidth { get; private set; }

    public int ViewportHeight { get; private set; }

    public float AspectRatio => (float)ViewportWidth / ViewportHeight;

    /// <summary>Half the visible size in world units, after zoom.</summary>
    public Vector2 HalfExtents
    {
        get
        {
            var halfHeight = _orthographicSize / _zoom;
            return new Vector2(halfHeight * AspectRatio, halfHeight);
        }
    }

    /// <summary>World units per screen pixel.</summary>
    public float UnitsPerPixel => HalfExtents.Y * 2f / ViewportHeight;

    public Matrix4x4 View =>
        (Matrix4x4.Translate(new Vector3(Position, 0f)) * Matrix4x4.Rotate(Quaternion.Euler(0f, 0f, Rotation))).Inverse;

    public Matrix4x4 Projection
    {
        get
        {
            var half = HalfExtents;
            return Matrix4x4.Ortho(-half.X, half.X, -half.Y, half.Y, -1f, 1f);
        }
    }

    public Matrix4x4 ViewProjection => Projection * View;

    /// <summary>Axis-aligned world rectangle containing everything visible (exact when not rotated).</summary>
    public Rect VisibleWorldBounds
    {
        get
        {
            var half = HalfExtents;
            if (Rotation == 0f)
            {
                return Rect.FromMinMax(Position - half, Position + half);
            }

            var a = Vector2.Rotate(half, Rotation);
            var b = Vector2.Rotate(new Vector2(half.X, -half.Y), Rotation);
            var extent = Vector2.Max(Vector2.Abs(a), Vector2.Abs(b));
            return Rect.FromMinMax(Position - extent, Position + extent);
        }
    }

    public void SetViewportSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ViewportWidth = width;
        ViewportHeight = height;
    }

    /// <summary>Maps world units to pixels 1:1 at zoom 1 (pixel-art friendly setup).</summary>
    public void UsePixelUnits() => OrthographicSize = ViewportHeight * 0.5f;

    public Vector2 ScreenToWorld(Vector2 screen)
    {
        var ndc = new Vector3((screen.X / ViewportWidth * 2f) - 1f, 1f - (screen.Y / ViewportHeight * 2f), 0f);
        return (Vector2)ViewProjection.Inverse.MultiplyPoint3x4(ndc);
    }

    public Vector2 WorldToScreen(Vector2 world)
    {
        var ndc = ViewProjection.MultiplyPoint3x4(new Vector3(world, 0f));
        return new Vector2((ndc.X + 1f) * 0.5f * ViewportWidth, (1f - ndc.Y) * 0.5f * ViewportHeight);
    }
}
