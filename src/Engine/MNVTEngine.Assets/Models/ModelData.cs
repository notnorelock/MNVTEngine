using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Assets.Models;

/// <summary>
/// An imported model (FBX, glTF/GLB) in engine form: the node hierarchy, meshes with submeshes, materials, textures,
/// skins and animation clips. Produced by the model importer, stored as an artifact, read by the runtime; no importer
/// library is needed to use it. Conventions are the engine's: right-handed, +Y up, meters (after the import scale).
/// </summary>
public sealed class ModelData
{
    /// <summary>Nodes in parent-first order; node 0 is the root.</summary>
    public List<ModelNode> Nodes { get; } = [];

    public List<ModelMesh> Meshes { get; } = [];

    public List<ModelMaterial> Materials { get; } = [];

    public List<ModelTexture> Textures { get; } = [];

    public List<ModelSkin> Skins { get; } = [];

    public List<ModelAnimation> Animations { get; } = [];

    /// <summary>Problems found while importing (also reported on the asset).</summary>
    public List<string> Warnings { get; } = [];

    public ModelMesh? FindMesh(string name) => Meshes.Find(m => m.Name == name);

    public ModelMaterial? FindMaterial(string name) => Materials.Find(m => m.Name == name);

    public int VertexCount => Meshes.Sum(m => m.Mesh.VertexCount);

    public int TriangleCount => Meshes.Sum(m => m.Mesh.TriangleCount);
}

/// <param name="Parent">Index of the parent node, -1 for the root.</param>
/// <param name="Mesh">Index into <see cref="ModelData.Meshes"/>, or -1.</param>
/// <param name="Skin">Index into <see cref="ModelData.Skins"/> when the mesh is skinned, or -1.</param>
public sealed record ModelNode(string Name, int Parent, Vector3 Position, Quaternion Rotation, Vector3 Scale, int Mesh = -1, int Skin = -1);

/// <summary>A mesh with one material index per submesh; skinned meshes carry four joints and weights per vertex.</summary>
public sealed class ModelMesh(string name, Mesh mesh)
{
    /// <summary>Unique within the model; the mesh's sub-asset name.</summary>
    public string Name { get; } = name;

    public Mesh Mesh { get; } = mesh;

    /// <summary>Index into <see cref="ModelData.Materials"/> for each submesh (-1: none).</summary>
    public List<int> SubMeshMaterials { get; } = [];

    /// <summary>Four joint indices (into the skin's joint list) per vertex; null when not skinned.</summary>
    public ushort[]? Joints { get; set; }

    /// <summary>Four weights per vertex, summing to 1; null when not skinned.</summary>
    public float[]? Weights { get; set; }

    /// <summary>Blend shape count in the source (imported in a later version; reported, not dropped silently).</summary>
    public int MorphTargetCount { get; set; }
}

public enum ModelAlphaMode
{
    Opaque,

    /// <summary>Alpha test against <see cref="ModelMaterial.AlphaCutoff"/>.</summary>
    Mask,

    Blend,
}

/// <summary>Texture roles of a metallic-roughness material (glTF 2.0 semantics).</summary>
public enum TextureRole
{
    BaseColor,
    MetallicRoughness,
    Metallic,
    Roughness,
    Normal,
    Occlusion,
    Emission,
    Opacity,

    /// <summary>Height/displacement: kept for inspection, not used by the Standard shader.</summary>
    Height,

    /// <summary>Connected in the source with a role the importer could not map; assign it by hand.</summary>
    Unclassified,
}

/// <summary>A source material converted to metallic-roughness PBR, with its texture connections by role.</summary>
public sealed class ModelMaterial(string name)
{
    /// <summary>Unique within the model; the material's sub-asset name (and the key for extraction and remapping).</summary>
    public string Name { get; } = name;

    public Color BaseColor { get; set; } = Color.White;

    public float Metallic { get; set; }

    public float Roughness { get; set; } = 0.5f;

    public Color Emissive { get; set; } = new(0f, 0f, 0f, 1f);

    public float NormalScale { get; set; } = 1f;

    public float OcclusionStrength { get; set; } = 1f;

    public ModelAlphaMode AlphaMode { get; set; }

    public float AlphaCutoff { get; set; } = 0.5f;

    public bool DoubleSided { get; set; }

    /// <summary>Texture index (into <see cref="ModelData.Textures"/>) by role.</summary>
    public Dictionary<TextureRole, int> Textures { get; } = [];

    /// <summary>How the source described the material (e.g. "glTF PBR", "FBX Phong"), for the inspector.</summary>
    public string SourceModel { get; set; } = "";
}

/// <summary>
/// A texture the model uses: embedded in the source (encoded bytes kept, decoded at load time), or external and found
/// in the project (<see cref="ProjectAsset"/>), or external and not found (reported).
/// </summary>
public sealed class ModelTexture(string name)
{
    public string Name { get; } = name;

    /// <summary>Encoded image (PNG/JPEG/...) when embedded in the model file.</summary>
    public byte[]? Embedded { get; set; }

    /// <summary>The project texture asset for an external file inside <c>Assets/</c>.</summary>
    public Guid? ProjectAsset { get; set; }

    /// <summary>The path as written in the source file (external textures).</summary>
    public string? SourcePath { get; set; }

    public bool IsResolved => Embedded is not null || ProjectAsset is not null;
}

/// <summary>A skeleton: joint nodes and their inverse bind matrices.</summary>
public sealed class ModelSkin(string name)
{
    public string Name { get; } = name;

    public List<int> Joints { get; } = [];

    public List<Matrix4x4> InverseBindMatrices { get; } = [];
}

public enum AnimationPath
{
    Translation,
    Rotation,
    Scale,
}

public enum AnimationInterpolation
{
    Step,
    Linear,

    /// <summary>Cubic spline: each key stores in-tangent, value, out-tangent.</summary>
    CubicSpline,
}

/// <param name="Values">3 floats (translation, scale) or 4 (rotation quaternion x, y, z, w) per key; three times that for cubic splines.</param>
public sealed record AnimationChannel(int Node, AnimationPath Path, AnimationInterpolation Interpolation, float[] Times, float[] Values);

public sealed class ModelAnimation(string name)
{
    public string Name { get; } = name;

    public List<AnimationChannel> Channels { get; } = [];

    public float Duration => Channels.Count == 0 ? 0f : Channels.Max(c => c.Times.Length == 0 ? 0f : c.Times[^1]);
}
