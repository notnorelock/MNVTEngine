// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

using MNVTEngine.Graphics.Text;
using MNVTEngine.Graphics;
using MNVTEngine.Mathematics;
using MNVTEngine.Reactivity;
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
    private UISystem? _system;
    private readonly UIInputRouter _input;
    private UITheme _theme = UITheme.Dark;
    private float _scale = 1f;
    private readonly List<UIElement> _ticking = [];
    private bool _layoutDirty = true;
    private ReactiveScope? _mountScope;
    private UIElement? _mounted;

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

    /// <summary>
    /// Brings work from other threads onto the UI: posted actions run at the start of the next <see cref="Update"/>,
    /// in one reactive batch.
    /// </summary>
    public ReactiveDispatcher Dispatcher { get; } = new();

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

    /// <summary>
    /// Window pixels per logical pixel: the zoom set here times the fit to the window's width (<see cref="ReferenceWidth"/>).
    /// 2 makes everything twice as large. Reads give the effective scale.
    /// </summary>
    public float Scale
    {
        get => _scale * _fit;
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

    /// <summary>
    /// While set, Tab and Shift+Tab move focus only among its descendants (a modal dialog). Opening a dialog sets it;
    /// closing restores the previous scope.
    /// </summary>
    public UIElement? FocusScope { get; set; }

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

    /// <summary>Seconds of UI time since the context was made: one clock for effects that must stay in step (skeleton shimmer).</summary>
    public double Time { get; private set; }

    /// <param name="pointerBlocked">A context above already took the pointer: hover and clicks are off for this one.</param>
    internal void Update(InputView input, Vector2 windowSize, float framebufferScale, float deltaSeconds, bool pointerBlocked)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Time += Math.Max(0f, deltaSeconds);
        Dispatcher.Drain();
        _fit = FitScale(windowSize.X, ReferenceWidth, MinFit, MaxFit);
        var size = windowSize / Scale;
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

        _ticking.Clear(); // one list kept across frames: animations and ticks may change the tree while it runs
        Collect(Root, _ticking);
        Collect(Overlay, _ticking);
        foreach (var element in _ticking)
        {
            if (element.Tick(deltaSeconds) | element.StepAnimations(deltaSeconds))
            {
                MarkVisualDirty();
            }
        }

        _ticking.Clear(); // let removed elements go
    }

    /// <summary>The element and everything under it, like <see cref="UIElement.Descendants"/> without its iterators.</summary>
    private static void Collect(UIElement element, List<UIElement> into)
    {
        into.Add(element);
        foreach (var child in element.Children)
        {
            Collect(child, into);
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

    /// <summary>
    /// Whether focus came from the keyboard (Tab) rather than a click: focus rings show only then, so clicking a switch
    /// does not leave it outlined.
    /// </summary>
    public bool FocusVisible { get; internal set; }

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
        Root.Dispose(); // stops every binding, effect and animation in the trees
        Overlay.Dispose();
    }

    /// <summary>
    /// Builds content into <see cref="Root"/> (replacing what <see cref="Mount"/> built before) inside a reactive scope
    /// owned by the root, so effects and computed values made while building stop with the context:
    /// <code>ui.Mount(() => V.Column(V.Title("Settings"), V.Switch(music, "Music")));</code>
    /// </summary>
    public UIElement Mount(Func<UIElement> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _mounted?.Dispose();
        _mountScope?.Dispose();
        _mountScope = Reactive.CreateScope(detached: true);
        Root.OnCleanup(_mountScope.Dispose);
        _mounted = _mountScope.Run(build);
        return Root.AddChild(_mounted);
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

    /// <summary>Moves this context to another scene's UI system (a DontDestroyOnLoad UIDocument).</summary>
    internal void MoveTo(UISystem target)
    {
        _system?.Remove(this);
        _system = target;
        target.Add(this);
    }

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

        point = element.UndoOwnTransform(point); // into the element's layout space (it may be moved or scaled)
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
