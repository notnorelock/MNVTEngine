using MNVTEngine.Editor.Services;
using MNVTEngine.Mathematics;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;

namespace MNVTEngine.Editor.Tests;

public sealed class UndoTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-undo-").FullName;
    private readonly EditorSession _session;

    public UndoTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "Scenes"));
        File.WriteAllText(Path.Combine(_root, "Assets", "Scenes", "Main.mnvt"), """{ "schemaVersion": 2, "name": "Main", "gameObjects": [] }""");
        File.WriteAllText(Path.Combine(_root, "Game.mnvtproj"), """{ "schemaVersion": 1, "name": "Game", "startupScene": "Scenes/Main.mnvt" }""");
        _session = new EditorSession(GameProject.Load(_root), Build.EngineModuleSet.CreateRegistry());
        _session.OpenScene("Scenes/Main.mnvt");
    }

    private UndoHistory History => _session.History;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private GameObject Create(string name)
    {
        var go = _session.Scene.CreateGameObject(name);
        _session.Edited("Create");
        History.Commit();
        return go;
    }

    [Fact]
    public void ADragIsOneStep_AndUndoRedoRestoreTheScene_KeepingTheSelection()
    {
        var box = Create("Box");
        _session.Selection.Select(box);

        for (var x = 1; x <= 10; x++)
        {
            box.Transform.Position = new Vector2(x, 0);
            _session.Edited("Move"); // every frame of a drag
        }

        Assert.Equal("Move", History.UndoLabel);
        History.Commit();
        Assert.Equal(2, History.Count);

        Assert.True(History.Undo());
        Assert.Equal(Vector2.Zero, _session.Scene.Find("Box")!.Transform.Position);
        Assert.Equal(_session.Scene.Find("Box"), _session.Selection.Resolve(_session.Scene)); // same id, new instance
        Assert.Equal("Move", History.RedoLabel);

        Assert.True(History.Redo());
        Assert.Equal(new Vector2(10, 0), _session.Scene.Find("Box")!.Transform.Position);

        History.Undo();
        History.Undo();
        Assert.Null(_session.Scene.Find("Box"));
        Assert.Null(_session.Selection.Id); // the selected object no longer exists
        Assert.False(History.CanUndo);
        Assert.False(History.Undo());
    }

    [Fact]
    public void ANewEdit_ClearsRedo_AndUnchangedValuesMakeNoStep()
    {
        var box = Create("Box");
        box.Transform.Position = new Vector2(1, 1);
        _session.Edited("Move");
        History.Commit();
        History.Undo();
        Assert.True(History.CanRedo);

        box = _session.Scene.Find("Box")!;
        box.Name = "Crate";
        _session.Edited("Rename");
        Assert.False(History.CanRedo); // pending edit: redo would discard it
        History.Commit();
        Assert.False(History.CanRedo);

        var before = History.Count;
        _session.Scene.Find("Crate")!.Name = "Crate";
        _session.Edited("Rename");
        Assert.False(History.Commit());
        Assert.Equal(before, History.Count);
    }

    [Fact]
    public void UndoingBackToTheSavedState_ClearsTheUnsavedMark_AndOpeningAnotherSceneResets()
    {
        Create("Box");
        _session.SaveScene();
        Assert.False(_session.IsDirty);

        _session.Scene.Find("Box")!.Name = "Crate";
        _session.Edited("Rename");
        Assert.True(_session.IsDirty);
        History.Undo();
        Assert.False(_session.IsDirty);
        History.Redo();
        Assert.True(_session.IsDirty);

        _session.NewScene();
        Assert.False(History.CanUndo);
        Assert.False(History.CanRedo);
    }

    [Fact]
    public void DeletesAndComponentEdits_AreUndoable()
    {
        var map = Create("Map");
        var tiles = map.AddComponent<Tilemap>();
        TilePainter.Stroke(tiles, new Vector2(0.5f, 0.5f), new Vector2(4.5f, 0.5f), 2);
        _session.Edited("Paint Tiles");
        History.Commit();

        SceneEditing.Delete(_session, _session.Scene.Find("Map")!);
        History.Commit();
        Assert.Null(_session.Scene.Find("Map"));

        History.Undo();
        Assert.Equal(5, _session.Scene.Find("Map")!.GetComponent<Tilemap>()!.Count);
        History.Undo(); // adding the tilemap and painting were one step
        Assert.Null(_session.Scene.Find("Map")!.GetComponent<Tilemap>());
    }

    [Fact]
    public void History_KeepsAtMostItsCapacity()
    {
        var value = 0;
        var history = new UndoHistory(() => value.ToString(System.Globalization.CultureInfo.InvariantCulture), s => value = int.Parse(s, System.Globalization.CultureInfo.InvariantCulture), capacity: 3);
        history.Reset();
        for (var i = 1; i <= 5; i++)
        {
            value = i;
            history.MarkChanged("Set");
            history.Commit();
        }

        Assert.Equal(3, history.Count);
        while (history.Undo())
        {
        }

        Assert.Equal(2, value); // the two oldest steps were dropped
    }
}
