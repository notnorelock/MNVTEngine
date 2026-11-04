using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;

namespace MNVTEngine;

/// <summary>Built-in meshes (see <c>MeshPrimitives</c>; unit-sized and centered on the origin).</summary>
public enum PrimitiveMesh
{
    None,

    /// <summary>1 × 1 × 1.</summary>
    Cube,

    /// <summary>Diameter 1.</summary>
    Sphere,

    /// <summary>Diameter 1, height 2 along Y.</summary>
    Capsule,

    /// <summary>Diameter 1, height 2 along Y.</summary>
    Cylinder,

    /// <summary>Base diameter 1, height 1 along Y.</summary>
    Cone,

    /// <summary>10 × 10 in the XZ plane, facing +Y.</summary>
    Plane,

    /// <summary>1 × 1 in the XY plane, facing +Z.</summary>
    Quad,
}

/// <summary>
/// Draws a mesh at the GameObject's transform in the 3D pass (before sprites), seen through the scene's main camera:
/// a mesh of an imported model (<see cref="Mesh"/>) or a built-in <see cref="Primitive"/>. Model meshes use the model's
/// materials, one per submesh. Without a material it uses the built-in Standard shader: texture × <see cref="Color"/>;
/// a color with alpha below 1 is drawn as transparent.
/// </summary>
[ComponentId("MNVTEngine.MeshRenderer")]
public sealed class MeshRenderer : Component
{
    [SerializeField]
    public PrimitiveMesh Primitive { get; set; } = PrimitiveMesh.Cube;

    /// <summary>A mesh of an imported model (<c>asset:&lt;guid&gt;#mesh/&lt;name&gt;</c>); when set it replaces <see cref="Primitive"/>.</summary>
    [SerializeField]
    [AssetType(AssetTypes.Model)]
    public AssetRef Mesh { get; set; }

    /// <summary>Tint of the built-in material.</summary>
    [SerializeField]
    public Color Color { get; set; } = Color.White;

    /// <summary>Texture of the built-in material (UV0); none draws the color alone.</summary>
    [SerializeField]
    [AssetType(AssetTypes.Texture)]
    public AssetRef Texture { get; set; }

    /// <summary>
    /// A custom material (<c>.mnvtmat</c>) whose shader reads the mesh vertex layout (locations 0 position, 1 normal,
    /// 2 tangent, 3 uv0, 4 uv1, 5 color) and <c>uViewProjection</c>/<c>uModel</c>; none uses the model's materials
    /// (model meshes) or the built-in one. Set, it draws every submesh.
    /// </summary>
    [SerializeField]
    [AssetType(AssetTypes.Material)]
    public AssetRef Material { get; set; }

    /// <summary>Draw both sides of each face (thin surfaces such as planes seen from below).</summary>
    [SerializeField]
    public bool DoubleSided { get; set; }
}
