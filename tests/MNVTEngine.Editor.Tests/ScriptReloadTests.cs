using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Editor.Services;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;

namespace MNVTEngine.Editor.Tests;

/// <summary>End-to-end hot reload with real builds (slow: a few seconds per build).</summary>
public sealed class ScriptReloadTests : IDisposable
{
    private static readonly string[] EngineAssemblies = ["Core", "Mathematics", "ECS", "Input", "Serialization", "Scenes"];

    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-reload-").FullName;
    private DateTime _clock = DateTime.UtcNow;

    public ScriptReloadTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "Scenes"));
        Directory.CreateDirectory(Path.Combine(_root, "Scripts"));
        File.WriteAllText(Path.Combine(_root, "Game.mnvtproj"), """{ "schemaVersion": 1, "name": "T", "startupScene": "Scenes/Main.mnvt", "scripts": { "project": "Scripts/Game.csproj" } }""");
        File.WriteAllText(Path.Combine(_root, "Assets", "Scenes", "Main.mnvt"), $$"""
            { "schemaVersion": 2, "name": "Main", "gameObjects": [
              { "id": "11111111-1111-1111-1111-111111111111", "name": "Player",
                "components": [ { "type": "Game.Health", "id": "22222222-2222-2222-2222-222222222222", "data": { "points": 42 } } ] } ] }
            """);
        var references = string.Join("\n", EngineAssemblies.Select(n => $"""<Reference Include="{Path.Combine(AppContext.BaseDirectory, $"MNVTEngine.{n}.dll")}" Private="false" />"""));
        File.WriteAllText(Path.Combine(_root, "Scripts", "Game.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>
              <ItemGroup>{references}</ItemGroup>
            </Project>
            """);
        WriteSource("public int Points = 10;");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Build servers may briefly hold files in obj/.
        }
    }

    private void WriteSource(string members)
    {
        var path = Path.Combine(_root, "Scripts", "Health.cs");
        File.WriteAllText(path, $$"""
            namespace Game;
            [MNVTEngine.ComponentId("Game.Health")]
            public sealed class Health : MNVTEngine.Component
            {
                {{members}}
            }
            """);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(Random.Shared.Next(10, 10_000)));
    }

    private async Task WaitFor(ScriptReloadController scripts, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out; state {scripts.State}: {string.Join("\n", scripts.Diagnostics)}");
            await Task.Delay(50, TestContext.Current.CancellationToken);
            _clock = _clock.AddSeconds(5); // skip poll and verification delays
            scripts.Update(_clock);
        }
    }

    [Fact]
    public async Task EditCompileReload_PreservesSceneData_AndBlocksPlayOnErrors()
    {
        var session = new EditorSession(GameProject.Load(_root), ComponentRegistry.CreateDefault());
        session.OpenScene("Scenes/Main.mnvt");
        var player = session.Scene.Find("Player")!;
        Assert.IsType<MissingComponent>(player.Components[1]); // scripts not compiled yet
        session.Selection.Select(player);

        var play = new PlayModeController(session, NullLoggerFactory.Instance);
        using var scripts = new ScriptReloadController(session, play, NullLogger.Instance);
        var reloads = 0;
        scripts.Reloaded += () => reloads++;
        Assert.False(scripts.CanEnterPlayMode);

        // 1. Initial compile turns the missing component into the real one, data intact.
        await WaitFor(scripts, () => reloads == 1);
        Assert.True(scripts.CanEnterPlayMode);
        // Checks on old-generation objects stay in their own scope: locals of an async test method live in its state
        // machine (and Debug builds keep spilled temporaries), which would keep the old code from unloading.
        {
            var first = session.Scene.Find("Player")!.Components[1];
            Assert.Equal("Health", first.GetType().Name);
            Assert.Equal(42, first.GetType().GetField("Points")!.GetValue(first));
        }

        Assert.False(session.IsDirty);
        Assert.Equal(player.Id, session.Selection.Resolve(session.Scene)!.Id);

        // 2. Edit: new field appears, existing data survives, the old generation is unloaded.
        WriteSource("public int Points = 10; public float Regen = 1.5f;");
        await WaitFor(scripts, () => reloads == 2);
        {
            var second = session.Scene.Find("Player")!.Components[1];
            Assert.Equal(42, second.GetType().GetField("Points")!.GetValue(second));
            Assert.Equal(1.5f, second.GetType().GetField("Regen")!.GetValue(second));
        }

        await WaitFor(scripts, () => scripts.LastUnloadCollected is not null);
        Assert.True(scripts.LastUnloadCollected);

        // 3. Compile error: last good code stays, Play Mode is blocked, errors point at the file.
        WriteSource("public int Points = \"oops\";");
        await WaitFor(scripts, () => scripts.State == ScriptState.Failed);
        Assert.False(scripts.CanEnterPlayMode);
        Assert.Contains(scripts.Diagnostics, d => d.Code == "CS0029" && d.File!.EndsWith("Health.cs", StringComparison.Ordinal));
        Assert.Equal("Health", session.Scene.Find("Player")!.Components[1].GetType().Name);

        // 4. Fix it: reload resumes.
        WriteSource("public int Points = 10;");
        await WaitFor(scripts, () => reloads == 3);
        Assert.True(scripts.CanEnterPlayMode);
    }

    [Fact]
    public async Task ReloadDuringPlay_ContinuesTheRunningGame()
    {
        var session = new EditorSession(GameProject.Load(_root), ComponentRegistry.CreateDefault());
        session.OpenScene("Scenes/Main.mnvt");
        var play = new PlayModeController(session, NullLoggerFactory.Instance);
        using var scripts = new ScriptReloadController(session, play, NullLogger.Instance);
        var reloads = 0;
        scripts.Reloaded += () => reloads++;
        await WaitFor(scripts, () => reloads == 1);

        play.Play();
        {
            var running = play.PlayScene!.Find("Player")!.Components[1];
            running.GetType().GetField("Points")!.SetValue(running, 7); // game state reached while playing
        }

        play.Tick(0.5);
        var clock = play.Time.TotalTime;
        WriteSource("public int Points = 10; public bool Changed = true;");
        await WaitFor(scripts, () => reloads == 2);

        Assert.True(play.IsPlaying);
        Assert.Equal(clock, play.Time.TotalTime);
        {
            var reloaded = play.PlayScene!.Find("Player")!.Components[1];
            Assert.Equal(7, reloaded.GetType().GetField("Points")!.GetValue(reloaded));  // state kept
            Assert.Equal(true, reloaded.GetType().GetField("Changed")!.GetValue(reloaded)); // new code running
            var edit = session.Scene.Find("Player")!.Components[1];
            Assert.Equal(42, edit.GetType().GetField("Points")!.GetValue(edit));          // edit scene untouched
        }

        play.Stop();
        await WaitFor(scripts, () => scripts.LastUnloadCollected is not null);
        Assert.True(scripts.LastUnloadCollected);
    }
}
