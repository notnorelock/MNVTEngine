using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Assets;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;

namespace MNVTEngine.Editor.Services;

/// <summary>
/// The scene being edited: which file it came from, whether it has unsaved changes, and what is selected.
/// The edit scene never runs; Play Mode works on a copy (<see cref="PlayModeController"/>).
/// </summary>
public sealed class EditorSession
{
    public EditorSession(GameProject project, ComponentRegistry registry, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(registry);
        Project = project;
        Registry = registry;
        Assets = AssetDatabase.OpenForEditing(project.AssetsDirectory, Path.Combine(project.RootDirectory, "Library"), MNVTEngine.Assets.Models.ModelImporters.WithDefaults());
        Serializer = new SceneSerializer(registry, Assets);
        Prefabs = new PrefabProvider(Serializer, Assets, logger ?? NullLogger.Instance);
        ModelAnimations = new ModelAnimationProvider(Assets, logger ?? NullLogger.Instance);
        Scene = CreateDefaultScene("Untitled");
        History = new UndoHistory(() => Serializer.Serialize(Scene), Restore);
        History.Reset();
    }

    /// <summary>The project's assets in editing mode (creates metas, imports, caches in Library/).</summary>
    public AssetDatabase Assets { get; }

    public PrefabProvider Prefabs { get; }

    /// <summary>Animation clips of imported models for scenes (lives as long as the session's asset database).</summary>
    public ModelAnimationProvider ModelAnimations { get; }

    public GameProject Project { get; }

    public ComponentRegistry Registry { get; }

    public SceneSerializer Serializer { get; }

    public Scene Scene { get; private set; }

    /// <summary>Path relative to Assets, or null for a scene that was never saved.</summary>
    public string? ScenePath { get; private set; }

    public bool IsDirty { get; private set; }

    private string? _savedSnapshot;

    public Selection Selection { get; } = new();

    /// <summary>Undo/redo of edits to <see cref="Scene"/>; reset when a scene is created or opened.</summary>
    public UndoHistory History { get; }

    /// <summary>Diagnostics from the most recent <see cref="OpenScene"/>.</summary>
    public IReadOnlyList<SerializationDiagnostic> LastLoadDiagnostics { get; private set; } = [];

    /// <summary>Raised after the edit scene is replaced (new or opened).</summary>
    public event Action? SceneReplaced;

    public string DisplayName => (ScenePath is null ? Scene.Name : Path.GetFileName(ScenePath)) + (IsDirty ? "*" : "");

    public void MarkDirty() => IsDirty = true;

    /// <summary>Records an edit for undo and marks the scene unsaved.</summary>
    public void Edited(string label)
    {
        IsDirty = true;
        History.MarkChanged(label);
    }

    /// <summary>New scene with a camera, like a fresh project template.</summary>
    public void NewScene(string name = "Untitled")
    {
        Replace(CreateDefaultScene(name), null);
    }

    /// <exception cref="SerializationException">Unreadable scene file.</exception>
    public SceneLoadResult OpenScene(string relativePath)
    {
        string json;
        try
        {
            json = File.ReadAllText(Project.ResolveAsset(relativePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new SerializationException(relativePath, ex.Message, [], ex);
        }

        var result = Serializer.Deserialize(json, relativePath);
        LastLoadDiagnostics = result.Diagnostics;
        Replace(result.Scene, relativePath);
        return result;
    }

    /// <summary>Saves to <paramref name="relativePath"/>, or to the current path. The file is replaced atomically.</summary>
    /// <exception cref="InvalidOperationException">No path given and the scene was never saved.</exception>
    /// <exception cref="ArgumentException">The path escapes the Assets folder.</exception>
    public void SaveScene(string? relativePath = null)
    {
        relativePath ??= ScenePath ?? throw new InvalidOperationException("The scene has no file yet; choose a path.");

        // Written atomically (temp file + swap) and indexed, so the scene gets a GUID like any other asset.
        History.Commit();
        var json = Serializer.Serialize(Scene);
        Assets.WriteText(relativePath, json);

        ScenePath = relativePath;
        IsDirty = false;
        _savedSnapshot = json;
    }

    /// <summary>Swaps in the scene rebuilt after a script reload; file, unsaved-changes state and selection are kept.</summary>
    public void ReplaceAfterScriptReload(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        scene.Prefabs = Prefabs;
        scene.TextAssets = Prefabs;
        scene.ModelAnimations = ModelAnimations;
        scene.MeshData = ModelAnimations;
        Scene = scene;
        SceneReplaced?.Invoke();
    }

    /// <summary>Saves <paramref name="root"/> and its children as a prefab asset and returns its GUID.</summary>
    public Guid CreatePrefab(GameObject root, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        return Assets.WriteText(relativePath, Serializer.SerializePrefab(root));
    }

    private void Replace(Scene scene, string? path)
    {
        scene.Prefabs = Prefabs;
        scene.TextAssets = Prefabs;
        scene.ModelAnimations = ModelAnimations;
        scene.MeshData = ModelAnimations;
        Scene = scene;
        ScenePath = path;
        IsDirty = false;
        Selection.Clear();
        History.Reset();
        _savedSnapshot = path is null ? null : Serializer.Serialize(scene);
        SceneReplaced?.Invoke();
    }

    /// <summary>Undo/redo: rebuilds the scene from a snapshot; file and selection (by id) are kept.</summary>
    private void Restore(string snapshot)
    {
        var restored = Serializer.Deserialize(snapshot, ScenePath ?? Scene.Name).Scene;
        restored.Prefabs = Prefabs;
        restored.TextAssets = Prefabs;
        restored.ModelAnimations = ModelAnimations;
        restored.MeshData = ModelAnimations;
        Scene = restored;
        IsDirty = snapshot != _savedSnapshot;
        if (Selection.Id is { } id && restored.FindById(id) is null)
        {
            Selection.Clear();
        }

        SceneReplaced?.Invoke();
    }

    private Scene CreateDefaultScene(string name)
    {
        var scene = new Scene(name);
        scene.CreateGameObject("Main Camera").AddComponent<Camera>();
        scene.Prefabs = Prefabs;
        scene.TextAssets = Prefabs;
        scene.ModelAnimations = ModelAnimations;
        scene.MeshData = ModelAnimations;
        return scene;
    }
}

/// <summary>
/// Selected object, tracked by id so the same object stays selected when switching between the edit scene
/// and its Play Mode copy.
/// </summary>
public sealed class Selection
{
    /// <summary>Selected GameObject id. Selecting an object clears <see cref="Asset"/> and vice versa.</summary>
    public Guid? Id { get; private set; }

    /// <summary>Selected asset GUID (Project panel).</summary>
    public Guid? Asset { get; private set; }

    public void Select(GameObject? gameObject)
    {
        Id = gameObject?.Id;
        Asset = null;
    }

    public void SelectAsset(Guid? asset)
    {
        Asset = asset;
        Id = null;
    }

    public void Clear()
    {
        Id = null;
        Asset = null;
    }

    public GameObject? Resolve(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return Id is { } id && scene.FindById(id) is { IsDestroyed: false } gameObject ? gameObject : null;
    }

    public bool IsSelected(GameObject gameObject) => gameObject is not null && Id == gameObject.Id;
}
