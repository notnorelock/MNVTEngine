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
