using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics;

public enum TextureFilter
{
    /// <summary>Nearest-neighbour sampling; crisp pixel art.</summary>
    Point,

    /// <summary>Bilinear sampling (trilinear when mipmaps exist).</summary>
    Linear,
}

public enum TextureWrap
{
    Clamp,
    Repeat,
    MirroredRepeat,
}

public readonly record struct SamplerState(TextureFilter Filter, TextureWrap Wrap, bool GenerateMipmaps = false)
{
    public static SamplerState PointClamp => new(TextureFilter.Point, TextureWrap.Clamp);
    public static SamplerState LinearClamp => new(TextureFilter.Linear, TextureWrap.Clamp);
    public static SamplerState PointRepeat => new(TextureFilter.Point, TextureWrap.Repeat);
    public static SamplerState LinearRepeat => new(TextureFilter.Linear, TextureWrap.Repeat);
}

/// <summary>RGBA8 2D texture.</summary>
public abstract class Texture2D : GraphicsResource
{
    protected Texture2D(GraphicsResourceTracker? tracker, string debugName, int width, int height, SamplerState sampler)
        : base(tracker, debugName)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        Sampler = sampler;
    }

    public int Width { get; }

    public int Height { get; }

    public Vector2 Size => new(Width, Height);

    public SamplerState Sampler { get; }

    /// <summary>
    /// The device frame (<see cref="IGraphicsDevice.FrameNumber"/>) in which the texture was last bound for drawing; 0 when
    /// never, or when the backend does not track it. Used to stream and evict textures by use.
    /// </summary>
    public long LastDrawnFrame { get; protected set; }

    /// <summary>
    /// Size of the image the texture holds now. Equals <see cref="Width"/> x <see cref="Height"/>, except for a streamed
    /// texture showing its low-resolution proxy (<see cref="IGraphicsDevice.CreateStreamingTexture"/>), which keeps
    /// reporting its full size so sprite sizes and source rectangles stay right.
    /// </summary>
    public virtual (int Width, int Height) ResidentSize => (Width, Height);

    /// <summary>
    /// True when texel row 0 is the bottom of the image (render targets). Uploaded images store row 0 at the top.
    /// Sprite rendering uses this to pick texture coordinates so both appear upright.
    /// </summary>
    public virtual bool IsFlippedVertically => false;
}

/// <summary>Offscreen color target that can be drawn to and then sampled like any texture.</summary>
public abstract class RenderTarget2D : Texture2D
{
    protected RenderTarget2D(GraphicsResourceTracker? tracker, string debugName, int width, int height, SamplerState sampler)
        : base(tracker, debugName, width, height, sampler)
    {
    }

    public override bool IsFlippedVertically => true;

    /// <summary>The target has a depth buffer, so 3D drawing into it can depth-test.</summary>
    public virtual bool HasDepth => false;
}
