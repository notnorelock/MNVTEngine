using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;

namespace MNVTEngine;

/// <summary>Draws a texture as a quad at the GameObject's transform.</summary>
[ComponentId("MNVTEngine.SpriteRenderer")]
public sealed class SpriteRenderer : Component
{
    [SerializeField]
    [AssetType(AssetTypes.Texture)]
    public AssetRef Texture { get; set; }

    [SerializeField]
    public Color Color { get; set; } = Color.White;

    /// <summary>Size in world units before the transform's scale.</summary>
    [SerializeField]
    public Vector2 Size { get; set; } = Vector2.One;

    /// <summary>Normalized pivot: (0, 0) bottom-left, (0.5, 0.5) center.</summary>
    [SerializeField]
    public Vector2 Pivot { get; set; } = new(0.5f, 0.5f);

    [SerializeField]
    public bool FlipX { get; set; }

    [SerializeField]
    public bool FlipY { get; set; }

    /// <summary>Shader, blend mode and uniforms (<c>.mnvtmat</c>); none uses the default sprite material.</summary>
    [SerializeField]
    [AssetType(AssetTypes.Material)]
    public AssetRef Material { get; set; }

    /// <summary>Part of the texture to draw, in texels from the image's top-left; empty (zero size) draws the whole texture. Set by <see cref="SpriteAnimator"/>.</summary>
    [SerializeField]
    public Rect SourceRect { get; set; }

    /// <summary>Higher values draw on top. Equal values have no guaranteed order.</summary>
    [SerializeField]
    public int SortingOrder { get; set; }
}

public enum CameraProjection
{
    /// <summary>Parallel projection; <see cref="Camera.OrthographicSize"/> is half the visible height. 2D scenes use it.</summary>
    Orthographic,

    /// <summary>Perspective projection with a vertical <see cref="Camera.FieldOfView"/>.</summary>
    Perspective,
}

/// <summary>
/// A camera looking down its local -Z (<see cref="Transform.Forward"/>). The scene renders through its
/// <see cref="Scene.MainCamera"/>. 2D rendering uses the orthographic size and the XY position and angle; the matrices
/// below (Vulkan depth range 0..1) are for 3D rendering, culling and picking.
/// </summary>
[ComponentId("MNVTEngine.Camera")]
public sealed class Camera : Component
{
    private float _orthographicSize = 5f;
    private float _fieldOfView = 60f;
    private float _nearClip = 0.1f;
    private float _farClip = 1000f;

    [SerializeField]
    public CameraProjection Projection { get; set; }

    /// <summary>Vertical field of view in degrees (perspective), between 1 and 179.</summary>
    [SerializeField]
    public float FieldOfView
    {
        get => _fieldOfView;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1f);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 179f);
            _fieldOfView = value;
        }
    }

    /// <summary>Distance to the near clipping plane; must be positive and below <see cref="FarClip"/>.</summary>
    [SerializeField]
    public float NearClip
    {
        get => _nearClip;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _nearClip = value;
        }
    }

    /// <summary>Distance to the far clipping plane.</summary>
    [SerializeField]
    public float FarClip
    {
        get => _farClip;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _farClip = value;
        }
    }

    /// <summary>Half the visible height in world units.</summary>
    [SerializeField]
    public float OrthographicSize
    {
        get => _orthographicSize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _orthographicSize = value;
        }
    }

    [SerializeField]
    public Color BackgroundColor { get; set; } = new(0.1f, 0.1f, 0.14f);

    /// <summary>Output size in pixels; maintained by the renderer, not persisted.</summary>
    public Vector2 ViewportSize { get; set; } = new(1280f, 720f);

    public Vector2 HalfExtents => new(_orthographicSize * ViewportSize.X / ViewportSize.Y, _orthographicSize);

    /// <summary>Width / height of the output.</summary>
    public float Aspect => ViewportSize.Y > 0f ? ViewportSize.X / ViewportSize.Y : 1f;

    /// <summary>Clip-space projection with Vulkan's depth range (near 0, far 1).</summary>
    /// <exception cref="InvalidOperationException"><see cref="NearClip"/> is not below <see cref="FarClip"/>.</exception>
    public Matrix4x4 ProjectionMatrix
    {
        get
        {
            if (_nearClip >= _farClip)
            {
                throw new InvalidOperationException($"Camera '{GameObject.Name}': near clip {_nearClip} must be below far clip {_farClip}.");
            }

            if (Projection == CameraProjection.Perspective)
            {
                return Matrix4x4.PerspectiveZeroToOne(_fieldOfView, Aspect, _nearClip, _farClip);
            }

            var half = HalfExtents;
            return Matrix4x4.OrthoZeroToOne(-half.X, half.X, -half.Y, half.Y, _nearClip, _farClip);
        }
    }

    /// <summary>World to camera space: the inverse of the camera's position and rotation (scale is ignored).</summary>
    public Matrix4x4 ViewMatrix => Matrix4x4.Rotate(Transform.Rotation.Inverse) * Matrix4x4.Translate(-Transform.Position);

    public Matrix4x4 ViewProjectionMatrix => ProjectionMatrix * ViewMatrix;

    /// <summary>The visible volume in world space, for culling.</summary>
    public Frustum CalculateFrustum() => Frustum.FromViewProjection(ViewProjectionMatrix);

    /// <summary>The ray from the camera through a pixel (origin top-left), starting on the near plane; for picking.</summary>
    public Ray ScreenPointToRay(Vector2 screen)
    {
        var inverse = ViewProjectionMatrix.Inverse;
        var ndc = new Vector2(((screen.X / ViewportSize.X) * 2f) - 1f, 1f - ((screen.Y / ViewportSize.Y) * 2f));
        var near = inverse.MultiplyPoint(new Vector3(ndc.X, ndc.Y, 0f));
        var far = inverse.MultiplyPoint(new Vector3(ndc.X, ndc.Y, 1f));
        return new Ray(near, far - near);
    }

    /// <summary>A world point in pixels (origin top-left); Z is the distance in front of the camera (negative behind it).</summary>
    public Vector3 WorldToScreenPoint(Vector3 world)
    {
        var clip = ViewProjectionMatrix * new Vector4(world.X, world.Y, world.Z, 1f);
        var depth = -ViewMatrix.MultiplyPoint3x4(world).Z;
        var w = MathF.Abs(clip.W) > Mathf.Epsilon ? clip.W : Mathf.Epsilon;
        return new Vector3(((clip.X / w) + 1f) * 0.5f * ViewportSize.X, (1f - (clip.Y / w)) * 0.5f * ViewportSize.Y, depth);
    }

    /// <summary>2D: converts window pixels (origin top-left) to world units on the XY plane (orthographic cameras).</summary>
    public Vector2 ScreenToWorld(Vector2 screen)
    {
        var half = HalfExtents;
        var offset = new Vector2(((screen.X / ViewportSize.X) - 0.5f) * 2f * half.X, (0.5f - (screen.Y / ViewportSize.Y)) * 2f * half.Y);
        return (Vector2)Transform.Position + Vector2.Rotate(offset, Transform.Rotation2D);
    }
}

/// <summary>
/// A point light. When a scene has any enabled light, it is lit: everything is multiplied by a light buffer that starts
/// at the ambient color (<see cref="GlobalLight2D"/>) and adds each light's color with a smooth falloff to <see cref="Radius"/>.
/// </summary>
[ComponentId("MNVTEngine.Light2D")]
public sealed class Light2D : Component
{
    private float _radius = 5f;

    [SerializeField]
    public Color Color { get; set; } = Color.White;

    /// <summary>Brightness multiplier; light adds up and saturates at full brightness.</summary>
    [SerializeField]
    public float Intensity { get; set; } = 1f;

    /// <summary>Reach in world units (scaled by the transform).</summary>
    [SerializeField]
    public float Radius { get => _radius; set => _radius = Math.Max(0.01f, value); }
}

/// <summary>Ambient light of a lit scene: what unlit areas look like. Default when absent: dark blue-grey (0.15).</summary>
[ComponentId("MNVTEngine.GlobalLight2D")]
public sealed class GlobalLight2D : Component
{
    [SerializeField]
    public Color Color { get; set; } = Color.White;

    [SerializeField]
    public float Intensity { get; set; } = 0.25f;
}
