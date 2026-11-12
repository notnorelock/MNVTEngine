using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Editor.Services;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;

namespace MNVTEngine.Editor.Tests;

public sealed class AssetWorkflowTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-editor-assets-").FullName;
    private readonly EditorSession _session;

    public AssetWorkflowTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "Scenes"));
        File.WriteAllText(Path.Combine(_root, "Assets", "Scenes", "Main.mnvt"), """{ "schemaVersion": 2, "name": "Main", "gameObjects": [] }""");
        File.WriteAllText(Path.Combine(_root, "Game.mnvtproj"), """{ "schemaVersion": 1, "name": "Game", "startupScene": "Scenes/Main.mnvt" }""");
        _session = new EditorSession(GameProject.Load(_root), ComponentRegistry.CreateDefault().Register<Holder>());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Session_IndexesProjectAssets()
    {
        Assert.True(_session.Assets.TryGetGuid("Scenes/Main.mnvt", out var guid));
        Assert.Equal(AssetTypes.Scene, _session.Assets.GetAssetType(guid));
        Assert.True(File.Exists(Path.Combine(_root, "Assets", "Scenes", "Main.mnvt.meta")));
        Assert.True(File.Exists(Path.Combine(_root, "Library", "AssetDatabase.json")));
    }

    [Fact]
    public void SavedScenes_BecomeAssetsWithStableGuid()
    {
        _session.SaveScene("Scenes/Level.mnvt");
        Assert.True(_session.Assets.TryGetGuid("Scenes/Level.mnvt", out var first));
        _session.MarkDirty();
        _session.SaveScene();
        Assert.True(_session.Assets.TryGetGuid("Scenes/Level.mnvt", out var second));
        Assert.Equal(first, second);
    }

    [Fact]
    public void CreatePrefab_ThenInstantiateIntoEditAndPlayScenes()
    {
        var root = _session.Scene.CreateGameObject("Enemy");
        root.AddComponent<Holder>().Speed = 7f;
        _session.Scene.CreateGameObject("Weapon", root.Transform);

        var prefab = _session.CreatePrefab(root, "Prefabs/Enemy.mnvtprefab");
        Assert.Equal(AssetTypes.Prefab, _session.Assets.GetAssetType(prefab));

        var copy = _session.Scene.Instantiate(new AssetRef(prefab));
        Assert.NotEqual(root.Id, copy.Id);
        Assert.Equal(7f, copy.GetComponent<Holder>()!.Speed);
        Assert.Equal("Weapon", copy.Transform.Children.Single().GameObject.Name);

        var play = new PlayModeController(_session, NullLoggerFactory.Instance);
        play.Play();
        var spawned = play.PlayScene!.Instantiate(new AssetRef(prefab));
        Assert.True(spawned.GetComponent<Holder>()!.Awoken);
        play.Stop();
    }

    [Fact]
    public void Instantiate_RejectsNonPrefabAssets()
    {
        Assert.True(_session.Assets.TryGetGuid("Scenes/Main.mnvt", out var scene));
        Assert.Throws<InvalidOperationException>(() => _session.Scene.Instantiate(new AssetRef(scene)));
    }

    [Fact]
    public void Selection_ObjectsAndAssetsAreExclusive()
    {
        var go = _session.Scene.CreateGameObject();
        _session.Selection.Select(go);
        _session.Selection.SelectAsset(Guid.NewGuid());
        Assert.Null(_session.Selection.Id);
        _session.Selection.Select(go);
        Assert.Null(_session.Selection.Asset);
    }

    [Fact]
    public void Inspector_ClassifiesAssetReferences()
    {
        var member = MemberInspector.GetMembers(typeof(SpriteRenderer)).Single(m => m.DisplayName == "Texture");
        Assert.Equal(MemberKind.AssetReference, member.Kind);
        Assert.True(member.AcceptsAssetType(AssetTypes.Texture));
        Assert.False(member.AcceptsAssetType(AssetTypes.Prefab));
        Assert.False(member.AcceptsAssetType(null));
    }

    [Fact]
    public void PlayMode_HandsEveryPlaySceneToTheModules()
    {
        // The browser module attaches its service this way; without it surfaces found no browser in Play Mode.
        var module = new RecordingModule();
        var play = new PlayModeController(_session, NullLoggerFactory.Instance) { Modules = [module] };
        play.Play();
        Assert.Same(play.PlayScene, Assert.Single(module.Scenes));
        play.Stop();
    }

    private sealed class RecordingModule : Scenes.IEngineModule, Scenes.ISceneIntegration
    {
        public List<Scene> Scenes { get; } = [];

        public string Id => "test.recording";

        public void Register(Scenes.ComponentRegistry components)
        {
        }

        public void SceneLoaded(Scene scene) => Scenes.Add(scene);
    }

    [Fact]
    public void PlayMode_LoadSceneByAssetRef()
    {
        File.WriteAllText(Path.Combine(_root, "Assets", "Scenes", "Other.mnvt"), """{ "schemaVersion": 2, "name": "Other", "gameObjects": [] }""");
        _session.Assets.Refresh();
        Assert.True(_session.Assets.TryGetGuid("Scenes/Other.mnvt", out var other));

        var play = new PlayModeController(_session, NullLoggerFactory.Instance);
        play.Play();
        play.RequestLoad(new AssetRef(other));
        play.Tick(0.1);
        Assert.Equal("Other", play.PlayScene!.Name);
        Assert.Throws<ArgumentException>(() => play.RequestLoad(new AssetRef(Guid.NewGuid())));
    }

    [Fact]
    public void PlayMode_GivesPlayScenesAGameUI_LaidOutToTheGameView_ThatTakesTheMouse()
    {
        using var font = Graphics.Text.Font.LoadDefault();
        var play = new PlayModeController(_session, NullLoggerFactory.Instance)
        {
            UIServices = new UI.UIServices(font, null, null, null),
            ViewportSize = new Mathematics.Vector2(400, 300),
        };
        play.Play();
        var context = UI.UISystem.For(play.PlayScene!)!.CreateContext();
        var button = context.Root.AddChild(new UI.Widgets.Button { Text = "Play", Width = 100, Height = 40 });

        play.Input.BeginFrame();
        play.Input.SetMousePosition(new Mathematics.Vector2(20, 20));
        play.Tick(0.016);
        Assert.Equal(new Mathematics.Vector2(400, 300), context.Size);
        Assert.Same(button, context.Hovered);
        Assert.True(play.Input.MouseCaptured); // gameplay does not see the click meant for the UI

        Application.Quit(); // game code quitting ends Play Mode after the frame
        Assert.NotNull(play.PlayScene);
        play.Tick(0.016);
        Assert.False(play.IsPlaying);
    }

    public sealed class Holder : Component
    {
        public float Speed;

        [DontSerialize]
        public bool Awoken;

        public override void Awake() => Awoken = true;
    }
}
