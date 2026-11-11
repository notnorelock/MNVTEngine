using Microsoft.Extensions.Logging;
using MNVTEngine.Core.Logging;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Editor.Services;
using MNVTEngine.Graphics;
using MNVTEngine.Platform;
using MNVTEngine.Runtime;

namespace MNVTEngine.Editor;

/// <summary>Services shared by the editor panels. Created in <see cref="EditorApp.Load"/> after the graphics device.</summary>
internal sealed class EditorContext
{
    public required GameWindow Window { get; init; }

    public required IGraphicsDevice Device { get; init; }

    public required Renderer2D Renderer { get; init; }

    public required ResourceCache Resources { get; init; }

    public required SceneRenderer SceneRenderer { get; init; }

    public required ImGuiRenderer ImGui { get; init; }

    /// <summary>Draws the game UI of the play scene into the Game view.</summary>
    public required UI.UIRenderer GameUI { get; init; }

    public required EditorSession Session { get; init; }

    public required PlayModeController PlayMode { get; init; }

    public required InMemoryLoggerProvider Logs { get; init; }

    /// <summary>Diagnostics settings of this editor session: the project's, plus overrides made in the Diagnostics window (used from the next Play).</summary>
    public required Diagnostics.DiagnosticsSettings DiagnosticsSettings { get; set; }

    /// <summary>Per-logger levels and call-site capture (edited from the Console's Levels popup).</summary>
    public required LogHub LogHub { get; init; }

    public required ILogger Logger { get; init; }

    /// <summary>The scene panels show and edit: the Play Mode copy while playing.</summary>
    public Scene ActiveScene => PlayMode.ActiveScene;

    public GameObject? Selected => Session.Selection.Resolve(ActiveScene);

    /// <summary>Breakpoints and stops of <c>.mnvts</c> scripts in Play Mode.</summary>
    public ScriptDebugSession ScriptDebugger { get; } = new();

    /// <summary>Terrain world editing shared by the Terrain window and the Scene view.</summary>
    public TerrainEditor TerrainEditor => field ??= new TerrainEditor(Logger);

    /// <summary>Tile brush shared by the inspector palette and the Scene view.</summary>
    public TilePainter Tiles { get; } = new();

    /// <summary>Records an edit for undo. Edits made during Play Mode change only the copy and are discarded on Stop.</summary>
    public void Changed(string label = "Edit")
    {
        if (!PlayMode.IsPlaying)
        {
            Session.Edited(label);
        }
    }

    /// <summary>File to open in the Script Editor (full path); set by panels, handled by <see cref="EditorApp"/>.</summary>
    public string? OpenScriptRequest { get; set; }

    /// <summary>Set by panels; handled by <see cref="EditorApp"/> with an unsaved-changes check.</summary>
    public string? OpenSceneRequest { get; set; }

    /// <summary>Dock node of the Game view (the central area); windows new to a saved layout dock there.</summary>
    public uint CenterDockId { get; set; }

    /// <summary>Dock node of the Inspector; the Terrain window docks there in layouts saved before it existed.</summary>
    public uint InspectorDockId { get; set; }

    /// <summary>A UI layout (full path) to open in the UI Editor this frame.</summary>
    public string? OpenUILayoutRequest { get; set; }

    /// <summary>A terrain world (<c>.mnvtterrain</c>) to edit next frame, through the WorldTerrain that uses it in the open scene.</summary>
    public Guid? OpenTerrainRequest { get; set; }

    /// <summary>An animator controller (<c>.mnvtanimator</c>) to open in the Animator window next frame.</summary>
    public string? OpenAnimatorRequest { get; set; }

    public GameObject CreateGameObject(string name, Transform? parent = null)
    {
        var gameObject = ActiveScene.CreateGameObject(name, parent);
        Session.Selection.Select(gameObject);
        Changed("Create");
        return gameObject;
    }

    /// <summary>Instantiates a prefab asset into the active scene and selects it. Failures are logged.</summary>
    public GameObject? InstantiatePrefab(Guid prefab, Transform? parent, Mathematics.Vector2? position = null)
    {
        try
        {
            var instance = ActiveScene.Instantiate(new AssetRef(prefab), parent);
            if (position is { } p)
            {
                instance.Transform.Position = p;
            }

            Session.Selection.Select(instance);
            Changed("Instantiate Prefab");
            return instance;
        }
        catch (InvalidOperationException ex)
        {
            EditorLog.ActionFailed(Logger, ex, "Instantiate prefab");
            return null;
        }
    }

    /// <summary>
    /// Instantiates an imported model (FBX, glTF, GLB) into the active scene as a GameObject hierarchy whose mesh
    /// renderers reference the model's meshes, and selects its root. A model that has not imported is logged.
    /// </summary>
    public GameObject? InstantiateModel(Guid model, Transform? parent, Mathematics.Vector3? position = null)
    {
        var path = Session.Assets.Get(model)?.Path ?? model.ToString();
        if (Session.Assets.ReadArtifact(model, Assets.Models.ModelArtifact.Name) is not { } artifact)
        {
            EditorLog.Warning(Logger, $"Model '{path}' has not imported; fix its import errors (see the asset inspector) and try again.");
            return null;
        }

        Assets.Models.ModelData data;
        try
        {
            data = Assets.Models.ModelArtifact.Read(artifact);
        }
        catch (InvalidDataException ex)
        {
            EditorLog.ActionFailed(Logger, ex, "Instantiate model");
            return null;
        }

        var instance = Runtime.ModelInstantiation.Instantiate(ActiveScene, model, data, Path.GetFileNameWithoutExtension(path), parent);
        if (position is { } p)
        {
            instance.Transform.Position = p;
        }

        Session.Selection.Select(instance);
        Changed("Instantiate Model");
        return instance;
    }

    /// <summary>Resolves a dragged asset path to its GUID and type.</summary>
    public (Guid Guid, string Type)? ResolveAsset(string relativePath) =>
        Session.Assets.TryGetGuid(relativePath, out var guid) && Session.Assets.GetAssetType(guid) is { } type ? (guid, type) : null;

    public void Delete(GameObject gameObject)
    {
        if (PlayMode.IsPlaying)
        {
            gameObject.Destroy(); // deferred to the end of the play frame
            return;
        }

        SceneEditing.Delete(Session, gameObject);
    }
}

/// <summary>A render target that follows the size of the panel showing it, under a stable ImGui texture id.</summary>
internal sealed class ViewTarget : IDisposable
{
    private readonly EditorContext _context;
    private readonly string _name;
    private int _requestedWidth = 1;
    private int _requestedHeight = 1;

    public ViewTarget(EditorContext context, string name)
    {
        _context = context;
        _name = name;
        Target = context.Device.CreateRenderTarget(1, 1, SamplerState.PointClamp, name, depth: true);
        Id = context.ImGui.Register(Target);
    }

    public RenderTarget2D Target { get; private set; }

    public nint Id { get; }

    public void Request(int width, int height)
    {
        _requestedWidth = Math.Max(1, width);
        _requestedHeight = Math.Max(1, height);
    }

    /// <summary>Recreates the target if the panel changed size. Call during rendering, before drawing into it.</summary>
    public void EnsureSize()
    {
        if (Target.Width == _requestedWidth && Target.Height == _requestedHeight)
        {
            return;
        }

        var old = Target;
        Target = _context.Device.CreateRenderTarget(_requestedWidth, _requestedHeight, SamplerState.PointClamp, _name, depth: true); // 3D views depth-test
        _context.ImGui.Rebind(Id, Target);
        old.Dispose();
    }

    public void Dispose()
    {
        _context.ImGui.Unregister(Target);
        Target.Dispose();
    }
}
