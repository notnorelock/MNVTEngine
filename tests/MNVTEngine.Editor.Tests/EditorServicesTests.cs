using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Editor.Services;
using MNVTEngine.Mathematics;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;

namespace MNVTEngine.Editor.Tests;

public sealed class EditorServicesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-editor-").FullName;
    private readonly EditorSession _session;

    public EditorServicesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "Scenes"));
        File.WriteAllText(Path.Combine(_root, "Assets", "Scenes", "Main.mnvt"), """{ "schemaVersion": 1, "name": "Main", "gameObjects": [] }""");
        File.WriteAllText(Path.Combine(_root, "Game.mnvtproj"), """{ "schemaVersion": 1, "name": "Game", "startupScene": "Scenes/Main.mnvt" }""");
        _session = new EditorSession(GameProject.Load(_root), ComponentRegistry.CreateDefault().Register<Mover>().Register<Spawner>().Register<Holder>());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private PlayModeController NewPlayMode() => new(_session, NullLoggerFactory.Instance);

    [Fact]
    public void NewSession_HasCameraAndIsClean()
    {
        Assert.NotNull(_session.Scene.MainCamera);
        Assert.False(_session.IsDirty);
        Assert.Null(_session.ScenePath);
        Assert.Throws<InvalidOperationException>(() => _session.SaveScene());
    }

    [Fact]
    public void Save_ThenOpen_RoundTripsAndClearsDirty()
    {
        var player = _session.Scene.CreateGameObject("Player");
        player.Transform.Position = new Vector2(3f, 4f);
        _session.MarkDirty();
        Assert.EndsWith("*", _session.DisplayName, StringComparison.Ordinal);

        _session.SaveScene("Scenes/Saved.mnvt");
        Assert.False(_session.IsDirty);
        Assert.False(File.Exists(Path.Combine(_root, "Assets", "Scenes", "Saved.mnvt.tmp")));

        var replaced = 0;
        _session.SceneReplaced += () => replaced++;
        _session.OpenScene("Scenes/Saved.mnvt");
        Assert.Equal(1, replaced);
        Assert.Equal(new Vector2(3f, 4f), _session.Scene.Find("Player")!.Transform.Position);
        Assert.Equal("Scenes/Saved.mnvt", _session.ScenePath);
    }

    [Fact]
    public void Save_RejectsPathsOutsideAssets() =>
        Assert.Throws<ArgumentException>(() => _session.SaveScene("../evil.mnvt"));

    [Fact]
    public void Open_UnreadableFileThrowsAndKeepsCurrentScene()
    {
        var before = _session.Scene;
        Assert.Throws<Serialization.SerializationException>(() => _session.OpenScene("Scenes/Nope.mnvt"));
        Assert.Same(before, _session.Scene);
    }

    [Fact]
    public void PlayMode_RunsACopy_AndStopRestoresTheEditScene()
    {
        var player = _session.Scene.CreateGameObject("Player");
        player.AddComponent<Mover>();
        _session.Selection.Select(player);
        var play = NewPlayMode();

        play.Play();
        Assert.Equal(PlayState.Playing, play.State);
        var copy = play.PlayScene!;
        Assert.NotSame(_session.Scene, copy);
        Assert.True(copy.IsRunning);
        Assert.False(_session.Scene.IsRunning);

        // Same ids: selection resolves in both scenes, to different objects.
        var playPlayer = _session.Selection.Resolve(copy)!;
        Assert.NotSame(player, playPlayer);
        Assert.Equal(player.Id, playPlayer.Id);

        play.Tick(0.5);
        play.Tick(0.5);
        Assert.True(playPlayer.Transform.Position.X > 0f);
        Assert.Equal(Vector2.Zero, player.Transform.Position);
        Assert.Equal(0, player.GetComponent<Mover>()!.Updates);

        play.Stop();
        Assert.Equal(PlayState.Editing, play.State);
        Assert.Null(play.PlayScene);
        Assert.Same(_session.Scene, play.ActiveScene);
        Assert.Equal(Vector2.Zero, player.Transform.Position);
        Assert.False(_session.IsDirty);
    }

    [Fact]
    public void PlayMode_ObjectsCreatedAndDestroyedAtRuntimeStayInTheCopy()
    {
        _session.Scene.CreateGameObject("Spawner").AddComponent<Spawner>();
        var before = _session.Scene.GameObjects.Count;
        var play = NewPlayMode();
        play.Play();
        play.Tick(0.1);
        play.Tick(0.1);
        Assert.True(play.PlayScene!.GameObjects.Count > before);
        play.Stop();
        Assert.Equal(before, _session.Scene.GameObjects.Count);
    }

    [Fact]
    public void PlayMode_PauseFreezesAndStepAdvancesOneFrame()
    {
        var player = _session.Scene.CreateGameObject("Player");
        player.AddComponent<Mover>();
        var play = NewPlayMode();
        play.Play();
        var mover = play.PlayScene!.Find("Player")!.GetComponent<Mover>()!;

        play.Tick(0.1);
        play.TogglePause();
        var updates = mover.Updates;
        play.Tick(0.1);
        Assert.Equal(updates, mover.Updates);

        var fixedBefore = mover.FixedUpdates;
        play.Step();
        play.Tick(5.0); // real time is ignored for a step
        Assert.Equal(updates + 1, mover.Updates);
        Assert.Equal(fixedBefore + 1, mover.FixedUpdates);

        play.Tick(0.1);
        Assert.Equal(updates + 1, mover.Updates); // still paused
    }

    [Fact]
    public void PlayMode_SceneSwitchOnlyAffectsTheCopy()
    {
        File.WriteAllText(Path.Combine(_root, "Assets", "Scenes", "Other.mnvt"), """{ "schemaVersion": 1, "name": "Other", "gameObjects": [] }""");
        var play = NewPlayMode();
        play.Play();
        play.RequestLoad("Scenes/Other.mnvt");
        play.Tick(0.1);
        Assert.Equal("Other", play.PlayScene!.Name);
        Assert.Equal("Untitled", _session.Scene.Name);
        Assert.Throws<ArgumentException>(() => play.RequestLoad("../x.mnvt"));
    }

    [Fact]
    public void Inspector_ListsMembersWithKindsAndNames()
    {
        var members = MemberInspector.GetMembers(typeof(Holder)).ToDictionary(m => m.DisplayName);
        Assert.Equal(MemberKind.Float, members["Speed"].Kind);
        Assert.Equal(MemberKind.GameObjectReference, members["Target"].Kind);
        Assert.Equal(MemberKind.ComponentReference, members["Other"].Kind);
        Assert.Equal(MemberKind.AssetPath, members["Icon"].Kind);
        Assert.Equal(MemberKind.Int, members["Hidden Count"].Kind);
        Assert.True(members["Icon"].AcceptsAsset("Textures/a.PNG"));
        Assert.False(members["Icon"].AcceptsAsset("Scenes/a.mnvt"));

        var camera = MemberInspector.GetMembers(typeof(Camera)).Single(m => m.DisplayName == "Orthographic Size");
        var component = _session.Scene.MainCamera!;
        Assert.False(camera.TrySetValue(component, -1f, out var error));
        Assert.Contains("Orthographic Size", error, StringComparison.Ordinal);
        Assert.True(camera.TrySetValue(component, 3f, out _));
        Assert.Equal(3f, component.OrthographicSize);
    }

    [Fact]
    public void Inspector_ResolvesDroppedReferences()
    {
        var go = _session.Scene.CreateGameObject("Target");
        var mover = go.AddComponent<Mover>();
        var members = MemberInspector.GetMembers(typeof(Holder)).ToDictionary(m => m.DisplayName);
        Assert.Same(go, MemberInspector.ResolveReference(members["Target"], go));
        Assert.Same(mover, MemberInspector.ResolveReference(members["Other"], go));
        Assert.Null(MemberInspector.ResolveReference(members["Other"], _session.Scene.CreateGameObject("Empty")));
    }

    [Fact]
    public void Picking_RespectsRotationScaleAndSortingOrder()
    {
        var scene = new Scene();
        var back = scene.CreateGameObject("Back");
        back.AddComponent<SpriteRenderer>().Size = new Vector2(10f, 10f);
        var front = scene.CreateGameObject("Front");
        front.Transform.Position = new Vector2(2f, 0f);
        front.Transform.Rotation2D = 45f;
        front.Transform.LocalScale = new Vector3(2f, 2f, 1f);
        front.AddComponent<SpriteRenderer>().SortingOrder = 5;

        Assert.Same(front, SceneEditing.Pick(scene, new Vector2(2f, 1.3f), 0.1f));   // inside the rotated 2x2 diamond
        Assert.Same(back, SceneEditing.Pick(scene, new Vector2(3.3f, 1.3f), 0.1f));  // outside it, inside Back
        Assert.Null(SceneEditing.Pick(scene, new Vector2(50f, 50f), 0.1f));

        var marker = scene.CreateGameObject("Empty");
        marker.Transform.Position = new Vector2(-3f, -3f);
        Assert.Same(marker, SceneEditing.Pick(scene, new Vector2(-3.05f, -3f), 0.1f));
    }

    [Fact]
    public void Gizmo_MovesAlongAxisRotatesAndScales()
    {
        var t = new Scene().CreateGameObject().Transform;
        const float Upp = 0.1f; // world units per pixel: axis length 8 units
        var gizmo = new TransformGizmo();

        Assert.Equal(GizmoHandle.AxisX, gizmo.HitTest(t, new Vector2(5f, 0.2f), Upp));
        Assert.Equal(GizmoHandle.Free, gizmo.HitTest(t, new Vector2(0.3f, 0.3f), Upp));
        Assert.Equal(GizmoHandle.None, gizmo.HitTest(t, new Vector2(5f, 5f), Upp));

        gizmo.BeginDrag(t, GizmoHandle.AxisX, new Vector2(5f, 0f), Upp);
        gizmo.Drag(t, new Vector2(7f, 3f));
        gizmo.EndDrag();
        Assert.Equal(new Vector3(2f, 0f, 0f), t.Position);

        gizmo.Tool = GizmoTool.Rotate;
        Assert.Equal(GizmoHandle.Ring, gizmo.HitTest(t, new Vector2(9f, 0f), Upp));
        gizmo.BeginDrag(t, GizmoHandle.Ring, new Vector2(9f, 0f), Upp);
        gizmo.Drag(t, new Vector2(2f, 7f));
        Assert.True(Mathf.Approximately(90f, t.Rotation2D, 1e-3f));
        gizmo.EndDrag();

        gizmo.Tool = GizmoTool.Scale;
        t.Rotation2D = 0f;
        gizmo.BeginDrag(t, GizmoHandle.AxisY, new Vector2(2f, 8f), Upp);
        gizmo.Drag(t, new Vector2(2f, 16f)); // one handle length outward
        Assert.Equal(new Vector3(1f, 2f, 1f), t.LocalScale);
    }

    public sealed class Mover : Component
    {
        public int Updates;
        public int FixedUpdates;

        public override void FixedUpdate() => FixedUpdates++;

        public override void Update()
        {
            Updates++;
            Transform.Position += new Vector2(Time.DeltaTime, 0f);
        }
    }

    public sealed class Spawner : Component
    {
        public override void Update() => GameObject.Scene.CreateGameObject("Spawned");
    }

    public sealed class Holder : Component
    {
        public float Speed;
        public GameObject? Target;
        public Mover? Other;

        [AssetPath(".png")]
        public string? Icon;

        [SerializeField]
        private int _hiddenCount = 1;

        public int HiddenCount => _hiddenCount;
    }
}
