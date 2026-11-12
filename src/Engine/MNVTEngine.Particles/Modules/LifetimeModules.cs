namespace MNVTEngine.Particles;

/// <summary>Space a velocity or force is given in: the emitter's (rotates with it) or the world's.</summary>
public enum ParticleSpace
{
    Local,
    World,
}

/// <summary>Extra velocity over a particle's life (moves it without changing its own velocity), scaled by <see cref="SpeedModifier"/>.</summary>
public sealed class VelocityOverLifetimeModule
{
    public bool Enabled { get; set; }

    public MinMaxCurve X { get; set; } = 0f;

    public MinMaxCurve Y { get; set; } = 0f;

    public MinMaxCurve Z { get; set; } = 0f;

    public ParticleSpace Space { get; set; }

    /// <summary>Multiplies the particle's whole velocity (its own plus this module's).</summary>
    public MinMaxCurve SpeedModifier { get; set; } = 1f;
}

/// <summary>Acceleration over a particle's life (changes its velocity).</summary>
public sealed class ForceOverLifetimeModule
{
    public bool Enabled { get; set; }

    public MinMaxCurve X { get; set; } = 0f;

    public MinMaxCurve Y { get; set; } = 0f;

    public MinMaxCurve Z { get; set; } = 0f;

    public ParticleSpace Space { get; set; }
}

/// <summary>Multiplies the start color over a particle's life.</summary>
public sealed class ColorOverLifetimeModule
{
    public bool Enabled { get; set; }

    public MinMaxGradient Color { get; set; } = new(ParticleGradient.Between(Mathematics.Color.White, Mathematics.Color.White.WithAlpha(0f)));
}

/// <summary>Multiplies the start size over a particle's life.</summary>
public sealed class SizeOverLifetimeModule
{
    public bool Enabled { get; set; }

    /// <summary>The size multiplier, or the X multiplier with <see cref="SeparateAxes"/>.</summary>
    public MinMaxCurve Size { get; set; } = new(ParticleCurve.Linear(1f, 0f));

    public bool SeparateAxes { get; set; }

    public MinMaxCurve Y { get; set; } = 1f;

    public MinMaxCurve Z { get; set; } = 1f;
}

/// <summary>Turns particles over their life, in degrees per second (2D and billboards: around their facing axis).</summary>
public sealed class RotationOverLifetimeModule
{
    public bool Enabled { get; set; }

    /// <summary>Degrees per second around Z (the facing axis), or around Z with <see cref="SeparateAxes"/>.</summary>
    public MinMaxCurve AngularVelocity { get; set; } = 45f;

    public bool SeparateAxes { get; set; }

    public MinMaxCurve X { get; set; } = 0f;

    public MinMaxCurve Y { get; set; } = 0f;
}

public enum TextureSheetTimeMode
{
    /// <summary><see cref="TextureSheetAnimationModule.FrameOverTime"/> over the particle's life.</summary>
    Lifetime,

    /// <summary><see cref="TextureSheetAnimationModule.FramesPerSecond"/>.</summary>
    FramesPerSecond,
}

/// <summary>Animates particles through the frames of a sprite sheet laid out as a grid (row by row from the top-left).</summary>
public sealed class TextureSheetAnimationModule
{
    public bool Enabled { get; set; }

    public int Columns { get; set; } = 1;

    public int Rows { get; set; } = 1;

    public TextureSheetTimeMode TimeMode { get; set; }

    /// <summary>Position in the animation, 0 (first frame) to 1 (past the last), over the particle's life.</summary>
    public MinMaxCurve FrameOverTime { get; set; } = new(ParticleCurve.Linear(0f, 1f));

    public float FramesPerSecond { get; set; } = 10f;

    /// <summary>Frame the particle starts on (use a random range for a random frame).</summary>
    public MinMaxCurve StartFrame { get; set; } = 0f;

    /// <summary>Times the animation plays over a particle's life (<see cref="TextureSheetTimeMode.Lifetime"/>).</summary>
    public float Cycles { get; set; } = 1f;

    /// <summary>Wraps past the last frame; otherwise the last frame holds.</summary>
    public bool Loop { get; set; } = true;

    public int FrameCount => Math.Max(1, Columns) * Math.Max(1, Rows);
}
