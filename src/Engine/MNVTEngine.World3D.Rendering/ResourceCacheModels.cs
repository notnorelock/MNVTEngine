using MNVTEngine.Assets.Models;
using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Meshes;

namespace MNVTEngine.Runtime;

/// <summary>The world.3d members of <see cref="ResourceCache"/>, served by its <see cref="ModelCache"/> extension.</summary>
public static class ResourceCacheModels
{
    extension(ResourceCache resources)
    {
        /// <summary>The cache of models and environments (added by the world.3d module).</summary>
        /// <exception cref="InvalidOperationException">world.3d is not part of the project.</exception>
        public ModelCache Models => resources.GetExtension<ModelCache>();

        public ModelData? GetModel(Guid guid) => resources.Models.GetModel(guid);

        public ModelMesh? GetModelMesh(AssetRef asset) => resources.Models.GetModelMesh(asset);

        public Material? GetModelMaterial(Guid model, int index) => resources.Models.GetModelMaterial(model, index);

        public Texture2D? GetEnvironmentTexture(AssetRef asset) => resources.Models.GetEnvironmentTexture(asset);
    }
}
