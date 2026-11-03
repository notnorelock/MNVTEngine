using Microsoft.Extensions.Logging;
using MNVTEngine.Core.Logging;
using MNVTEngine.Core.Timing;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Scenes.Tests;

// Recorder.Log is static: keep these tests in one sequential collection.
[Collection(nameof(LifecycleTests))]
public sealed class LifecycleTests : IDisposable
{
    private static readonly FrameTime Frame = new(0.1f, 0.1f, 0.1, 0.1, 1);

    public LifecycleTests() => Recorder.Log.Clear();

    private static List<string> Log => Recorder.Log;

    public void Dispose() => Recorder.Log.Clear();

    private static Scene NewScene() => new() { RethrowScriptExceptions = true };

    [Fact]
    public void EditMode_RunsNoCallbacks()
    {
        var scene = NewScene();
        scene.CreateGameObject("A").AddComponent<Recorder>();
        scene.Find("A")!.Destroy();
        Assert.Empty(Log);
        Assert.Empty(scene.GameObjects);
    }

    [Fact]
    public void Start_AwakesAllBeforeEnablingAny_ThenStartBeforeFirstUpdate()
    {
        var scene = NewScene();
        scene.CreateGameObject("A").AddComponent<Recorder>().Label = "A";
        scene.CreateGameObject("B").AddComponent<OtherRecorder>().Label = "B";

        scene.Start();
        Assert.Equal(["A.Awake", "B.Awake", "A.OnEnable", "B.OnEnable"], Log);

        Log.Clear();
        scene.FixedUpdate(0.02f);
        scene.Update(Frame);
        Assert.Equal(["A.Start", "B.Start", "A.FixedUpdate", "B.FixedUpdate", "A.Update", "B.Update", "A.LateUpdate", "B.LateUpdate"], Log);

        Log.Clear();
        scene.Update(Frame);
        Assert.Equal(["A.Update", "B.Update", "A.LateUpdate", "B.LateUpdate"], Log);
    }

    [Fact]
    public void ComponentAddedWhileRunning_AwakesImmediately_StartsNextFrame()
    {
        var scene = NewScene();
        scene.Start();
        var go = scene.CreateGameObject();
        go.AddComponent<Recorder>();
        Assert.Equal(["R.Awake", "R.OnEnable"], Log);

        Log.Clear();
        scene.Update(Frame);
        Assert.Equal(["R.Start", "R.Update", "R.LateUpdate"], Log);
    }

    [Fact]
    public void Destroy_IsDeferredToEndOfFrame()
    {
        var scene = NewScene();
        var go = scene.CreateGameObject();
        go.AddComponent<Recorder>();
        scene.Start();
        scene.Update(Frame);
        Log.Clear();

        go.Destroy();
        Assert.False(go.IsDestroyed);
        Assert.Empty(Log);

        scene.Update(Frame);
        Assert.Equal(["R.Update", "R.LateUpdate", "R.OnDisable", "R.OnDestroy"], Log);
        Assert.True(go.IsDestroyed);
        Assert.Empty(scene.GameObjects);
        Assert.False(scene.World.IsAlive(go.Entity));
    }

    [Fact]
    public void DestroyParent_DestroysChildrenFirst()
    {
        var scene = NewScene();
        var parent = scene.CreateGameObject("P");
        parent.AddComponent<Recorder>().Label = "P";
        var child = scene.CreateGameObject("C", parent.Transform);
        child.AddComponent<Recorder>().Label = "C";
        scene.Start();
        Log.Clear();

        parent.Destroy();
        scene.Update(Frame);
        Assert.Equal(["C.OnDisable", "C.OnDestroy", "P.OnDisable", "P.OnDestroy"], Log.Where(l => l.Contains("On", StringComparison.Ordinal) && !l.Contains("OnEnable", StringComparison.Ordinal)));
        Assert.True(child.IsDestroyed);
    }

    [Fact]
    public void DisableComponent_StopsUpdatesAndTogglesCallbacks()
    {
        var scene = NewScene();
        var r = scene.CreateGameObject().AddComponent<Recorder>();
        scene.Start();
        scene.Update(Frame);
        Log.Clear();

        r.Enabled = false;
        scene.Update(Frame);
        r.Enabled = true;
        Assert.Equal(["R.OnDisable", "R.OnEnable"], Log);
    }

    [Fact]
    public void SetActive_PropagatesToChildren()
    {
        var scene = NewScene();
        var parent = scene.CreateGameObject("P");
        var child = scene.CreateGameObject("C", parent.Transform);
        child.AddComponent<Recorder>();
        scene.Start();
        Log.Clear();

        parent.SetActive(false);
        Assert.False(child.ActiveInHierarchy);
        Assert.True(child.ActiveSelf);
        scene.Update(Frame);
        parent.SetActive(true);
        Assert.Equal(["R.OnDisable", "R.OnEnable"], Log);
    }

    [Fact]
    public void InactiveAtStart_AwakesOnlyWhenActivated()
    {
        var scene = NewScene();
        var go = scene.CreateGameObject();
        go.AddComponent<Recorder>();
        go.SetActive(false);
        scene.Start();
        Assert.Empty(Log);

        go.SetActive(true);
        Assert.Equal(["R.Awake", "R.OnEnable"], Log);
    }

    [Fact]
    public void OneComponentPerType()
    {
        var scene = NewScene();
        var go = scene.CreateGameObject();
        go.AddComponent<Recorder>();
        Assert.Throws<InvalidOperationException>(() => go.AddComponent<Recorder>());
        go.AddComponent<OtherRecorder>(); // a subclass is a different type
        Assert.Equal(2, go.GetComponents<Recorder>().Count());
    }

    [Fact]
    public void TransformCannotBeDestroyedOrAdded()
    {
        var scene = NewScene();
        var go = scene.CreateGameObject();
        Assert.Throws<InvalidOperationException>(() => go.Transform.Destroy());
        Assert.Throws<ArgumentException>(() => go.AddComponent(typeof(Transform)));
    }

    [Fact]
    public void Components_AreRegisteredInTheEcsWorld()
    {
        var scene = NewScene();
        var go = scene.CreateGameObject();
        var r = go.AddComponent<Recorder>();
        Assert.Same(r, scene.World.Get<Recorder>(go.Entity));
        Assert.Same(go.Transform, scene.World.Get<Transform>(go.Entity));

        r.Destroy();
        Assert.False(scene.World.Has<Recorder>(go.Entity));
    }

    [Fact]
    public void ScriptExceptions_AreLoggedAndTheFrameContinues()
    {
        using var logs = new InMemoryLoggerProvider();
        var scene = new Scene { Logger = logs.CreateLogger("Scene") };
        var thrower = scene.CreateGameObject("Bad").AddComponent<Thrower>();
        scene.CreateGameObject("Good").AddComponent<Recorder>();
        scene.Start();
        scene.Update(Frame);
        scene.Update(Frame);

        Assert.Equal(2, thrower.Updates);
        Assert.Equal(2, Log.Count(l => l == "R.Update"));
        var entry = logs.Snapshot().First(e => e.Level == LogLevel.Error);
        Assert.Contains("Thrower.Update on 'Bad'", entry.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(entry.Exception);
    }

    [Fact]
    public void SpecExample_PlayerControllerMovesWithInputAndTime()
    {
        var input = new InputState();
        var scene = new Scene { Input = input, RethrowScriptExceptions = true };
        var player = scene.CreateGameObject("Player");
        player.AddComponent<PlayerController>().Speed = 10f;
        scene.Start();

        input.SetKey(Key.D, true);
        scene.Update(new FrameTime(0.5f, 0.5f, 0.5, 0.5, 1));
        Assert.Equal(new Vector2(5f, 0f), player.Transform.Position);
    }

    [Fact]
    public void Time_InFixedUpdateIsTheFixedStep()
    {
        var scene = NewScene();
        var probe = scene.CreateGameObject().AddComponent<TimeProbe>();
        scene.Start();
        scene.Update(new FrameTime(0.25f, 0.25f, 0.25, 0.25, 1));
        scene.FixedUpdate(0.02f);
        Assert.Equal(0.25f, probe.UpdateDelta);
        Assert.Equal(0.02f, probe.FixedDelta);
    }

    [Fact]
    public void Facades_OutsideCallbacksThrow() => Assert.Throws<InvalidOperationException>(() => Time.DeltaTime);

    [Fact]
    public void Unload_DestroysEverythingAndStops()
    {
        var scene = NewScene();
        scene.CreateGameObject().AddComponent<Recorder>();
        scene.Start();
        Log.Clear();
        scene.Unload();
        Assert.Equal(["R.OnDisable", "R.OnDestroy"], Log);
        Assert.False(scene.IsRunning);
        Assert.Equal(0, scene.World.EntityCount);
    }

    [Fact]
    public void SceneManager_RequiresLoader()
    {
        var scene = NewScene();
        var loader = new RecordingLoader();
        scene.Loader = loader;
        scene.CreateGameObject().AddComponent<Switcher>();
        scene.Start();
        scene.Update(Frame);
        Assert.Equal(["Scenes/Next.mnvt"], loader.Requests);
    }

    private sealed class TimeProbe : Component
    {
        public float UpdateDelta;
        public float FixedDelta;

        public override void Update() => UpdateDelta = Time.DeltaTime;

        public override void FixedUpdate() => FixedDelta = Time.DeltaTime;
    }

    private sealed class Switcher : Component
    {
        public override void Update() => SceneManager.LoadScene("Scenes/Next.mnvt");
    }

    private sealed class RecordingLoader : ISceneLoader
    {
        public List<string> Requests { get; } = [];

        public void RequestLoad(string path) => Requests.Add(path);
    }
}

[CollectionDefinition(nameof(LifecycleTests), DisableParallelization = true)]
public sealed class LifecycleSerialGroup;
