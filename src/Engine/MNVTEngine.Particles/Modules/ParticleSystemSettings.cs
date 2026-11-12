namespace MNVTEngine.Particles;

/// <summary>
/// Everything that defines a particle system, as stored in a scene (inline on a <see cref="ParticleSystem"/>) or in a
/// <c>.mnvtparticle</c> asset (<see cref="ParticleSystemAsset"/>). Disabled modules cost nothing per particle.
/// </summary>
public sealed class ParticleSystemSettings
{
    public MainModule Main { get; set; } = new();

    public EmissionModule Emission { get; set; } = new();

    public ShapeModule Shape { get; set; } = new();

    public VelocityOverLifetimeModule VelocityOverLifetime { get; set; } = new();

    public ForceOverLifetimeModule ForceOverLifetime { get; set; } = new();

    public ColorOverLifetimeModule ColorOverLifetime { get; set; } = new();

    public SizeOverLifetimeModule SizeOverLifetime { get; set; } = new();

    public RotationOverLifetimeModule RotationOverLifetime { get; set; } = new();

    public TextureSheetAnimationModule TextureSheetAnimation { get; set; } = new();

    public ParticleRendererSettings Renderer { get; set; } = new();

    /// <summary>A deep copy (through the asset format), for editing without touching the original.</summary>
    public ParticleSystemSettings Clone() => ParticleSystemAsset.Parse(ParticleSystemAsset.Serialize(this), "clone");

    /// <summary>Names of the enabled optional modules (diagnostics).</summary>
    public IEnumerable<string> EnabledModules()
    {
        if (VelocityOverLifetime.Enabled)
        {
            yield return "velocityOverLifetime";
        }

        if (ForceOverLifetime.Enabled)
        {
            yield return "forceOverLifetime";
        }

        if (ColorOverLifetime.Enabled)
        {
            yield return "colorOverLifetime";
        }

        if (SizeOverLifetime.Enabled)
        {
            yield return "sizeOverLifetime";
        }

        if (RotationOverLifetime.Enabled)
        {
            yield return "rotationOverLifetime";
        }

        if (TextureSheetAnimation.Enabled)
        {
            yield return "textureSheetAnimation";
        }
    }
}
