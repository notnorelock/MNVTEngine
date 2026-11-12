using MNVTEngine.Runtime;

namespace MNVTEngine;

/// <summary>Terrain asset references, resolved against a project's assets (module terrain).</summary>
public static class TerrainResources
{
    extension(ResourceCache resources)
    {
        /// <summary>
        /// An asset a terrain world references: an <c>asset:</c> reference as is, else a path next to the terrain file, else
        /// from the Assets root (TerrainHeaven's project-relative paths). None when nothing matches.
        /// </summary>
        public AssetRef ResolveTerrainReference(AssetRef terrain, string reference)
        {
            if (AssetRef.TryParse(reference, out var direct))
            {
                return direct;
            }

            var terrainPath = terrain.IsNone ? "" : resources.Assets.Get(terrain.Guid)?.Path ?? "";
            foreach (var candidate in Terrain.Project.TerrainProject.ResolveCandidates(terrainPath, reference))
            {
                if (resources.Assets.TryGetGuid(candidate, out var guid))
                {
                    return new AssetRef(guid);
                }
            }

            return AssetRef.None;
        }
    }
}
