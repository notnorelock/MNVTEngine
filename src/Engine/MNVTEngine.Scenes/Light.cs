using MNVTEngine.Mathematics;

namespace MNVTEngine;

public enum LightType
{
    /// <summary>Parallel rays along the GameObject's forward (-Z), like the sun; position does not matter.</summary>
    Directional,

    /// <summary>Shines in every direction from the GameObject's position, fading out at <see cref="Light.Range"/>.</summary>
    Point,

    /// <summary>A cone along the GameObject's forward (-Z) from its position, up to <see cref="Light.Range"/>.</summary>
    Spot,
}

/// <summary>
/// A 3D light for meshes drawn with the Standard shader (and custom shaders that read the light uniforms). A scene with
/// any enabled <see cref="Light"/> or <see cref="AmbientLight"/> is lit; one with neither shows surfaces unlit, at full
/// brightness. Up to 8 lights affect a frame: directional lights first, then the point and spot lights nearest the camera.
/// </summary>
[ComponentId("MNVTEngine.Light")]
public sealed class Light : Component
{
    private float _range = 10f;
    private float _spotAngle = 30f;
    private float _innerSpotAngle = 21f;
    private float _shadowStrength = 1f;
    private float _shadowDistance = 40f;

    [SerializeField]
    public LightType Type { get; set; } = LightType.Directional;

    /// <summary>Light color (sRGB, like every color in the editor).</summary>
    [SerializeField]
    public Color Color { get; set; } = Color.White;

    /// <summary>Brightness: 1 lights a white surface facing the light to full white.</summary>
    [SerializeField]
    public float Intensity { get; set; } = 1f;

    /// <summary>Point and spot lights: distance (world units) at which the light has faded out.</summary>
    [SerializeField]
    public float Range { get => _range; set => _range = Math.Max(0.01f, value); }

    /// <summary>Spot lights: full angle of the cone in degrees (1–179).</summary>
    [SerializeField]
    public float SpotAngle { get => _spotAngle; set => _spotAngle = Math.Clamp(value, 1f, 179f); }

    /// <summary>Spot lights: full angle of the fully lit inner cone in degrees; the edge fades between the two.</summary>
    [SerializeField]
    public float InnerSpotAngle { get => _innerSpotAngle; set => _innerSpotAngle = Math.Clamp(value, 0f, 179f); }

    /// <summary>
    /// Directional lights: cast shadows from opaque meshes onto everything drawn with the Standard shader. One
    /// directional light per frame casts shadows (the first); spot and point light shadows are not supported yet.
    /// </summary>
    [SerializeField]
    public bool Shadows { get; set; }

    /// <summary>How dark shadows are: 0 = no shadow, 1 = no light from this light.</summary>
    [SerializeField]
    public float ShadowStrength { get => _shadowStrength; set => _shadowStrength = Math.Clamp(value, 0f, 1f); }

    /// <summary>Directional shadows: how far from the camera (world units) shadows are drawn; smaller is sharper.</summary>
    [SerializeField]
    public float ShadowDistance { get => _shadowDistance; set => _shadowDistance = Math.Max(1f, value); }
}

/// <summary>
/// Light that reaches every surface of a lit scene from all around (no direction, no shadows). Default when absent in a
/// lit scene: a dim blue-grey (0.25, 0.26, 0.3).
/// </summary>
[ComponentId("MNVTEngine.AmbientLight")]
public sealed class AmbientLight : Component
{
    [SerializeField]
    public Color Color { get; set; } = new(0.25f, 0.26f, 0.3f);

    [SerializeField]
    public float Intensity { get; set; } = 1f;
}
