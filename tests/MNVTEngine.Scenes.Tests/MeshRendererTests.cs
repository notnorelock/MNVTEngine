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
    public void ModelMesh_SubAssetReference_SurvivesSerialization()
    {
        var mesh = new AssetRef(Guid.Parse("e3000000-0000-4000-8000-000000000003"), "mesh/Body_1");
        var scene = new Scene();
        scene.CreateGameObject("Mesh").AddComponent<MeshRenderer>().Mesh = mesh;

        var serializer = new SceneSerializer(ComponentRegistry.CreateDefault());
        var json = serializer.Serialize(scene);
        Assert.Contains("\"asset:e3000000-0000-4000-8000-000000000003#mesh/Body_1\"", json, StringComparison.Ordinal);
        Assert.Equal(mesh, serializer.Deserialize(json).Scene.Find("Mesh")!.GetComponent<MeshRenderer>()!.Mesh);
    }

    [Theory]
    [InlineData("asset:e3000000-0000-4000-8000-000000000003", null)]
    [InlineData("asset:e3000000-0000-4000-8000-000000000003#mesh/Body", "mesh/Body")]
    [InlineData("asset:e3000000-0000-4000-8000-000000000003#a#b", "a#b")]
    public void AssetRef_ParsesSubAssets(string text, string? subAsset)
    {
        Assert.True(AssetRef.TryParse(text, out var value));
        Assert.Equal(subAsset, value.SubAsset);
        Assert.Equal(text, value.ToString());
        Assert.Equal(new AssetRef(value.Guid), value.Asset);
    }

    [Theory]
    [InlineData("asset:e3000000-0000-4000-8000-000000000003#")]
    [InlineData("asset:00000000-0000-0000-0000-000000000000#mesh/Body")]
    [InlineData("asset:not-a-guid#mesh/Body")]
    public void AssetRef_RejectsMalformedSubAssets(string text) => Assert.False(AssetRef.TryParse(text, out _));

    [Fact]
    public void Defaults_AreAWhiteCube()
    {
        var renderer = new Scene().CreateGameObject().AddComponent<MeshRenderer>();
        Assert.Equal((PrimitiveMesh.Cube, Color.White, false), (renderer.Primitive, renderer.Color, renderer.DoubleSided));
        Assert.True(renderer.Texture.IsNone && renderer.Material.IsNone);
    }
}
