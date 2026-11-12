using MNVTEngine.Mathematics;

namespace MNVTEngine;

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
