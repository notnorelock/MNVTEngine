using MNVTEngine.Mathematics;

namespace MNVTEngine;

public enum HierarchyStyleKind
{
    /// <summary>A colored marker and background behind the row (a gradient when <see cref="HierarchyStyle.Gradient"/> is on).</summary>
    Label,

    /// <summary>A full-width bar with the name centered: a section title such as "Managers" or "Environment".</summary>
    Header,
}

/// <summary>
/// How the editor's Hierarchy draws this GameObject: a colored label or a section header. Editor data only: games
/// load it with the scene and ignore it.
/// </summary>
[ComponentId("MNVTEngine.HierarchyStyle")]
public sealed class HierarchyStyle : Component
{
    [SerializeField]
    public HierarchyStyleKind Kind { get; set; } = HierarchyStyleKind.Label;

    [SerializeField]
    public Color Color { get; set; } = new(0.91f, 0.5f, 0.16f);

    /// <summary>Fades the color in from the left (labels) or from both ends (headers) instead of a solid fill.</summary>
    [SerializeField]
    public bool Gradient { get; set; }
}
