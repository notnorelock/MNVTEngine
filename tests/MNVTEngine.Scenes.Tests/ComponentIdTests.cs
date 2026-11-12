namespace MNVTEngine.Scenes.Tests;

/// <summary>No [ComponentId]: saved under its full type name.</summary>
public sealed class PlainMover : Component
{
    public float Speed = 2f;
}

/// <summary>Was "Game.OldMover" before a rename.</summary>
[ComponentAlias("Game.OldMover")]
[ComponentAlias("Game.Ancient.Mover")]
public sealed class RenamedMover : Component
{
    public float Speed = 1f;
}

public class ComponentIdTests
{
    private static readonly SceneSerializer Serializer = new(TestRegistry.Create().Register<PlainMover>().Register<RenamedMover>());

    [Fact]
    public void WithoutAnAttribute_TheIdIsTheFullTypeName()
    {
        Assert.Equal("MNVTEngine.Scenes.Tests.PlainMover", ComponentRegistry.IdOf(typeof(PlainMover)));
        var scene = new Scene();
        scene.CreateGameObject("A").AddComponent<PlainMover>().Speed = 7f;

        var json = Serializer.Serialize(scene);
        Assert.Contains("\"MNVTEngine.Scenes.Tests.PlainMover\"", json, StringComparison.Ordinal);
        Assert.Equal(7f, Serializer.Deserialize(json).Scene.Find("A")!.GetComponent<PlainMover>()!.Speed);
    }

    [Theory]
    [InlineData("Game.OldMover")]
    [InlineData("Game.Ancient.Mover")]
    public void ScenesSavedUnderAFormerName_LoadIntoTheRenamedComponent_AndSaveUnderTheNewName(string formerId)
    {
        var json = $$"""
            { "schemaVersion": 2, "name": "Old", "gameObjects": [ { "id": "{{Guid.NewGuid()}}", "name": "A",
              "components": [ { "type": "{{formerId}}", "id": "{{Guid.NewGuid()}}", "data": { "speed": 4.0 } } ] } ] }
            """;

        var loaded = Serializer.Deserialize(json);
        Assert.DoesNotContain(loaded.Diagnostics, d => d.Severity >= Serialization.DiagnosticSeverity.Warning); // only the schema 2 -> 3 migration note
        Assert.Equal(4f, loaded.Scene.Find("A")!.GetComponent<RenamedMover>()!.Speed);
        var resaved = Serializer.Serialize(loaded.Scene);
        Assert.Contains("\"MNVTEngine.Scenes.Tests.RenamedMover\"", resaved, StringComparison.Ordinal);
        Assert.DoesNotContain(formerId, resaved, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAliasCannotShadowAnotherComponent()
    {
        var registry = new ComponentRegistry().Register<PlainMover>();
        Assert.Throws<InvalidOperationException>(() => registry.Register<ShadowingMover>());
    }

    [ComponentAlias("MNVTEngine.Scenes.Tests.PlainMover")]
    private sealed class ShadowingMover : Component;
}
