using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;

namespace MNVTEngine;

/// <summary>Triangles of a mesh in its local space: positions, and three indices per triangle.</summary>
public sealed record MeshData(Mathematics.Vector3[] Positions, int[] Indices);

/// <summary>Implemented by the host: the triangles a <see cref="MeshRenderer"/> draws (primitive or model mesh), or null.</summary>
public interface IMeshDataProvider
{
    MeshData? GetMeshData(MeshRenderer renderer);
}

/// <summary>
/// A component that supplies its GameObject's collision triangles itself (procedural geometry such as terrain), in the
/// GameObject's local space. A <c>MeshCollider</c> uses it instead of the <see cref="MeshRenderer"/>; call
/// <c>Physics.RefreshShape</c> after the triangles change.
/// </summary>
public interface IMeshDataSource
{
    MeshData? GetMeshData();
}

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

    private float[] _blendShapeWeights = [];

    /// <summary>
    /// Blend shape (morph target) weights of a model mesh, by shape index: 0 is the base shape, 1 the full shape (values
    /// outside 0–1 extrapolate). Shapes without an entry use the model's default weight. Animated by
    /// <see cref="ModelAnimator"/> clips that have weight channels.
    /// </summary>
    [SerializeField]
    [HideInInspector] // edited as named sliders
    [Newtonsoft.Json.JsonProperty(ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace)]
    public IReadOnlyList<float> BlendShapeWeights { get => _blendShapeWeights; set => _blendShapeWeights = [.. value ?? []]; }

    /// <summary>The blend shape names of <see cref="Mesh"/> (empty for primitives, or when the host cannot tell).</summary>
    public IReadOnlyList<string> BlendShapeNames => GameObject.Scene.ModelAnimations?.BlendShapeNames(Mesh) ?? [];

    /// <summary>Changes whenever a weight is set, so renderers know when to deform again.</summary>
    public int BlendShapeVersion { get; private set; }

    private readonly Dictionary<string, MaterialOverride> _materialOverrides = new(StringComparer.Ordinal);

    /// <summary>
    /// Material properties this renderer draws with instead of its materials' own values (every submesh), set from game
    /// code: <c>SetMaterialColor("_BaseColor", Color.Red)</c>. Other renderers sharing the material are unaffected.
    /// Runtime only: not saved with the scene. Only values change; blend mode and render queue stay the material's.
    /// </summary>
    public IReadOnlyDictionary<string, MaterialOverride> MaterialOverrides => _materialOverrides;

    /// <summary>Colors are sRGB, like every engine color (and the material inspector).</summary>
    public void SetMaterialColor(string property, Color color) => SetOverride(property, new MaterialOverride(new Vector4(color.R, color.G, color.B, color.A), default));

    public void SetMaterialFloat(string property, float value) => SetOverride(property, new MaterialOverride(new Vector4(value, 0f, 0f, 0f), default));

    public void SetMaterialVector(string property, Vector4 value) => SetOverride(property, new MaterialOverride(value, default));

    /// <summary>A texture asset for a sampler property (<c>_BaseColorMap</c>, <c>_NormalMap</c>, ...).</summary>
    public void SetMaterialTexture(string property, AssetRef texture) => SetOverride(property, new MaterialOverride(default, texture.IsNone ? throw new ArgumentException("Use ClearMaterialOverride to remove a texture override.", nameof(texture)) : texture));

    /// <summary>Draws a property with the material's own value again; false when it was not overridden.</summary>
    public bool ClearMaterialOverride(string property) => _materialOverrides.Remove(property);

    public void ClearMaterialOverrides() => _materialOverrides.Clear();

    private void SetOverride(string property, MaterialOverride value)
    {
        ArgumentException.ThrowIfNullOrEmpty(property);
        _materialOverrides[property] = value;
    }

    /// <summary>A shape's current weight: the one set, else the model's default, else 0.</summary>
    public float GetBlendShapeWeight(int index) =>
        index >= 0 && index < _blendShapeWeights.Length ? _blendShapeWeights[index] : DefaultWeight(index);

    public float GetBlendShapeWeight(string name) => GetBlendShapeWeight(IndexOf(name));

    /// <summary>For renderers: the weight set for a shape, else <paramref name="defaults"/>, else 0 (no host lookup).</summary>
    public float GetBlendShapeWeightOrDefault(int index, IReadOnlyList<float> defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        return index >= 0 && index < _blendShapeWeights.Length ? _blendShapeWeights[index] : index >= 0 && index < defaults.Count ? defaults[index] : 0f;
    }

    /// <summary>Sets a shape's weight by index (shapes below it that were never set take their default weight).</summary>
    public void SetBlendShapeWeight(int index, float weight)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index >= _blendShapeWeights.Length)
        {
            var grown = new float[index + 1];
            _blendShapeWeights.CopyTo(grown, 0);
            for (var i = _blendShapeWeights.Length; i < grown.Length; i++)
            {
                grown[i] = DefaultWeight(i);
            }

            _blendShapeWeights = grown;
        }

        if (_blendShapeWeights[index] != weight)
        {
            _blendShapeWeights[index] = weight;
            BlendShapeVersion++;
        }
    }

    /// <summary>Sets a shape's weight by name; false (and nothing changes) when the mesh has no such shape.</summary>
    public bool SetBlendShapeWeight(string name, float weight)
    {
        var index = IndexOf(name);
        if (index < 0)
        {
            return false;
        }

        SetBlendShapeWeight(index, weight);
        return true;
    }

    /// <summary>Back to the model's default weights.</summary>
    public void ResetBlendShapeWeights()
    {
        _blendShapeWeights = [];
        BlendShapeVersion++;
    }

    private float DefaultWeight(int index) =>
        GameObject.Scene.ModelAnimations?.BlendShapeDefaults(Mesh) is { } defaults && index >= 0 && index < defaults.Count ? defaults[index] : 0f;

    private int IndexOf(string name)
    {
        var names = BlendShapeNames;
        for (var i = 0; i < names.Count; i++)
        {
            if (names[i] == name)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>A material property value set on one <see cref="MeshRenderer"/>: a texture when <see cref="Texture"/> is set, else <see cref="Value"/> (a float is its X).</summary>
public readonly record struct MaterialOverride(Vector4 Value, AssetRef Texture)
{
    public bool IsTexture => !Texture.IsNone;
}
