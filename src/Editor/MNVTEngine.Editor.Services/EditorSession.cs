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
    public EditorSession(GameProject project, ComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(registry);
        Project = project;
        Registry = registry;
        Serializer = new SceneSerializer(registry);
        Scene = CreateDefaultScene("Untitled");
    }

    public GameProject Project { get; }

    public ComponentRegistry Registry { get; }

    public SceneSerializer Serializer { get; }

    public Scene Scene { get; private set; }

    /// <summary>Path relative to Assets, or null for a scene that was never saved.</summary>
    public string? ScenePath { get; private set; }

    public bool IsDirty { get; private set; }

    public Selection Selection { get; } = new();

    /// <summary>Diagnostics from the most recent <see cref="OpenScene"/>.</summary>
    public IReadOnlyList<SerializationDiagnostic> LastLoadDiagnostics { get; private set; } = [];

    /// <summary>Raised after the edit scene is replaced (new or opened).</summary>
    public event Action? SceneReplaced;

    public string DisplayName => (ScenePath is null ? Scene.Name : Path.GetFileName(ScenePath)) + (IsDirty ? "*" : "");

    public void MarkDirty() => IsDirty = true;

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
        var full = Project.ResolveAsset(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        // Write next to the target and swap, so a crash mid-write never leaves a truncated scene.
        var temp = full + ".tmp";
        File.WriteAllText(temp, Serializer.Serialize(Scene));
        File.Move(temp, full, overwrite: true);

        ScenePath = relativePath;
        IsDirty = false;
    }

    private void Replace(Scene scene, string? path)
    {
        Scene = scene;
        ScenePath = path;
        IsDirty = false;
        Selection.Clear();
        SceneReplaced?.Invoke();
    }

    private static Scene CreateDefaultScene(string name)
    {
        var scene = new Scene(name);
        scene.CreateGameObject("Main Camera").AddComponent<Camera>();
        return scene;
    }
}

/// <summary>
/// Selected object, tracked by id so the same object stays selected when switching between the edit scene
/// and its Play Mode copy.
/// </summary>
public sealed class Selection
{
    public Guid? Id { get; private set; }

    public void Select(GameObject? gameObject) => Id = gameObject?.Id;

    public void Clear() => Id = null;

    public GameObject? Resolve(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return Id is { } id && scene.FindById(id) is { IsDestroyed: false } gameObject ? gameObject : null;
    }

    public bool IsSelected(GameObject gameObject) => gameObject is not null && Id == gameObject.Id;
}
