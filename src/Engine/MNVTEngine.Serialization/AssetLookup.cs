using System.Diagnostics.CodeAnalysis;

namespace MNVTEngine.Serialization;

/// <summary>Resolves persistent asset identities. Implemented by the asset database; used by loaders and migrations.</summary>
public interface IAssetLookup
{
    /// <param name="relativePath">Path relative to the Assets folder, forward slashes.</param>
    bool TryGetGuid(string relativePath, out Guid guid);

    bool TryGetPath(Guid guid, [NotNullWhen(true)] out string? relativePath);

    /// <summary>Asset type name (see <see cref="AssetTypes"/>), or null for unknown assets.</summary>
    string? GetAssetType(Guid guid);
}

/// <summary>Asset type names produced by importers and requested by <c>[AssetType]</c> members.</summary>
public static class AssetTypes
{
    public const string Texture = "Texture";
    public const string Scene = "Scene";
    public const string Prefab = "Prefab";
    public const string Audio = "Audio";
    public const string Font = "Font";
    public const string Shader = "Shader";
    public const string Script = "Script";
    public const string Json = "Json";
    public const string Animation = "Animation";
    public const string Material = "Material";
    public const string Text = "Text";

    /// <summary>A 3D model (FBX, glTF, GLB); its processed form is the <c>model</c> artifact.</summary>
    public const string Model = "Model";

    /// <summary>A saved UI tree (<c>.mnvtui</c>).</summary>
    public const string UILayout = "UILayout";

    /// <summary>A state machine of model clips (<c>.mnvtanimator</c>), played by <c>ModelAnimatorController</c>.</summary>
    public const string AnimatorController = "AnimatorController";

    /// <summary>A terrain world (<c>.mnvtterrain</c>: TerrainHeaven project JSON), drawn by <c>WorldTerrain</c>.</summary>
    public const string Terrain = "Terrain";
    public const string Unknown = "Unknown";
}
