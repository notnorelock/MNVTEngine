using MNVTEngine.Core.Timing;
using MNVTEngine.Mathematics;
using MNVTEngine.Scripting.Missions;
using MNVTEngine.Serialization;

namespace MNVTEngine.Runtime.Tests;

public class MissionRunnerTests
{
    private sealed class FakeTextAssets(string text) : ITextAssetReader
    {
        public string ReadText(AssetRef asset, string assetType) => assetType == AssetTypes.Script ? text : throw new InvalidOperationException("wrong type");
    }

    private const string Reach = """
        mission "reach" {
            on start { objective.activate("go") }
            objective "go" {
                marker.show("Marker")
                wait until player.distance_to("Door") < 1
                marker.hide("Marker")
                events.emit("door_reached", 42)
                mission.complete()
            }
        }
        """;

    [Fact]
    public void RunsAMissionAgainstSceneObjects()
    {
        var scene = new Scene { TextAssets = new FakeTextAssets(Reach), RethrowScriptExceptions = true };
        var player = scene.CreateGameObject("Player");
        scene.CreateGameObject("Door").Transform.Position = new Vector2(5, 0);
        var marker = scene.CreateGameObject("Marker");
        marker.SetActive(false);
        var runner = scene.CreateGameObject("Logic").AddComponent<MissionRunner>();
        runner.Script = new AssetRef(Guid.NewGuid());
        runner.StartMission = "reach";
        var emitted = new List<(string, object?)>();
        runner.EventEmitted += (name, payload) => emitted.Add((name, payload));

        scene.Start();
        Frame(scene, 3);
        Assert.True(marker.ActiveSelf);
        Assert.Equal(MissionState.Active, runner.Missions!.Missions["reach"].State);

        player.Transform.Position = new Vector2(4.5f, 0);
        Frame(scene, 2);
        Assert.False(marker.ActiveSelf);
        Assert.Equal(MissionState.Completed, runner.Missions.Missions["reach"].State);
        Assert.Equal([("door_reached", (object?)42.0)], emitted);
    }

    [Fact]
    public void ScriptWithErrors_DisablesTheRunner()
    {
        var scene = new Scene { TextAssets = new FakeTextAssets("on start { undefined_thing() }") };
        var runner = scene.CreateGameObject("Logic").AddComponent<MissionRunner>();
        runner.Script = new AssetRef(Guid.NewGuid());
        scene.Start();
        Frame(scene, 1);
        Assert.False(runner.Enabled);
        Assert.Null(runner.Vm);
    }

    [Fact]
    public void SettingsSurviveSceneSerialization()
    {
        var scene = new Scene();
        var runner = scene.CreateGameObject("Logic").AddComponent<MissionRunner>();
        runner.Script = new AssetRef(Guid.NewGuid());
        runner.StartMission = "reach";
        runner.PlayerObject = "Hero";
        var serializer = new Scenes.SceneSerializer(ScriptingComponents.Register(Scenes.ComponentRegistry.CreateDefault()));

        var loaded = serializer.Deserialize(serializer.Serialize(scene));
        Assert.Empty(loaded.Diagnostics);
        var copy = loaded.Scene.Find("Logic")!.GetComponent<MissionRunner>()!;
        Assert.Equal((runner.Script, "reach", "Hero"), (copy.Script, copy.StartMission, copy.PlayerObject));
    }

    private static void Frame(Scene scene, int count)
    {
        for (var i = 1; i <= count; i++)
        {
            scene.Update(new FrameTime(0.1f, 0.1f, 0.1 * i, 0.1 * i, i));
        }
    }
}
