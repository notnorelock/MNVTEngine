using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Assets;
using MNVTEngine.Core.Timing;
using MNVTEngine.Mathematics;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;
using MNVTEngine.Scripting.Missions;
using MNVTEngine.UI;
using MNVTEngine.UI.Widgets;

namespace MNVTEngine.Build.Tests;

/// <summary>
/// Plays samples/Platformer headlessly with simulated keys: the real scene, C# gameplay, physics, tilemap collision,
/// triggers and mission script, without a window. Proves the sample is playable, not only loadable.
/// </summary>
public sealed class PlatformerSampleTests
{
    private const float Step = 1f / 60f;
    private static readonly Lazy<Graphics.Text.Font> UIFont = new(Graphics.Text.Font.LoadDefault);
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
        Assert.DoesNotContain(loaded.Diagnostics, d => d.Severity >= Serialization.DiagnosticSeverity.Warning);
        var scene = loaded.Scene;
        var provider = new PrefabProvider(serializer, assets, NullLogger.Instance);
        var input = new InputState();
        (scene.TextAssets, scene.Prefabs, scene.Input, scene.RethrowScriptExceptions) = (provider, provider, input, true);
        UISystem.Attach(scene, new UIServices(UIFont.Value, null, asset => assets.ReadAllBytes(asset.Guid), null)); // as the runtime does
        scene.Start();
        return new Level(scene, input, scene.Find("Player")!, null!);
    }

    private void Frames(Level level, int count, params Key[] held) => Frames(level, count, null, held);

    /// <param name="mouse">Sets mouse input for the frame (after the input frame begins, so presses register).</param>
    private void Frames(Level level, int count, Action<InputState>? mouse, params Key[] held)
    {
        for (var i = 0; i < count; i++)
        {
            level.Input.BeginFrame();
            foreach (var key in Enum.GetValues<Key>().Where(k => k != Key.Unknown))
            {
                level.Input.SetKey(key, held.Contains(key));
            }

            mouse?.Invoke(level.Input);

            _frame++;
            var step = Step * level.Scene.TimeScale; // the host's clock applies Time.TimeScale
            if (step > 0f)
            {
                level.Scene.FixedUpdate(step);
            }

            level.Scene.Update(new FrameTime(step, Step, _frame * Step, _frame * Step, _frame));
            UISystem.For(level.Scene)!.Update(level.Input, new Vector2(1280, 720), 1f, Step);
        }
    }

    private static bool Grounded(GameObject player) =>
        (bool)player.Components.Single(c => c.GetType().Name == "Player").GetType().GetProperty("Grounded")!.GetValue(player.Components.Single(c => c.GetType().Name == "Player"))!;

    private static UIElement Ui(Level level, string objectName) => level.Scene.Find(objectName)!.GetComponent<UIDocument>()!.Root!;

    private static MissionRuntime MissionOf(Level level) =>
        level.Scene.FindObjectsOfType<MissionRunner>().Single().Missions!.Missions["level1"];

    [Fact]
    public async Task PlayerRunsJumpsOntoAPlatform_CollectsCoins_AndFinishesTheLevel()
    {
        var level = await LoadAsync();
        var player = level.Player;

        Frames(level, 30);
        var hud = Ui(level, "HUD");
        Assert.Equal("Coins 0 / 5", hud.Find<Label>("Coins")!.Text);
        Assert.Equal("Collect all coins", hud.Find<Label>("Objective")!.Text);
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
        Assert.Equal("Coins 1 / 5", hud.Find<Label>("Coins")!.Text);

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
        Assert.True(hud.Find("Complete")!.Visible, "the HUD shows the level-complete card");
        Assert.Contains("All 5 coins", hud.Find<Label>("CompleteTime")!.Text, StringComparison.Ordinal);
        Assert.Equal(0f, level.Scene.TimeScale);
    }

    [Fact]
    public async Task Escape_PausesTheGame_WithThePauseMenu()
    {
        var level = await LoadAsync();
        var pause = level.Scene.Find("Pause Menu")!.GetComponent<UIDocument>()!.Context!;
        Frames(level, 30);
        Assert.False(pause.Visible);

        Frames(level, 1, Key.Escape);
        Assert.True(pause.Visible);
        Assert.Equal(0f, level.Scene.TimeScale);
        var x = level.Player.Transform.Position.X;
        Frames(level, 30, Key.Right); // paused: running does nothing
        Assert.Equal(x, level.Player.Transform.Position.X);

        Frames(level, 1);
        Frames(level, 1, Key.Escape);
        Assert.False(pause.Visible);
        Assert.Equal(1f, level.Scene.TimeScale);
        Frames(level, 30, Key.Right);
        Assert.True(level.Player.Transform.Position.X > x + 1f);
    }

    [Fact]
    public async Task PauseMenuButtons_ResumeAndRestart()
    {
        var level = await LoadAsync();
        var loader = new RecordingLoader();
        level.Scene.Loader = loader;
        level.Scene.RethrowScriptExceptions = false; // as in the editor and the runtime
        var pause = level.Scene.Find("Pause Menu")!.GetComponent<UIDocument>()!;
        Frames(level, 30);

        Frames(level, 1, Key.Escape);
        Click(level, pause.Root!.Find("Resume")!);
        Assert.False(pause.Context!.Visible);
        Assert.Equal(1f, level.Scene.TimeScale);

        Frames(level, 1, Key.Escape);
        Click(level, pause.Root!.Find("Restart")!);
        Assert.Equal(["asset:9a000000-0000-4000-8000-000000000028"], loader.Requests); // Level1, through SceneManager
        Assert.Equal(0, level.Scene.ScriptExceptionCount);
    }

    private void Click(Level level, UIElement element)
    {
        Frames(level, 1, input => input.SetMousePosition(element.Bounds.Center));
        Frames(level, 1, input => input.SetMouseButton(MouseButton.Left, true));
        Frames(level, 1, input => input.SetMouseButton(MouseButton.Left, false));
    }

    private sealed class RecordingLoader : ISceneLoader
    {
        public List<string> Requests { get; } = [];

        public void RequestLoad(string path) => Requests.Add(path);

        public void RequestLoad(AssetRef scene) => Requests.Add(scene.ToString());
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
