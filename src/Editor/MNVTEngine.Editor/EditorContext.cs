using Microsoft.Extensions.Logging;
using MNVTEngine.Core.Logging;
using MNVTEngine.Editor.ImGuiBackend;
using MNVTEngine.Editor.Services;
using MNVTEngine.Graphics;
using MNVTEngine.Platform;
using MNVTEngine.Runtime;

namespace MNVTEngine.Editor;

/// <summary>Services shared by the editor panels. Created in <see cref="EditorApp.Load"/> while the GL context is current.</summary>
internal sealed class EditorContext
{
    public required GameWindow Window { get; init; }

    public required IGraphicsDevice Device { get; init; }

    public required Renderer2D Renderer { get; init; }

    public required ResourceCache Resources { get; init; }

    public required SceneRenderer SceneRenderer { get; init; }

    public required ImGuiRenderer ImGui { get; init; }

    public required EditorSession Session { get; init; }

    public required PlayModeController PlayMode { get; init; }

    public required InMemoryLoggerProvider Logs { get; init; }

    public required ILogger Logger { get; init; }

    /// <summary>The scene panels show and edit: the Play Mode copy while playing.</summary>
    public Scene ActiveScene => PlayMode.ActiveScene;

    public GameObject? Selected => Session.Selection.Resolve(ActiveScene);

    /// <summary>Records an edit. Edits made during Play Mode change only the copy and are discarded on Stop.</summary>
    public void Changed()
    {
        if (!PlayMode.IsPlaying)
        {
            Session.MarkDirty();
        }
    }

    /// <summary>Set by panels; handled by <see cref="EditorApp"/> with an unsaved-changes check.</summary>
    public string? OpenSceneRequest { get; set; }

    public GameObject CreateGameObject(string name, Transform? parent = null)
    {
        var gameObject = ActiveScene.CreateGameObject(name, parent);
        Session.Selection.Select(gameObject);
        Changed();
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
            Changed();
            return instance;
        }
        catch (InvalidOperationException ex)
        {
            EditorLog.ActionFailed(Logger, ex, "Instantiate prefab");
            return null;
        }
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
        Target = context.Device.CreateRenderTarget(1, 1, SamplerState.PointClamp, name);
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
        Target = _context.Device.CreateRenderTarget(_requestedWidth, _requestedHeight, SamplerState.PointClamp, _name);
        _context.ImGui.Rebind(Id, Target);
        old.Dispose();
    }

    public void Dispose()
    {
        _context.ImGui.Unregister(Target);
        Target.Dispose();
    }
}
