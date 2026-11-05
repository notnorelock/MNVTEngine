using MNVTEngine.Scenes;
using MNVTEngine.Serialization;

namespace MNVTEngine.UI;

/// <summary>
/// Shows a UI layout asset (<c>.mnvtui</c>, made in the editor's UI Editor) in its own context while the component is
/// active. Game code binds behaviour by action name: <c>GetComponent&lt;UIDocument&gt;().Context!.OnAction("play", ...)</c>.
/// Without a UI system in the scene (the editor's edit mode) it shows nothing.
/// </summary>
[ComponentId("MNVTEngine.UIDocument")]
public sealed class UIDocument : Component
{
    [SerializeField]
    [AssetType(AssetTypes.UILayout)]
    public AssetRef Layout { get; set; }

    /// <summary>Contexts with a higher order draw on top and get input first.</summary>
    [SerializeField]
    public int SortOrder { get; set; }

    /// <summary>Logical pixels to window pixels (UI scale).</summary>
    [SerializeField]
    public float Scale { get; set; } = 1f;

    /// <summary>The context showing the layout; null before Awake, without a UI system, or after destruction.</summary>
    public UIContext? Context { get; private set; }

    /// <summary>The loaded layout's root element; null when not loaded.</summary>
    public UIElement? Root { get; private set; }

    /// <summary>Loaded in Awake, so other components can bind actions in their Start.</summary>
    public override void Awake()
    {
        if (UISystem.For(GameObject.Scene) is not { } system)
        {
            return;
        }

        Context = system.CreateContext();
        Context.SortOrder = SortOrder;
        Context.Scale = Scale;
        if (Layout.IsNone)
        {
            Log.Warn($"UIDocument on '{GameObject.Name}' has no layout.");
            return;
        }

        try
        {
            Root = Context.Root.AddChild(system.LoadLayout(Layout));
        }
        catch (Exception ex) when (ex is SerializationException or IOException or KeyNotFoundException)
        {
            Log.Error($"UIDocument on '{GameObject.Name}': layout {Layout} could not be loaded.", ex);
        }
    }

    public override void OnEnable() => Context?.Visible = true;

    public override void OnDisable() => Context?.Visible = false;

    /// <summary>Moved by DontDestroyOnLoad: the context joins the new scene's UI (with the fonts it may use).</summary>
    protected override void OnMovedToScene(Scene previous)
    {
        if (Context is null || UISystem.For(previous) is not { } old)
        {
            return;
        }

        if (UISystem.For(GameObject.Scene) is { } current)
        {
            current.AdoptFonts(old);
            Context.MoveTo(current);
        }
        else
        {
            Context.Dispose(); // no UI in the new scene
            (Context, Root) = (null, null);
        }
    }

    public override void OnDestroy()
    {
        Context?.Dispose();
        (Context, Root) = (null, null);
    }
}

public static class UIComponents
{
    public static ComponentRegistry Register(ComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Register<UIDocument>();
    }
}
