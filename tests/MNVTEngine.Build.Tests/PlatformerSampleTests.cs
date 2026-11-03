using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Assets;
using MNVTEngine.Core.Timing;
using MNVTEngine.Mathematics;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;
using MNVTEngine.Scripting.Missions;

namespace MNVTEngine.Build.Tests;

/// <summary>
/// Plays samples/Platformer headlessly with simulated keys: the real scene, C# gameplay, physics, tilemap collision,
/// triggers and mission script, without a window. Proves the sample is playable, not only loadable.
/// </summary>
public sealed class PlatformerSampleTests
{
    private const float Step = 1f / 60f;
    private long _frame;

    private sealed record Level(Scene Scene, InputState Input, GameObject Player, MissionRuntime Mission);

    private static async Task<Level> LoadAsync()
    {
        var project = GameProject.Load(Path.Combine(EngineLocation.FindRoot(), "samples", "Platformer"));
        var registry = EngineComponents.CreateRegistry();
        var code = await DevelopmentScripts.LoadAsync(project, registry, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(code.Succeeded, string.Join("\n", code.Diagnostics));

        var assets = AssetDatabase.OpenReadOnly(project.AssetsDirectory);
        var serializer = new SceneSerializer(registry, assets);
        var loaded = serializer.Deserialize(File.ReadAllText(project.ResolveAsset(project.StartupScene)), project.StartupScene);
        Assert.Empty(loaded.Diagnostics);
        var scene = loaded.Scene;
        var provider = new PrefabProvider(serializer, assets, NullLogger.Instance);
        var input = new InputState();
        (scene.TextAssets, scene.Prefabs, scene.Input, scene.RethrowScriptExceptions) = (provider, provider, input, true);
        scene.Start();
        return new Level(scene, input, scene.Find("Player")!, null!);
    }

    private void Frames(Level level, int count, params Key[] held)
    {
        for (var i = 0; i < count; i++)
        {
            level.Input.BeginFrame();
            foreach (var key in Enum.GetValues<Key>().Where(k => k != Key.Unknown))
            {
                level.Input.SetKey(key, held.Contains(key));
            }

            _frame++;
            level.Scene.FixedUpdate(Step);
            level.Scene.Update(new FrameTime(Step, Step, _frame * Step, _frame * Step, _frame));
        }
    }

    private static bool Grounded(GameObject player) =>
        (bool)player.Components.Single(c => c.GetType().Name == "Player").GetType().GetProperty("Grounded")!.GetValue(player.Components.Single(c => c.GetType().Name == "Player"))!;

    private static MissionRuntime MissionOf(Level level) =>
        level.Scene.FindObjectsOfType<MissionRunner>().Single().Missions!.Missions["level1"];

    [Fact]
    public async Task PlayerRunsJumpsOntoAPlatform_CollectsCoins_AndFinishesTheLevel()
    {
        var level = await LoadAsync();
        var player = level.Player;

        Frames(level, 30);
        Assert.True(Grounded(player), "the player lands on the tilemap floor");
        Assert.Equal(0.5f, player.Transform.Position.Y, 1);

        // Run right, jump at the platform's edge, keep running: land on the platform at y = 3 and grab the first coin.
        var guard = 0;
        while (player.Transform.Position.X < 4.6f && guard++ < 120)
        {
            Frames(level, 1, Key.Right);
        }

        Frames(level, 1, Key.Right, Key.Space);
        Frames(level, 40, Key.Right);
        Frames(level, 40);
        Assert.True(Grounded(player));
        Assert.Equal(3.5f, player.Transform.Position.Y, 1);
        Assert.Null(level.Scene.Find("Coin 1"));

        var mission = MissionOf(level);
        Assert.Equal(MissionState.Active, mission.State);
        Assert.False(level.Scene.Find("Flag")!.ActiveSelf);

        // The rest of the coins: teleport onto each (the trigger still has to fire through physics).
        foreach (var name in new[] { "Coin 2", "Coin 3", "Coin 4", "Coin 5" })
        {
            player.Transform.Position = level.Scene.Find(name)!.Transform.Position;
            Frames(level, 3);
            Assert.Null(level.Scene.Find(name));
        }

        Frames(level, 2);
        Assert.Equal(ObjectiveState.Completed, mission.Objectives["coins"].State);
        Assert.True(level.Scene.Find("Flag")!.ActiveSelf, "the mission shows the flag after the last coin");

        player.Transform.Position = level.Scene.Find("Flag")!.Transform.Position;
        Frames(level, 3);
        Assert.Equal(MissionState.Completed, mission.State);
    }

    [Fact]
    public async Task FallingIntoAGap_RespawnsAtTheStart()
    {
        var level = await LoadAsync();
        var player = level.Player;
        Frames(level, 30);
        var start = player.Transform.Position;

        player.Transform.Position = new Vector2(15f, 2f); // above the gap at x = 14..16
        Frames(level, 120);

        Assert.Equal(start.X, player.Transform.Position.X, 0);
        Assert.True(Grounded(player));
    }
}
