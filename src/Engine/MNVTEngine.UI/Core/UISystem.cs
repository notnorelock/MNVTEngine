using MNVTEngine.ECS;
using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Text;
using MNVTEngine.Mathematics;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;

namespace MNVTEngine.UI;

/// <summary>What the host gives the UI of a scene: the default font, the clipboard, and access to font and texture assets.</summary>
/// <param name="ReadAsset">Bytes of an asset (project folder or content archives); used to load fonts. Must be thread-safe.</param>
/// <param name="LoadTexture">A texture asset as a GPU texture (cached by the host), or null when missing.</param>
public sealed record UIServices(Font DefaultFont, IClipboard? Clipboard, Func<AssetRef, byte[]>? ReadAsset, Func<AssetRef, Texture2D?>? LoadTexture);

/// <summary>
/// The UI of one scene: its contexts (created with <see cref="UI.CreateContext"/>), input routing in
/// <see cref="UIContext.SortOrder"/> order (a context under the pointer blocks the ones below), keyboard and mouse
/// capture from gameplay, rendering after the scene, and a font cache. The host attaches it when a scene loads and
/// calls <see cref="Update"/> before gameplay updates, so gameplay sees input the UI took as released.
/// </summary>
public sealed class UISystem : GameSystem
{
    private readonly List<UIContext> _contexts = [];
    private readonly Dictionary<AssetRef, Task<Font>> _fonts = [];

    private UISystem(Scene scene, UIServices services)
    {
        Services = services;
        scene.Unloaded += _ => Dispose();
    }

    public UIServices Services { get; }

    public IReadOnlyList<UIContext> Contexts => _contexts;

    /// <exception cref="InvalidOperationException">The scene already has a UI system.</exception>
    public static UISystem Attach(Scene scene, UIServices services)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(services);
        if (For(scene) is not null)
        {
            throw new InvalidOperationException("The scene already has a UI system.");
        }

        var system = new UISystem(scene, services);
        scene.Systems.Add(system);
        return system;
    }

    public static UISystem? For(Scene scene) => scene?.Systems.Get<UISystem>();

    public UIContext CreateContext()
    {
        var context = new UIContext(this, Services.DefaultFont) { Clipboard = Services.Clipboard };
        _contexts.Add(context);
        return context;
    }

    /// <summary>Loads a font asset on a worker thread (reading and parsing are CPU only); cached per asset.</summary>
    /// <exception cref="InvalidOperationException">The host provides no asset access.</exception>
    public Task<Font> LoadFontAsync(AssetRef font)
    {
        var read = Services.ReadAsset ?? throw new InvalidOperationException("This UI system cannot read assets.");
        if (!_fonts.TryGetValue(font, out var task))
        {
            _fonts[font] = task = Task.Run(() => Font.Load(read(font), font.ToString()));
        }

        return task;
    }

    /// <summary>Loads a font asset now (blocking); prefer <see cref="LoadFontAsync"/> during gameplay.</summary>
    public Font LoadFont(AssetRef font) => LoadFontAsync(font).GetAwaiter().GetResult();

    /// <summary>Updates contexts top to bottom and takes keyboard/mouse from gameplay where the UI uses them.</summary>
    /// <param name="windowSize">Viewport size in window pixels.</param>
    public void Update(InputState input, Vector2 windowSize, float framebufferScale, float deltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(input);
        var blocked = false;
        foreach (var context in _contexts.Where(c => c.Visible).OrderByDescending(c => c.SortOrder).ToList())
        {
            context.Update(input.ForUI, windowSize, framebufferScale, deltaSeconds, pointerBlocked: blocked);
            blocked |= context.WantsMouse;
            input.CaptureForUI(context.WantsKeyboard, context.WantsMouse);
        }
    }

    /// <summary>Draws all visible contexts, bottom to top, into the current target.</summary>
    public void Render(UIRenderer renderer, int framebufferWidth, int framebufferHeight)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        foreach (var context in _contexts.Where(c => c.Visible).OrderBy(c => c.SortOrder).ToList())
        {
            renderer.Render(context, framebufferWidth, framebufferHeight);
        }
    }

    /// <summary>Totals over all contexts, for diagnostics.</summary>
    public (int Contexts, int Elements, int Visible, int Quads, int DrawCalls, long LayoutPasses, long Rebuilds) Totals() =>
        (_contexts.Count, _contexts.Sum(c => c.Statistics.Elements), _contexts.Sum(c => c.Statistics.VisibleElements), _contexts.Sum(c => c.Statistics.Quads),
         _contexts.Sum(c => c.Statistics.DrawCalls), _contexts.Sum(c => c.Statistics.LayoutPasses), _contexts.Sum(c => c.Statistics.GeometryRebuilds));

    internal void Remove(UIContext context) => _contexts.Remove(context);

    private void Dispose()
    {
        foreach (var context in _contexts.ToList())
        {
            context.Dispose();
        }

        foreach (var task in _fonts.Values.Where(t => t.IsCompletedSuccessfully))
        {
            task.Result.Dispose();
        }

        _fonts.Clear();
    }
}

/// <summary>Game-code entry points to the UI of the running scene.</summary>
public static class UI
{
    /// <summary>Creates a UI context in the current scene. Dispose it (e.g. in <c>OnDestroy</c>) to remove it.</summary>
    /// <exception cref="InvalidOperationException">Outside scene callbacks, or the host attached no UI system.</exception>
    public static UIContext CreateContext() => System.CreateContext();

    /// <summary>Loads a font asset (TTF or OTF) without blocking the frame.</summary>
    public static Task<Font> LoadFontAsync(AssetRef font) => System.LoadFontAsync(font);

    private static UISystem System => UISystem.For(SceneManager.ActiveScene)
        ?? throw new InvalidOperationException("This scene has no UI system; the game host attaches one when the scene loads.");
}
