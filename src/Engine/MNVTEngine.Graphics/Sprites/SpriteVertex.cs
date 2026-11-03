using System.Runtime.InteropServices;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics;

/// <summary>20-byte sprite vertex: world position, texture coordinate, packed RGBA8 color.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct SpriteVertex(Vector2 Position, Vector2 TexCoord, uint Color)
{
    public static VertexLayout Layout { get; } = new(20,
    [
        new VertexElement(0, 2, VertexElementType.Float32, 0),
        new VertexElement(1, 2, VertexElementType.Float32, 8),
        new VertexElement(2, 4, VertexElementType.UNormByte, 16),
    ]);
}

[Flags]
public enum SpriteFlip
{
    None = 0,
    Horizontal = 1,
    Vertical = 2,
}

public enum SpriteSortMode
{
    /// <summary>Draw in submission order. Batches break whenever the texture changes.</summary>
    Deferred,

    /// <summary>Group by texture (in order of first use) for the fewest draw calls. Only for opaque or order-independent content.</summary>
    Texture,

    /// <summary>Higher depth first. Correct ordering for alpha-blended layers. Ties keep submission order.</summary>
    BackToFront,

    /// <summary>Lower depth first. Ties keep submission order.</summary>
    FrontToBack,
}
