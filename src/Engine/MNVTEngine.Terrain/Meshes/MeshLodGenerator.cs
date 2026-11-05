using MNVTEngine.Terrain.Geometry;

namespace MNVTEngine.Terrain.Meshes;

/// <summary>
/// Coarser LODs of an uploaded prop by decimating LOD0 (props have no procedural rebuild path). Each LOD keeps a fixed
/// fraction of the vertices, matching the presets every other exported element uses. Results are cached per asset path.
/// </summary>
public sealed class MeshLodGenerator
{
    /// <summary>Fraction of the original vertex count kept at each LOD index.</summary>
    private static readonly double[] KeepRatio = [1, 0.4, 0.15, 0.05];

    /// <summary>Below this the collapse has too few vertices to work with, or is not worth the time.</summary>
    private const int MinVerticesToSimplify = 24;

    private readonly Dictionary<(string Path, int Lod), IReadOnlyList<MeshGroup>> _cache = [];

    public IReadOnlyList<MeshGroup> GetLodGroups(string assetPath, int lodIndex, IReadOnlyList<MeshGroup> lod0)
    {
        ArgumentNullException.ThrowIfNull(lod0);
        var index = Math.Clamp(lodIndex, 0, LodLevels.MaxLodIndex);
        if (index == 0)
        {
            return lod0;
        }

        if (!_cache.TryGetValue((assetPath, index), out var groups))
        {
            groups = lod0.Select(g => SimplifyGroup(g, KeepRatio[index])).ToList();
            _cache[(assetPath, index)] = groups;
        }

        return groups;
    }

    /// <summary>Drops the cached LODs of an asset (its file or calibration changed).</summary>
    public void Invalidate(string assetPath)
    {
        for (var index = 1; index <= LodLevels.MaxLodIndex; index++)
        {
            _cache.Remove((assetPath, index));
        }
    }

    public static MeshGroup SimplifyGroup(MeshGroup group, double keepRatio)
    {
        ArgumentNullException.ThrowIfNull(group);
        var vertexCount = group.VertexCount;
        if (vertexCount < MinVerticesToSimplify)
        {
            return group;
        }

        if (keepRatio >= 1)
        {
            return group;
        }

        var (positions, uvs) = MeshSimplifier.Simplify(group.Positions, group.Uvs, keepRatio);
        return group with { Positions = positions, Uvs = uvs };
    }
}
