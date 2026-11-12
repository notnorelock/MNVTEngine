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

/// <summary>
/// The "ui" engine module: registers UI documents, and draws each scene's UI over it in screen space (a screen layer of
/// the host), with the window's clipboard and the project's fonts and textures.
/// </summary>
public sealed class UIModule : IEngineModule, Runtime.IScreenIntegration, IDiagnosticsIntegration
{
    private Func<UIRenderer?> _renderer = () => null; // the host owns the layer and its renderer

    public string Id => "ui";

    public void Register(ComponentRegistry components) => UIComponents.Register(components);

    /// <summary>The renderer of the host's UI layer (glyph atlas inspection); null before the layer exists.</summary>
    public UIRenderer? Renderer => _renderer();

    public Runtime.IScreenLayer CreateScreenLayer(Runtime.ScreenServices services)
    {
        var layer = new UIScreenLayer(services);
        _renderer = () => layer.Renderer;
        return layer;
    }

    public IEnumerable<Diagnostics.IMetricsProvider> CreateMetricsProviders(Func<Scene?> scene) => [new UIMetrics(scene, () => _renderer())];

    private sealed class UIScreenLayer(Runtime.ScreenServices services) : Runtime.IScreenLayer
    {
        public UIRenderer Renderer { get; } = new(services.Renderer.Device, services.Renderer.DefaultMaterial);

        public void SceneLoaded(Scene scene) => UISystem.Attach(scene, new UIServices(services.DefaultFont, new WindowClipboard(services.Window),
            asset => services.Assets.ReadAllBytes(asset.Guid), asset => services.Resources.GetTexture(asset)));

        public void Update(Scene scene, float deltaSeconds)
        {
            if (UISystem.For(scene) is { } ui)
            {
                using var sample = Core.Diagnostics.Profiler.Sample("UI");
                var window = services.Window;
                var framebufferScale = window.WindowWidth > 0 ? (float)window.FramebufferWidth / window.WindowWidth : 1f;
                ui.Update(window.Input, new Mathematics.Vector2(window.WindowWidth, window.WindowHeight), framebufferScale, deltaSeconds);
            }
        }

        public void Render(Scene scene, int framebufferWidth, int framebufferHeight)
        {
            if (UISystem.For(scene) is { } ui)
            {
                using var sample = Core.Diagnostics.Profiler.Sample("UI");
                services.Renderer.Device.SetRenderTarget(null);
                ui.Render(Renderer, framebufferWidth, framebufferHeight);
            }
        }

        public void Dispose() => Renderer.Dispose();
    }

    /// <summary>Game UI of the current scene: element counts, draws, cache behaviour and the glyph atlas.</summary>
    private sealed class UIMetrics(Func<Scene?> scene, Func<UIRenderer?> renderer) : Diagnostics.IMetricsProvider
    {
        public string Name => "UI";

        public void Collect(Diagnostics.MetricsSnapshot metrics)
        {
            if (scene() is not { } current || UISystem.For(current) is not { } ui)
            {
                return;
            }

            var totals = ui.Totals();
            metrics.Set(Diagnostics.Metrics.UIElements, totals.Elements);
            metrics.Set(Diagnostics.Metrics.UIVisibleElements, totals.Visible);
            metrics.Set(Diagnostics.Metrics.UIDrawCalls, totals.DrawCalls);
            metrics.Set(Diagnostics.Metrics.UIQuads, totals.Quads);
            metrics.Set(Diagnostics.Metrics.UILayoutPasses, totals.LayoutPasses);
            metrics.Set(Diagnostics.Metrics.UIGeometryRebuilds, totals.Rebuilds);
            metrics.Set(Diagnostics.Metrics.UITextLayouts, TextBlock.Builds);
            if (renderer() is { } atlas)
            {
                metrics.Set(Diagnostics.Metrics.UIGlyphs, atlas.Atlas.GlyphCount);
                metrics.Set(Diagnostics.Metrics.UIAtlasPages, atlas.Atlas.PageCount);
            }
        }
    }
}
