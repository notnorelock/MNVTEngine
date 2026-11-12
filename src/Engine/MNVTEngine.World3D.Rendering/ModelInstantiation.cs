using MNVTEngine.Assets.Models;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Runtime;

/// <summary>
/// Creates the GameObject hierarchy of an imported model: one GameObject per node with its local transform, and a
/// <see cref="MeshRenderer"/> referencing the node's mesh (<c>asset:&lt;guid&gt;#mesh/&lt;name&gt;</c>), so the scene keeps
/// referencing the model and picks up reimports. A model with animation clips gets a <see cref="ModelAnimator"/> on its
/// root; skinned meshes follow their joint GameObjects. Lights in the model become <see cref="Light"/> components and
/// cameras become disabled <see cref="Camera"/> components (enable one to look through it; enabled, it could take over
/// the scene's main camera).
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
                renderer.Mesh = new AssetRef(model, ModelCache.MeshSubAssetPrefix + data.Meshes[node.Mesh].Name);
                renderer.DoubleSided = data.Meshes[node.Mesh].SubMeshMaterials.Any(m => m >= 0 && m < data.Materials.Count && data.Materials[m].DoubleSided);
                renderer.BlendShapeWeights = data.Meshes[node.Mesh].DefaultBlendShapeWeights; // the shape the model was saved in
            }

            objects[i] = gameObject;
        }

        if (data.Animations.Count > 0)
        {
            objects[0].AddComponent<ModelAnimator>().Model = new AssetRef(model); // plays the first clip on start
        }

        foreach (var source in data.Lights.Where(l => l.Node >= 0 && l.Node < objects.Length))
        {
            var light = Holder<Light>(scene, objects[source.Node], source.Name, source.Rotation).AddComponent<Light>();
            light.Type = source.Type switch { ModelLightType.Directional => LightType.Directional, ModelLightType.Spot => LightType.Spot, _ => LightType.Point };
            (light.Color, light.Intensity) = (source.Color, source.Intensity);
            // No range in the source (glTF: infinite): where an inverse-square light of this intensity has faded to 1%.
            light.Range = source.Range ?? MathF.Max(1f, MathF.Sqrt(source.Intensity * 100f));
            (light.SpotAngle, light.InnerSpotAngle) = (source.SpotAngle, source.InnerSpotAngle);
        }

        foreach (var source in data.Cameras.Where(c => c.Node >= 0 && c.Node < objects.Length))
        {
            var camera = Holder<Camera>(scene, objects[source.Node], source.Name, source.Rotation).AddComponent<Camera>();
            camera.Projection = source.Orthographic ? CameraProjection.Orthographic : CameraProjection.Perspective;
            (camera.FieldOfView, camera.OrthographicSize, camera.FarClip, camera.NearClip) = (source.FieldOfView, source.OrthographicSize, source.FarClip, source.NearClip);
            camera.Enabled = false;
        }

        return objects[0];
    }

    /// <summary>
    /// The node's GameObject, or a child of it when the source points the light or camera another way than the engine's
    /// -Z, or the node already has a <typeparamref name="T"/>.
    /// </summary>
    private static GameObject Holder<T>(Scene scene, GameObject node, string name, Quaternion rotation)
        where T : Component
    {
        if (rotation == Quaternion.Identity && node.GetComponent<T>() is null)
        {
            return node;
        }

        var child = scene.CreateGameObject(name, node.Transform);
        child.Transform.LocalRotation = rotation;
        return child;
    }
}
