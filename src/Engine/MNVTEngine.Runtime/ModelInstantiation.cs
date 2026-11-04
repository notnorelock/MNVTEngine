using MNVTEngine.Assets.Models;

namespace MNVTEngine.Runtime;

/// <summary>
/// Creates the GameObject hierarchy of an imported model: one GameObject per node with its local transform, and a
/// <see cref="MeshRenderer"/> referencing the node's mesh (<c>asset:&lt;guid&gt;#mesh/&lt;name&gt;</c>), so the scene keeps
/// referencing the model and picks up reimports. Skinned meshes are drawn in their bind pose (skinning comes later).
/// </summary>
public static class ModelInstantiation
{
    /// <summary>The model's root GameObject, named <paramref name="name"/>, under <paramref name="parent"/>.</summary>
    public static GameObject Instantiate(Scene scene, Guid model, ModelData data, string name, Transform? parent = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(data);
        if (data.Nodes.Count == 0)
        {
            throw new ArgumentException("The model has no nodes.", nameof(data));
        }

        var objects = new GameObject[data.Nodes.Count];
        for (var i = 0; i < data.Nodes.Count; i++)
        {
            var node = data.Nodes[i];
            var parentTransform = node.Parent < 0 ? parent : objects[node.Parent].Transform;
            var gameObject = scene.CreateGameObject(i == 0 ? name : node.Name, parentTransform);
            gameObject.Transform.LocalPosition = node.Position;
            gameObject.Transform.LocalRotation = node.Rotation;
            gameObject.Transform.LocalScale = node.Scale;
            if (node.Mesh >= 0 && node.Mesh < data.Meshes.Count)
            {
                var renderer = gameObject.AddComponent<MeshRenderer>();
                renderer.Primitive = PrimitiveMesh.None;
                renderer.Mesh = new AssetRef(model, ResourceCache.MeshSubAssetPrefix + data.Meshes[node.Mesh].Name);
                renderer.DoubleSided = data.Meshes[node.Mesh].SubMeshMaterials.Any(m => m >= 0 && m < data.Materials.Count && data.Materials[m].DoubleSided);
            }

            objects[i] = gameObject;
        }

        return objects[0];
    }
}
