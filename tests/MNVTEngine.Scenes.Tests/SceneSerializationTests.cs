using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Scenes.Tests;

public class SceneSerializationTests
{
    private static readonly ComponentRegistry Registry = ComponentRegistry.CreateDefault()
        .Register<PlayerController>()
        .Register<Inspector>();

    private static readonly SceneSerializer Serializer = new(Registry);

    private static readonly Guid PlayerTexture = Guid.Parse("0f5e0000-0000-4000-8000-000000000001");

    private static Scene BuildScene()
    {
        var scene = new Scene("Level");
        var player = scene.CreateGameObject("Player");
        player.Transform.LocalPosition = new Vector2(1.5f, -2f);
        player.Transform.LocalRotation = 30f;
        player.Transform.LocalScale = new Vector2(2f, 3f);
        player.AddComponent<PlayerController>().Speed = 12.5f;
        var sprite = player.AddComponent<SpriteRenderer>();
        sprite.Texture = new AssetRef(PlayerTexture);
        sprite.Color = new Color(1f, 0.5f, 0.25f, 1f);
        sprite.SortingOrder = 3;

        var gun = scene.CreateGameObject("Gun", player.Transform);
        gun.Transform.LocalPosition = new Vector2(0.5f, 0f);
        gun.SetActive(false);
        scene.CreateGameObject("Barrel", gun.Transform);
        scene.CreateGameObject("Muzzle", gun.Transform);

        var camera = scene.CreateGameObject("Camera");
        camera.AddComponent<Camera>().OrthographicSize = 8f;
        var inspector = camera.AddComponent<Inspector>();
        inspector.Target = player;
        inspector.TargetTransform = gun.Transform;
        inspector.Controller = player.GetComponent<PlayerController>();
        inspector.Group = [player, gun];
        inspector.Secret = 42;
        inspector.Transient = 99;
        inspector.Tint = Color.Cyan;
        inspector.Offset = new Vector2(0f, 3f);
        inspector.Kind = Mode.Running;
        inspector.Enabled = false;
        return scene;
    }

    [Fact]
    public void RoundTrip_PreservesHierarchyValuesAndReferences()
    {
        var original = BuildScene();
        var result = Serializer.Deserialize(Serializer.Serialize(original), "Level.mnvt");
        Assert.Empty(result.Diagnostics);

        var scene = result.Scene;
        Assert.Equal("Level", scene.Name);
        Assert.Equal(original.GameObjects.Select(g => g.Id), scene.GameObjects.Select(g => g.Id));

        var player = scene.Find("Player")!;
        Assert.Equal(new Vector2(1.5f, -2f), player.Transform.LocalPosition);
        Assert.Equal(30f, player.Transform.LocalRotation);
        Assert.Equal(new Vector2(2f, 3f), player.Transform.LocalScale);
        Assert.Equal(12.5f, player.GetComponent<PlayerController>()!.Speed);
        var sprite = player.GetComponent<SpriteRenderer>()!;
        Assert.Equal(new AssetRef(PlayerTexture), sprite.Texture);
        Assert.Equal(new Color(1f, 0.5f, 0.25f, 1f), sprite.Color);
        Assert.Equal(3, sprite.SortingOrder);

        var gun = scene.Find("Gun")!;
        Assert.Same(player.Transform, gun.Transform.Parent);
        Assert.False(gun.ActiveSelf);
        Assert.Equal(["Barrel", "Muzzle"], gun.Transform.Children.Select(c => c.GameObject.Name));

        var inspector = scene.Find("Camera")!.GetComponent<Inspector>()!;
        Assert.False(inspector.Enabled);
        Assert.Same(player, inspector.Target);
        Assert.Same(gun.Transform, inspector.TargetTransform);
        Assert.Same(player.GetComponent<PlayerController>(), inspector.Controller);
        Assert.Equal([player, gun], inspector.Group);
        Assert.Equal(42, inspector.Secret);
        Assert.Equal(3, inspector.Transient);
        Assert.Equal(Color.Cyan, inspector.Tint);
        Assert.Equal(Mode.Running, inspector.Kind);
        Assert.Equal(8f, scene.Find("Camera")!.GetComponent<Camera>()!.OrthographicSize);
    }

    [Fact]
    public void Serialization_IsStable()
    {
        var json = Serializer.Serialize(BuildScene());
        var again = Serializer.Serialize(Serializer.Deserialize(json).Scene);
        Assert.Equal(json, again);
    }

    [Fact]
    public void Format_IsReadableAndUsesStableIds()
    {
        var json = JObject.Parse(Serializer.Serialize(BuildScene()));
        Assert.Equal(2, json.Value<int>("schemaVersion"));
        var player = (JObject)json["gameObjects"]![0]!;
        Assert.Equal("Player", player.Value<string>("name"));
        var controller = (JObject)player["components"]![0]!;
        Assert.Equal("MNVTEngine.Scenes.Tests.PlayerController", controller.Value<string>("type"));
        Assert.Equal(12.5f, controller["data"]!.Value<float>("speed"));
        var inspector = (JObject)json["gameObjects"]![4]!["components"]![1]!["data"]!;
        Assert.Equal(player.Value<string>("id"), inspector.Value<string>("target"));
        Assert.Equal(42, inspector.Value<int>("secret"));
        Assert.Null(inspector["transient"]);
        Assert.Equal("running", inspector.Value<string>("kind"));
    }

    [Fact]
    public void UnknownComponent_IsPreservedAcrossSave()
    {
        var json = Serializer.Serialize(BuildScene());
        var limited = new SceneSerializer(ComponentRegistry.CreateDefault().Register<Inspector>());

        var result = limited.Deserialize(json);
        var missing = Assert.Single(result.Scene.Find("Player")!.GetComponents<MissingComponent>());
        Assert.Equal("MNVTEngine.Scenes.Tests.PlayerController", missing.TypeId);
        Assert.Contains(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("PlayerController", StringComparison.Ordinal));
        Assert.False(result.HasErrors);

        // The reference to the missing component's type cannot resolve, but the data survives a save with the full registry.
        var resaved = limited.Serialize(result.Scene);
        var restored = Serializer.Deserialize(resaved).Scene;
        Assert.Equal(12.5f, restored.Find("Player")!.GetComponent<PlayerController>()!.Speed);
    }

    [Fact]
    public void UnknownDataMember_WarnsAndKeepsLoading()
    {
        var json = JObject.Parse(Serializer.Serialize(BuildScene()));
        json["gameObjects"]![0]!["components"]![0]!["data"]!["oldField"] = 5;
        var result = Serializer.Deserialize(json.ToString());
        Assert.Contains(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("oldField", StringComparison.Ordinal));
        Assert.Equal(12.5f, result.Scene.Find("Player")!.GetComponent<PlayerController>()!.Speed);
    }

    [Fact]
    public void BadComponentData_IsAnErrorButOtherObjectsLoad()
    {
        var json = JObject.Parse(Serializer.Serialize(BuildScene()));
        json["gameObjects"]![0]!["components"]![0]!["data"]!["speed"] = "fast";
        var result = Serializer.Deserialize(json.ToString());
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Path.Contains("speed", StringComparison.Ordinal) || d.Message.Contains("speed", StringComparison.Ordinal));
        Assert.NotNull(result.Scene.Find("Camera")!.GetComponent<Camera>());
    }

    [Fact]
    public void DanglingReference_BecomesNullWithWarning()
    {
        var json = JObject.Parse(Serializer.Serialize(BuildScene()));
        json["gameObjects"]![4]!["components"]![1]!["data"]!["target"] = Guid.NewGuid().ToString();
        var result = Serializer.Deserialize(json.ToString());
        Assert.Null(result.Scene.Find("Camera")!.GetComponent<Inspector>()!.Target);
        Assert.Contains(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("not found", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateIds_AreReassigned()
    {
        var json = JObject.Parse(Serializer.Serialize(BuildScene()));
        json["gameObjects"]![1]!["id"] = json["gameObjects"]![0]!["id"];
        var result = Serializer.Deserialize(json.ToString());
        Assert.True(result.HasErrors);
        Assert.Equal(5, result.Scene.GameObjects.Select(g => g.Id).Distinct().Count());
    }

    [Fact]
    public void ParentCycle_IsReportedNotFatal()
    {
        const string json = """
            { "schemaVersion": 2, "name": "Cycle", "gameObjects": [
              { "id": "11111111-1111-1111-1111-111111111111", "name": "A", "parent": "22222222-2222-2222-2222-222222222222" },
              { "id": "22222222-2222-2222-2222-222222222222", "name": "B", "parent": "11111111-1111-1111-1111-111111111111" } ] }
            """;
        var result = Serializer.Deserialize(json);
        Assert.True(result.HasErrors);
        Assert.Equal(2, result.Scene.GameObjects.Count);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "name": "x", "gameObjects": [] }""")]
    [InlineData("""{ "schemaVersion": 99, "gameObjects": [] }""")]
    [InlineData("""{ "schemaVersion": 1 }""")]
    [InlineData("""{ "schemaVersion": 2, "gameObjects": [], "gameObjects": [] }""")]
    public void UnreadableDocuments_Throw(string json) =>
        Assert.Throws<SerializationException>(() => Serializer.Deserialize(json, "bad.mnvt"));

    [Fact]
    public void TypeNamesInData_AreNeverInstantiated()
    {
        const string json = """
            { "schemaVersion": 2, "gameObjects": [ { "id": "11111111-1111-1111-1111-111111111111", "name": "A",
              "components": [ { "type": "System.IO.FileInfo, System.IO.FileSystem", "data": { "fileName": "x" } } ] } ] }
            """;
        var result = Serializer.Deserialize(json);
        var missing = Assert.Single(result.Scene.Find("A")!.GetComponents<MissingComponent>());
        Assert.Equal("System.IO.FileInfo, System.IO.FileSystem", missing.TypeId);
    }

    [Fact]
    public void Migrations_UpgradeOldDocuments()
    {
        var serializer = new SceneSerializer(Registry);
        var v0 = """{ "schemaVersion": 0, "title": "Old", "gameObjects": [] }""";
        Assert.Throws<SerializationException>(() => serializer.Deserialize(v0));

        serializer.Migrations.Add(0, doc =>
        {
            doc["name"] = doc["title"];
            doc.Remove("title");
        });
        var result = serializer.Deserialize(v0);
        Assert.Equal("Old", result.Scene.Name);
        Assert.Contains(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Info);
    }

    [Fact]
    public void Registry_RejectsConflictsAndNonComponents()
    {
        var registry = new ComponentRegistry();
        Assert.Throws<ArgumentException>(() => registry.Register(typeof(string)));
        Assert.Throws<ArgumentException>(() => registry.Register(typeof(MissingComponent)));
        registry.Register<Camera>().Register<Camera>();
        Assert.Throws<InvalidOperationException>(() => registry.Register(typeof(FakeCamera)));
        registry.RegisterAssembly(typeof(SceneSerializationTests).Assembly);
        Assert.True(registry.TryGetType("MNVTEngine.Scenes.Tests.PlayerController", out _));
    }

    [ComponentId("MNVTEngine.Camera")]
    private sealed class FakeCamera : Component;
}
