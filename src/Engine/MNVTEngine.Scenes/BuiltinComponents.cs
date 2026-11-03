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

/// <summary>Orthographic 2D camera. The scene renders through its <see cref="Scene.MainCamera"/>.</summary>
[ComponentId("MNVTEngine.Camera")]
public sealed class Camera : Component
{
    private float _orthographicSize = 5f;

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

    /// <summary>Converts window pixels (origin top-left) to world units.</summary>
    public Vector2 ScreenToWorld(Vector2 screen)
    {
        var half = HalfExtents;
        var offset = new Vector2(((screen.X / ViewportSize.X) - 0.5f) * 2f * half.X, (0.5f - (screen.Y / ViewportSize.Y)) * 2f * half.Y);
        return Transform.Position + Vector2.Rotate(offset, Transform.Rotation);
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
