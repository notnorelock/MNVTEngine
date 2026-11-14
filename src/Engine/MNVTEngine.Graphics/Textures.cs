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

    /// <summary>Frames over which <see cref="RequiredScale"/> keeps the largest reported size.</summary>
    public const int ScreenScaleWindow = 30;

    private long _scaleBucket = -2;
    private float _scaleCurrent;
    private float _scalePrevious;

    /// <summary>
    /// Set by texture streaming: renderers then report how large the texture is drawn (<see cref="ReportScreenScale"/>), so
    /// it can be loaded at the resolution the screen needs. Off for other textures, which skip the bookkeeping.
    /// </summary>
    public bool TrackScreenScale { get; set; }

    /// <summary>
    /// A renderer drew the texture this large on screen, as a fraction of its full size (1 = one texel per pixel, 0.25 =
    /// a quarter of its resolution is enough). Keeps the largest of the last <see cref="ScreenScaleWindow"/> to 2x frames.
    /// </summary>
    public void ReportScreenScale(float scale, long frame)
    {
        if (!float.IsFinite(scale))
        {
            scale = float.MaxValue;
        }

        var bucket = frame / ScreenScaleWindow;
        if (bucket != _scaleBucket)
        {
            _scalePrevious = bucket == _scaleBucket + 1 ? _scaleCurrent : 0f;
            (_scaleCurrent, _scaleBucket) = (0f, bucket);
        }

        _scaleCurrent = Math.Max(_scaleCurrent, scale);
    }

    /// <summary>
    /// The largest scale reported recently (<see cref="ReportScreenScale"/>); null when nothing reported one, in which case
    /// a texture that is drawn must be treated as needing its full resolution (some draws cannot tell their size).
    /// </summary>
    public float? RequiredScale(long frame)
    {
        var bucket = frame / ScreenScaleWindow;
        return bucket == _scaleBucket ? Math.Max(_scaleCurrent, _scalePrevious) : bucket == _scaleBucket + 1 ? _scaleCurrent : null;
    }

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
