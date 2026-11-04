using MNVTEngine.Graphics.Text;
using MNVTEngine.Graphics;
using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;

namespace MNVTEngine.UI;

/// <summary>
/// One independent UI (a menu, a HUD, a loading screen): an element tree with a theme, focus, hover and pointer
/// capture. <see cref="Root"/> fills the viewport; <see cref="Overlay"/> is drawn above it (dropdown lists, tooltips).
/// Layout runs only when something changed, and geometry is rebuilt only when something visible changed.
/// <para>
/// Coordinates: logical pixels, origin at the top-left of the game viewport, y down. One logical pixel is
/// <see cref="Scale"/> window pixels (and <see cref="Scale"/> x <see cref="FramebufferScale"/> framebuffer pixels on
/// high-DPI displays), so the logical size is the window size divided by <see cref="Scale"/>.
/// </para>
/// </summary>
public sealed partial class UIContext : IDisposable
{
    private readonly UISystem? _system;
    private readonly UIInputRouter _input;
    private UITheme _theme = UITheme.Dark;
    private float _scale = 1f;
    private bool _layoutDirty = true;

    /// <summary>A context outside any scene (tests, loading screens). Scenes create theirs with <see cref="UI.CreateContext"/>.</summary>
    public UIContext(Font defaultFont)
        : this(null, defaultFont)
    {
    }

    internal UIContext(UISystem? system, Font defaultFont)
    {
        ArgumentNullException.ThrowIfNull(defaultFont);
        _system = system;
        DefaultFont = defaultFont;
        _input = new UIInputRouter(this);
        Root = new UIElement { Name = "Root", Width = UIUnit.Percent(100), Height = UIUnit.Percent(100) };
        Overlay = new UIElement { Name = "Overlay", Width = UIUnit.Percent(100), Height = UIUnit.Percent(100) };
        Root.Attach(this);
        Overlay.Attach(this);
    }

    public UIElement Root { get; }

    /// <summary>Drawn above <see cref="Root"/>: popups and tooltips.</summary>
    public UIElement Overlay { get; }

    public UITheme Theme
    {
        get => _theme;
        set
        {
            _theme = value ?? throw new ArgumentNullException(nameof(value));
            MarkLayoutDirty();
        }
    }

    /// <summary>Window pixels per logical pixel (UI zoom). 2 makes everything twice as large.</summary>
    public float Scale
    {
        get => _scale;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _scale = value;
            MarkLayoutDirty();
        }
    }

    /// <summary>Framebuffer pixels per window pixel (above 1 on high-DPI displays); set by the host each frame.</summary>
    public float FramebufferScale { get; private set; } = 1f;

    /// <summary>Logical size of the viewport (window size / <see cref="Scale"/>).</summary>
    public Vector2 Size { get; private set; }

    /// <summary>Used by text without a font set (normally the engine's built-in font).</summary>
    public Font DefaultFont { get; }

    /// <summary>Drawing order among contexts of a scene; higher draws on top and gets input first.</summary>
    public int SortOrder { get; set; }

    /// <summary>Hidden contexts are neither drawn nor given input.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>System clipboard for text fields; null disables copy and paste.</summary>
    public IClipboard? Clipboard { get; set; }

    /// <summary>Seconds the pointer rests on an element before its tooltip shows.</summary>
    public float TooltipDelay { get; set; } = 0.6f;

    public UIElement? Focused { get; private set; }

    public UIElement? Hovered { get; internal set; }

    /// <summary>The element pressed and holding the pointer capture until release.</summary>
    public UIElement? Pressed { get; internal set; }

    /// <summary>Raised when an element with an <see cref="UIElement.Action"/> is activated: the serializable way to bind UI to game code.</summary>
    public event Action<UIElement, string>? ActionInvoked;

    /// <summary>True while a focused element takes the keyboard (text input).</summary>
    public bool WantsKeyboard => Focused is { WantsKeyboard: true };

    /// <summary>True while the pointer is over an element that takes it, or an element holds the pointer capture.</summary>
    public bool WantsMouse { get; internal set; }

    public UIStatistics Statistics { get; } = new();

    internal bool VisualDirty { get; set; } = true;

    public bool IsDisposed { get; private set; }

    /// <summary>
    /// Runs one frame: layout when dirty, then input (hover, clicks, focus, keys, text), then element ticks. Called by
    /// <see cref="UISystem"/> (or directly for a standalone context).
    /// </summary>
    /// <param name="windowSize">Viewport size in window pixels.</param>
    /// <param name="framebufferScale">Framebuffer pixels per window pixel.</param>
    public void Update(InputView input, Vector2 windowSize, float framebufferScale, float deltaSeconds) =>
        Update(input, windowSize, framebufferScale, deltaSeconds, pointerBlocked: false);

    /// <param name="pointerBlocked">A context above already took the pointer: hover and clicks are off for this one.</param>
    internal void Update(InputView input, Vector2 windowSize, float framebufferScale, float deltaSeconds, bool pointerBlocked)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var size = windowSize / _scale;
        if (size != Size || framebufferScale != FramebufferScale)
        {
            (Size, FramebufferScale) = (size, framebufferScale);
            MarkLayoutDirty();
        }

        LayoutIfDirty();
        if (Visible)
        {
            _input.Process(input, deltaSeconds, pointerBlocked);
            LayoutIfDirty(); // input may have changed the tree (opened a dropdown, typed text)
        }

        foreach (var element in Root.Descendants().Concat(Overlay.Descendants()))
        {
            if (element.Tick(deltaSeconds))
            {
                MarkVisualDirty();
            }
        }
    }

    /// <summary>Recomputes layout now if anything changed (normally done by <see cref="Update"/>).</summary>
    public void LayoutIfDirty()
    {
        if (!_layoutDirty)
        {
            return;
        }

        _layoutDirty = false;
        var viewport = new Rect(0, 0, Size.X, Size.Y);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        LayoutEngine.Layout(Root, viewport);
        LayoutEngine.Layout(Overlay, viewport);
        Statistics.LayoutPasses++;
        Statistics.LastLayoutMilliseconds = watch.Elapsed.TotalMilliseconds;
        Statistics.Elements = Root.Descendants().Count() + Overlay.Descendants().Count(); // structure changes dirty layout
        VisualDirty = true;
    }

    /// <summary>Moves keyboard focus; null clears it.</summary>
    public void SetFocus(UIElement? element)
    {
        if (element is not null && (element.Context != this || !element.Focusable || !element.IsEnabledInHierarchy || !IsShown(element)))
        {
            return;
        }

        if (Focused == element)
        {
            return;
        }

        var previous = Focused;
        Focused = element;
        previous?.OnBlur();
        element?.OnFocus();
        MarkVisualDirty();
    }

    /// <summary>The topmost element under <paramref name="point"/> (logical coordinates) that takes the pointer, or null.</summary>
    public UIElement? HitTest(Vector2 point) => HitTest(Overlay, point) ?? HitTest(Root, point);

    /// <summary>Subscribes to one action name (see <see cref="UIElement.Action"/>).</summary>
    public void OnAction(string action, Action<UIElement> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ActionInvoked += (element, name) =>
        {
            if (name == action)
            {
                handler(element);
            }
        };
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        SetFocus(null);
        _system?.Remove(this);
        Root.Attach(null);
        Overlay.Attach(null);
    }

    internal void MarkLayoutDirty()
    {
        _layoutDirty = true;
        VisualDirty = true;
    }

    internal void MarkVisualDirty() => VisualDirty = true;

    internal void RaiseAction(UIElement element, string action)
    {
        if (ActionInvoked is { } handler)
        {
            element.RaiseEvent($"action '{action}'", () => handler(element, action));
        }
    }

    /// <summary>The scene whose context game event handlers run in; null for standalone contexts.</summary>
    internal Scene? Scene => _system?.Scene;

    /// <summary>A texture asset through the scene's UI services (null without a UI system or when missing).</summary>
    internal Texture2D? LoadTexture(AssetRef asset) => _system?.Services.LoadTexture?.Invoke(asset);

    internal void OnDetached(UIElement element)
    {
        if (Focused is { } focused && IsSelfOrAncestor(element, focused))
        {
            SetFocus(null);
        }

        if (Hovered is { } hovered && IsSelfOrAncestor(element, hovered))
        {
            Hovered = null;
        }

        if (Pressed is { } pressed && IsSelfOrAncestor(element, pressed))
        {
            Pressed = null;
        }
    }

    internal static bool IsShown(UIElement element)
    {
        for (var e = element; e is not null; e = e.Parent)
        {
            if (!e.Visible)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsSelfOrAncestor(UIElement ancestor, UIElement element)
    {
        for (var e = element; e is not null; e = e.Parent)
        {
            if (e == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    private static UIElement? HitTest(UIElement element, Vector2 point)
    {
        if (!element.Visible)
        {
            return null;
        }

        var children = element.ChildrenInDrawOrder().ToList();
        for (var i = children.Count - 1; i >= 0; i--)
        {
            if (HitTest(children[i], point) is { } hit)
            {
                return hit;
            }
        }

        return element.IsHitTestVisible && element.ClipBounds.Contains(point) && element.ClipBounds.Width > 0 ? element : null;
    }
}

/// <summary>Counters of one context, for diagnostics.</summary>
public sealed class UIStatistics
{
    public long LayoutPasses { get; internal set; }

    public double LastLayoutMilliseconds { get; internal set; }

    public long GeometryRebuilds { get; internal set; }

    public int Elements { get; internal set; }

    public int VisibleElements { get; internal set; }

    public int Quads { get; internal set; }

    public int DrawCalls { get; internal set; }

    public int TextureSwitches { get; internal set; }
}
