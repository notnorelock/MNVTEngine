namespace MNVTEngine.Particles;

public enum ParticleBlendMode
{
    Alpha,
    Additive,
}

/// <summary>How a particle is oriented. 2D draws every mode as a sprite in the XY plane.</summary>
public enum ParticleRenderMode
{
    /// <summary>3D: faces the camera.</summary>
    Billboard,

    /// <summary>3D: parallel to the camera's view plane.</summary>
    ViewAligned,

    /// <summary>3D: lies flat on the XZ plane (puddles, ground dust).</summary>
    HorizontalBillboard,

    /// <summary>3D: upright, turning only around Y toward the camera.</summary>
    VerticalBillboard,

    /// <summary>Stretched along its velocity (sparks, rain).</summary>
    Stretched,

    /// <summary>3D: oriented by its own 3D rotation.</summary>
    WorldOriented,
}

public enum ParticleSortMode
{
    None,

    /// <summary>3D: farthest from the camera first (correct alpha blending).</summary>
    ByDistance,

    OldestInFront,

    YoungestInFront,
}

/// <summary>How the particles of a system are drawn. Data only: the 2D and 3D particle modules draw them.</summary>
public sealed class ParticleRendererSettings
{
    /// <summary>Particle texture; none draws white squares (2D) or quads (3D).</summary>
    public AssetRef Texture { get; set; }

    /// <summary>A material (.mnvtmat) to draw with instead of the built-in one.</summary>
    public AssetRef Material { get; set; }

    public ParticleBlendMode Blend { get; set; }

    public ParticleRenderMode RenderMode { get; set; }

    public ParticleSortMode SortMode { get; set; }

    /// <summary>2D: draw order among sprites (higher on top). 3D: order among transparent systems at the same distance.</summary>
    public int SortingOrder { get; set; }

    /// <summary><see cref="ParticleRenderMode.Stretched"/>: length added per unit of speed.</summary>
    public float SpeedScale { get; set; } = 0.1f;

    /// <summary><see cref="ParticleRenderMode.Stretched"/>: length as a multiple of the size.</summary>
    public float LengthScale { get; set; } = 1f;
}
