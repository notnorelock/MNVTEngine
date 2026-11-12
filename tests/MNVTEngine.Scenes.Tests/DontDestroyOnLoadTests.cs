using MNVTEngine.Core.Timing;

namespace MNVTEngine.Scenes.Tests;

// Recorder.Log is static: run with the other lifecycle tests, one at a time.
[Collection(nameof(LifecycleTests))]
public sealed class DontDestroyOnLoadTests : IDisposable
{
    private static readonly FrameTime Frame = new(0.1f, 0.1f, 0.1, 0.1, 1);

    private struct Health
    {
        public int Value;
    }

    public DontDestroyOnLoadTests() => Recorder.Log.Clear();

    public void Dispose() => Recorder.Log.Clear();

    private static Scene NewScene(string name) => new(name) { RethrowScriptExceptions = true };

    /// <summary>What hosts do on SceneManager.LoadScene: carry persistent objects over, unload, start the new scene.</summary>
    private static void Load(Scene from, Scene to)
    {
        from.MovePersistentObjectsTo(to);
        from.Unload();
        to.Start();
    }

    [Fact]
    public void PersistentRoot_MovesWithItsChildren_KeepingStateAndLifecycle()
    {
        var first = NewScene("First");
        var music = first.CreateGameObject("Music");
        var recorder = music.AddComponent<Recorder>();
        recorder.Label = "Music";
        var child = first.CreateGameObject("Track", music.Transform);
        first.World.Add(child.Entity, new Health { Value = 7 });
        first.CreateGameObject("Level").AddComponent<Recorder>().Label = "Level";
        first.Start();
        first.Update(Frame);
        Component.DontDestroyOnLoad(recorder); // inside a component: DontDestroyOnLoad(this) or DontDestroyOnLoad(GameObject)
        Recorder.Log.Clear();

        var second = NewScene("Second");
        second.CreateGameObject("Enemy").AddComponent<Recorder>().Label = "Enemy";
        Load(first, second);

        // The level's objects were destroyed; the persistent ones were not, and did not wake up again.
        Assert.Equal(["Level.OnDisable", "Level.OnDestroy", "Enemy.Awake", "Enemy.OnEnable"], Recorder.Log);
        Assert.Same(second, music.Scene);
        Assert.Same(second, child.Scene);
        Assert.Same(music, second.FindById(music.Id));
        Assert.Equal(["Enemy", "Music"], second.RootObjects.Select(r => r.Name).Order());
        Assert.Contains(child, second.GameObjects);
        Assert.Empty(first.GameObjects);
        Assert.Equal(7, second.World.Get<Health>(child.Entity).Value); // struct components moved too
        Assert.Same(recorder, second.World.Get<Recorder>(music.Entity));

        Recorder.Log.Clear();
        second.Update(Frame);
        Assert.Contains("Music.Update", Recorder.Log); // it runs in the new scene
        Assert.True(music.PersistsAcrossLoads); // and keeps persisting through the next load
        var third = NewScene("Third");
        Load(second, third);
        Assert.Same(third, music.Scene);
    }

    [Fact]
    public void OnlyRoots_Persist()
    {
        var scene = NewScene("A");
        var parent = scene.CreateGameObject("Parent");
        var child = scene.CreateGameObject("Child", parent.Transform);
        child.DontDestroyOnLoad();
        Assert.False(child.PersistsAcrossLoads);

        var next = NewScene("B");
        Load(scene, next);
        Assert.Empty(next.GameObjects);
        Assert.True(child.IsDestroyed);
    }

    [Fact]
    public void LoadingTheSameSceneAgain_GivesTheCarriedObjectANewId()
    {
        var first = NewScene("Menu");
        var manager = first.CreateGameObject("Manager");
        var id = manager.Id;
        var serializer = new SceneSerializer(TestRegistry.Create());
        var again = serializer.Deserialize(serializer.Serialize(first)).Scene; // the scene file contains it too
        var copy = again.FindById(id)!;
        manager.DontDestroyOnLoad();
        first.Start();
        Load(first, again);

        Assert.NotEqual(id, manager.Id);
        Assert.Equal(manager.Id, manager.Transform.Id);
        Assert.Same(copy, again.FindById(id));
        Assert.Same(manager, again.FindById(manager.Id));
    }

    [Fact]
    public void PendingStartAndQueuedDestroys_CarryOver()
    {
        var first = NewScene("A");
        first.Start();
        var fresh = first.CreateGameObject("Fresh");
        fresh.AddComponent<Recorder>().Label = "Fresh"; // awake now, Start still pending
        fresh.DontDestroyOnLoad();
        var doomed = first.CreateGameObject("Doomed");
        doomed.AddComponent<Recorder>().Label = "Doomed";
        doomed.DontDestroyOnLoad();
        doomed.Destroy(); // queued for the end of the frame
        Recorder.Log.Clear();

        var second = NewScene("B");
        Load(first, second);
        second.Update(Frame);

        Assert.Equal(["Fresh.Start", "Fresh.Update", "Fresh.LateUpdate"], Recorder.Log.Where(e => e.StartsWith("Fresh.", StringComparison.Ordinal)));
        Assert.Contains("Doomed.OnDestroy", Recorder.Log); // the destroy queued in the old scene happened in the new one
        Assert.True(doomed.IsDestroyed);
        Assert.DoesNotContain(doomed, second.GameObjects);
    }
}
