using System.Diagnostics.CodeAnalysis;
using MNVTEngine.Core.Timing;
using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Scenes.Tests;

public class AssetAndPrefabTests
{
    private static readonly Guid Texture = Guid.Parse("0f5e0000-0000-4000-8000-000000000001");
    private static readonly ComponentRegistry Registry = ComponentRegistry.CreateDefault().Register<Inspector>().Register<AwakeProbe>();

    [Fact]
    public void AssetRef_RoundTripsAsPrefixedString()
    {
        var scene = new Scene();
        scene.CreateGameObject("S").AddComponent<SpriteRenderer>().Texture = new AssetRef(Texture);
        var serializer = new SceneSerializer(Registry);
        var json = serializer.Serialize(scene);

        Assert.Contains($"\"texture\": \"asset:{Texture}\"", json, StringComparison.Ordinal);
        Assert.Equal(new AssetRef(Texture), serializer.Deserialize(json).Scene.Find("S")!.GetComponent<SpriteRenderer>()!.Texture);

        Assert.True(AssetRef.TryParse($"asset:{Texture}", out var parsed));
        Assert.Equal(Texture, parsed.Guid);
        Assert.False(AssetRef.TryParse(Texture.ToString(), out _));
        Assert.False(AssetRef.TryParse("asset:00000000-0000-0000-0000-000000000000", out _));
        Assert.Equal("None", AssetRef.None.ToString());
    }

    [Fact]
    public void AssetRef_MalformedValueIsAnError()
    {
        var json = $$"""{ "schemaVersion": 2, "gameObjects": [ { "id": "{{Guid.NewGuid()}}", "components": [ { "type": "MNVTEngine.SpriteRenderer", "data": { "texture": "Textures/a.png" } } ] } ] }""";
        Assert.True(new SceneSerializer(Registry).Deserialize(json).HasErrors);
    }

    [Fact]
    public void SchemaV1_TexturePathsMigrateToAssetRefs()
    {
        var json = $$"""
            { "schemaVersion": 1, "name": "Old", "gameObjects": [
              { "id": "{{Guid.NewGuid()}}", "name": "Known", "components": [ { "type": "MNVTEngine.SpriteRenderer", "data": { "texture": "Textures/player.png" } } ] },
              { "id": "{{Guid.NewGuid()}}", "name": "Unknown", "components": [ { "type": "MNVTEngine.SpriteRenderer", "data": { "texture": "Textures/gone.png" } } ] } ] }
            """;
        var result = new SceneSerializer(Registry, new FakeLookup()).Deserialize(json);

        Assert.Equal(new AssetRef(Texture), result.Scene.Find("Known")!.GetComponent<SpriteRenderer>()!.Texture);
        Assert.True(result.Scene.Find("Unknown")!.GetComponent<SpriteRenderer>()!.Texture.IsNone);
        Assert.Contains(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("gone.png", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Info);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Prefab_InstancesGetFreshIdsAndInternalReferencesFollow()
    {
        var source = new Scene();
        var root = source.CreateGameObject("Turret");
        var barrel = source.CreateGameObject("Barrel", root.Transform);
        barrel.Transform.LocalPosition = new Vector2(1f, 0f);
        var inspector = root.AddComponent<Inspector>();
        inspector.Target = barrel;                 // inside the prefab: must point at each copy's own barrel
        inspector.TargetTransform = root.Transform;
        var serializer = new SceneSerializer(Registry);
        var prefab = serializer.SerializePrefab(root);

        var scene = new Scene();
        var parent = scene.CreateGameObject("Parent");
        var first = serializer.InstantiatePrefab(prefab, scene, parent.Transform).Root;
        var second = serializer.InstantiatePrefab(prefab, scene).Root;

        Assert.NotEqual(root.Id, first.Id);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Same(parent.Transform, first.Transform.Parent);
        Assert.Null(second.Transform.Parent);

        var firstBarrel = first.Transform.Children.Single().GameObject;
        Assert.Same(firstBarrel, first.GetComponent<Inspector>()!.Target);
        Assert.Same(first.Transform, first.GetComponent<Inspector>()!.TargetTransform);
        Assert.Same(second.Transform.Children.Single().GameObject, second.GetComponent<Inspector>()!.Target);
        Assert.Equal(new Vector2(1f, 0f), firstBarrel.Transform.LocalPosition);
        Assert.Equal(5, scene.GameObjects.Count);
    }

    [Fact]
    public void Prefab_InRunningScene_AwakesAfterDataIsLoaded()
    {
        var source = new Scene();
        source.CreateGameObject("P").AddComponent<AwakeProbe>().Value = 42;
        var serializer = new SceneSerializer(Registry);
        var prefab = serializer.SerializePrefab(source.Find("P")!);

        var scene = new Scene { RethrowScriptExceptions = true };
        scene.Start();
        var instance = serializer.InstantiatePrefab(prefab, scene).Root;
        var probe = instance.GetComponent<AwakeProbe>()!;
        Assert.Equal(42, probe.ValueSeenInAwake);

        scene.Update(new FrameTime(0.1f, 0.1f, 0.1, 0.1, 1));
        Assert.True(probe.Started);
    }

    [Fact]
    public void Prefab_InactiveRootStaysInactive()
    {
        var source = new Scene();
        var root = source.CreateGameObject("Off");
        root.SetActive(false);
        var serializer = new SceneSerializer(Registry);
        var instance = serializer.InstantiatePrefab(serializer.SerializePrefab(root), new Scene()).Root;
        Assert.False(instance.ActiveSelf);
    }

    [Fact]
    public void Prefab_MustHaveExactlyOneRoot()
    {
        var json = $$"""{ "schemaVersion": 2, "gameObjects": [ { "id": "{{Guid.NewGuid()}}" }, { "id": "{{Guid.NewGuid()}}" } ] }""";
        var scene = new Scene();
        Assert.Throws<SerializationException>(() => new SceneSerializer(Registry).InstantiatePrefab(json, scene));
        Assert.Empty(scene.GameObjects);
    }

    [Fact]
    public void SceneInstantiate_RequiresProvider()
    {
        var scene = new Scene();
        Assert.Throws<InvalidOperationException>(() => scene.Instantiate(new AssetRef(Texture)));
    }

    [Fact]
    public void SerializedPrefab_RootHasNoParent()
    {
        var scene = new Scene();
        var parent = scene.CreateGameObject("Parent");
        var child = scene.CreateGameObject("Child", parent.Transform);
        var json = JObject.Parse(new SceneSerializer(Registry).SerializePrefab(child));
        Assert.Equal(JTokenType.Null, json["gameObjects"]![0]!["parent"]!.Type);
        Assert.Single((JArray)json["gameObjects"]!);
    }

    public sealed class AwakeProbe : Component
    {
        public int Value;

        [DontSerialize]
        public int ValueSeenInAwake;

        [DontSerialize]
        public bool Started;

        public override void Awake() => ValueSeenInAwake = Value;

        public override void Start() => Started = true;
    }

    private sealed class FakeLookup : IAssetLookup
    {
        public bool TryGetGuid(string relativePath, out Guid guid)
        {
            guid = relativePath == "Textures/player.png" ? Texture : Guid.Empty;
            return guid != Guid.Empty;
        }

        public bool TryGetPath(Guid guid, [NotNullWhen(true)] out string? relativePath)
        {
            relativePath = guid == Texture ? "Textures/player.png" : null;
            return relativePath is not null;
        }

        public string? GetAssetType(Guid guid) => guid == Texture ? AssetTypes.Texture : null;
    }
}
