using MNVTEngine.Mathematics;

namespace MNVTEngine.Scenes.Tests;

public sealed class MeshRendererTests
{
    [Fact]
    public void Settings_SurviveSerialization()
    {
        var texture = new AssetRef(Guid.Parse("e3000000-0000-4000-8000-000000000001"));
        var material = new AssetRef(Guid.Parse("e3000000-0000-4000-8000-000000000002"));
        var scene = new Scene();
        var renderer = scene.CreateGameObject("Mesh").AddComponent<MeshRenderer>();
        (renderer.Primitive, renderer.Color, renderer.Texture, renderer.Material, renderer.DoubleSided) = (PrimitiveMesh.Capsule, new Color(0.2f, 0.4f, 0.6f, 0.5f), texture, material, true);

        var serializer = new SceneSerializer(ComponentRegistry.CreateDefault());
        var copy = serializer.Deserialize(serializer.Serialize(scene)).Scene.Find("Mesh")!.GetComponent<MeshRenderer>()!;
        Assert.Equal((PrimitiveMesh.Capsule, new Color(0.2f, 0.4f, 0.6f, 0.5f), texture, material, true), (copy.Primitive, copy.Color, copy.Texture, copy.Material, copy.DoubleSided));
    }

    [Fact]
    public void Defaults_AreAWhiteCube()
    {
        var renderer = new Scene().CreateGameObject().AddComponent<MeshRenderer>();
        Assert.Equal((PrimitiveMesh.Cube, Color.White, false), (renderer.Primitive, renderer.Color, renderer.DoubleSided));
        Assert.True(renderer.Texture.IsNone && renderer.Material.IsNone);
    }
}
