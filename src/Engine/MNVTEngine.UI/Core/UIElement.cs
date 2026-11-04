using MNVTEngine.Graphics.Text;
using MNVTEngine.Mathematics;

namespace MNVTEngine.UI;

/// <summary>
/// Base of everything in a UI tree. It has a parent and children, layout properties, a style, interaction state and
/// event hooks. Changing a property marks the context's layout or geometry dirty: nothing is recomputed until the next
/// update, and only when something changed.
/// <para>
/// Coordinates are logical pixels with the origin at the top-left of the UI viewport, y down. The context maps them to
/// window pixels with its scale (see <see cref="UIContext.Scale"/>).
/// </para>
/// </summary>
public partial class UIElement
{
    private readonly List<UIElement> _children = [];
    private UIUnit _width, _height, _left, _top;
    private float _minWidth, _minHeight, _maxWidth = float.PositiveInfinity, _maxHeight = float.PositiveInfinity;
    private Thickness _margin;
    private Vector2 _anchor, _pivot;
    private LayoutMode _layout;
    private float _gap, _flexGrow;
    private Justify _justify;
    private Align _alignItems = Align.Stretch;
    private Align? _alignSelf;
    private int _gridColumns = 2, _zIndex;
    private Overflow _overflow;
    private bool _visible = true, _enabled = true;
    private string? _styleClass;

    public string Name { get; set; } = "";

    public UIElement? Parent { get; private set; }

    public IReadOnlyList<UIElement> Children => _children;

    /// <summary>The context whose tree contains this element; null while detached.</summary>
    public UIContext? Context { get; private set; }

    // Layout

    public UIUnit Width { get => _width; set => SetLayout(ref _width, value); }

    public UIUnit Height { get => _height; set => SetLayout(ref _height, value); }

    public float MinWidth { get => _minWidth; set => SetLayout(ref _minWidth, value); }

    public float MinHeight { get => _minHeight; set => SetLayout(ref _minHeight, value); }

    public float MaxWidth { get => _maxWidth; set => SetLayout(ref _maxWidth, value); }

    public float MaxHeight { get => _maxHeight; set => SetLayout(ref _maxHeight, value); }

    public Thickness Margin { get => _margin; set => SetLayout(ref _margin, value); }

    /// <summary>Offset from the anchor point, for children of <see cref="LayoutMode.Absolute"/> parents.</summary>
    public UIUnit Left { get => _left; set => SetLayout(ref _left, value); }

    public UIUnit Top { get => _top; set => SetLayout(ref _top, value); }

    /// <summary>Point of the parent's content box the element is placed at: (0,0) top-left, (0.5,0.5) center, (1,1) bottom-right.</summary>
    public Vector2 Anchor { get => _anchor; set => SetLayout(ref _anchor, value); }

    /// <summary>Point of the element that sits on the anchor: (0.5,0.5) centers it there.</summary>
    public Vector2 Pivot { get => _pivot; set => SetLayout(ref _pivot, value); }

    /// <summary>How this element places its children.</summary>
    public LayoutMode Layout { get => _layout; set => SetLayout(ref _layout, value); }

    /// <summary>Space between children of rows, columns and grids.</summary>
    public float Gap { get => _gap; set => SetLayout(ref _gap, value); }

    public Justify Justify { get => _justify; set => SetLayout(ref _justify, value); }

    /// <summary>Cross-axis placement of children of rows and columns.</summary>
    public Align AlignItems { get => _alignItems; set => SetLayout(ref _alignItems, value); }

    /// <summary>Overrides the parent's <see cref="AlignItems"/> for this element.</summary>
    public Align? AlignSelf { get => _alignSelf; set => SetLayout(ref _alignSelf, value); }

    /// <summary>Share of a row's or column's free space this element takes (0 = none).</summary>
    public float FlexGrow { get => _flexGrow; set => SetLayout(ref _flexGrow, value); }

    public int GridColumns { get => _gridColumns; set => SetLayout(ref _gridColumns, Math.Max(1, value)); }

    public Overflow Overflow { get => _overflow; set => SetLayout(ref _overflow, value); }

    /// <summary>Order among siblings for drawing and hit testing; higher is on top. Equal values keep child order.</summary>
    public int ZIndex { get => _zIndex; set => SetLayout(ref _zIndex, value); }

    public bool Visible { get => _visible; set => SetLayout(ref _visible, value); }

    /// <summary>Disabled elements (and their descendants) ignore input and use the disabled style.</summary>
    public bool Enabled { get => _enabled; set => SetVisual(ref _enabled, value); }

    /// <summary>True when this element and all its ancestors are enabled.</summary>
    public bool IsEnabledInHierarchy => Enabled && (Parent?.IsEnabledInHierarchy ?? true);

    /// <summary>Whether the element can take keyboard focus (by click or Tab).</summary>
    public virtual bool Focusable { get; set; }

    /// <summary>Tab order; elements with equal values follow tree order.</summary>
    public int TabIndex { get; set; }

    /// <summary>Shown after the pointer rests on the element for <see cref="UIContext.TooltipDelay"/>.</summary>
    public string? Tooltip { get; set; }

    /// <summary>Receives pointer events; null decides by type (containers without a background let the pointer through).</summary>
    public bool? HitTestVisible { get; set; }

    /// <summary>Name of an action raised through <see cref="UIContext.ActionInvoked"/> when the element is activated (serializable, unlike delegates).</summary>
    public string? Action { get; set; }

    // Style

    /// <summary>Space-separated theme classes, e.g. "primary large" (theme selectors ".primary").</summary>
    public string? StyleClass { get => _styleClass; set => SetVisual(ref _styleClass, value); }

    /// <summary>This element's own style, over the theme.</summary>
    public UIStyle Style { get; } = new();

    /// <summary>This element's own style per state, over everything else.</summary>
    public Dictionary<UIState, UIStyle> StateStyles { get; } = [];

    public Color? BackgroundColor { get => Style.Background; set { Style.Background = value; InvalidateVisual(); } }

    public Color? TextColor { get => Style.Foreground; set { Style.Foreground = value; InvalidateVisual(); } }

    public Color? BorderColor { get => Style.BorderColor; set { Style.BorderColor = value; InvalidateVisual(); } }

    public float? BorderWidth { get => Style.BorderWidth; set { Style.BorderWidth = value; InvalidateLayout(); } }

    public float? CornerRadius { get => Style.CornerRadius; set { Style.CornerRadius = value; InvalidateVisual(); } }

    public Thickness? Padding { get => Style.Padding; set { Style.Padding = value; InvalidateLayout(); } }

    public Font? Font { get => Style.Font; set { Style.Font = value; InvalidateLayout(); } }

    public float? FontSize { get => Style.FontSize; set { Style.FontSize = value; InvalidateLayout(); } }

    public float? Opacity { get => Style.Opacity; set { Style.Opacity = value; InvalidateVisual(); } }

    // Results of layout (logical pixels, context coordinates)

    /// <summary>Border box after the last layout.</summary>
    public Rect Bounds { get; internal set; }

    /// <summary>Bounds minus border and padding: where children and content go.</summary>
    public Rect ContentBounds { get; internal set; }

    /// <summary>The visible part of the element after ancestors' clipping (where it can be hit).</summary>
    public Rect ClipBounds { get; internal set; }

    /// <summary>The clip the element's own drawing uses (its parent's child clip).</summary>
    internal Rect PaintClip { get; set; }

    /// <summary>The clip its children draw within: <see cref="ClipBounds"/> with <see cref="Overflow.Clip"/>, else the parent's.</summary>
    internal Rect ChildClip { get; set; }

    /// <summary>Hover, pressed, focused, disabled, checked.</summary>
    public UIState State
    {
        get
        {
            var state = UIState.Normal;
            if (!IsEnabledInHierarchy)
            {
                return UIState.Disabled | (IsChecked ? UIState.Checked : 0);
            }

            if (Context is { } context)
            {
                state |= context.Hovered == this ? UIState.Hover : 0;
                state |= context.Pressed == this ? UIState.Pressed : 0;
                state |= context.Focused == this ? UIState.Focused : 0;
            }

            return state | (IsChecked ? UIState.Checked : 0);
        }
    }

    /// <summary>For toggles and radio buttons: drives the <c>:checked</c> style.</summary>
    protected virtual bool IsChecked => false;

    /// <summary>Theme type selector; the class name unless overridden.</summary>
    protected virtual string StyleType => GetType().Name;

    /// <summary>Default for <see cref="HitTestVisible"/>: interactive widgets and elements with a background.</summary>
    protected virtual bool DefaultHitTestVisible => Focusable || (Style.Background is { A: > 0f });

    /// <summary>True while focused text input should take the keyboard from gameplay.</summary>
    public virtual bool WantsKeyboard => false;

    /// <summary>Adds <paramref name="child"/> as the last child (moving it from any previous parent).</summary>
    public T AddChild<T>(T child)
        where T : UIElement => InsertChild(_children.Count, child);

    public T InsertChild<T>(int index, T child)
        where T : UIElement
    {
        ArgumentNullException.ThrowIfNull(child);
        for (var a = this; a is not null; a = a.Parent)
        {
            if (a == child)
            {
                throw new InvalidOperationException($"'{child.Name}' cannot be added to itself or its own descendant.");
            }
        }

        child.RemoveFromParent();
        _children.Insert(Math.Clamp(index, 0, _children.Count), child);
        child.Parent = this;
        child.Attach(Context);
        InvalidateLayout();
        return child;
    }

    public bool RemoveChild(UIElement child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (child.Parent != this)
        {
            return false;
        }

        _children.Remove(child);
        child.Parent = null;
        child.Attach(null);
        InvalidateLayout();
        return true;
    }

    public void RemoveFromParent() => Parent?.RemoveChild(this);

    public void ClearChildren()
    {
        while (_children.Count > 0)
        {
            RemoveChild(_children[^1]);
        }
    }

    /// <summary>Moves this element among its siblings (draw and tab order).</summary>
    public void SetSiblingIndex(int index)
    {
        if (Parent is { } parent)
        {
            parent._children.Remove(this);
            parent._children.Insert(Math.Clamp(index, 0, parent._children.Count), this);
            parent.InvalidateLayout();
        }
    }

    /// <summary>The first descendant (depth first) with this name, or null.</summary>
    public UIElement? Find(string name)
    {
        foreach (var child in _children)
        {
            if (child.Name == name)
            {
                return child;
            }

            if (child.Find(name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    public T? Find<T>(string name)
        where T : UIElement => Find(name) as T;

    /// <summary>This element and every descendant, depth first.</summary>
    public IEnumerable<UIElement> Descendants()
    {
        yield return this;
        foreach (var child in _children)
        {
            foreach (var d in child.Descendants())
            {
                yield return d;
            }
        }
    }

    /// <summary>Gives this element keyboard focus (if it is focusable, visible and enabled).</summary>
    public void Focus() => Context?.SetFocus(this);

    /// <summary>The style for the current state, layered from the theme, classes and this element.</summary>
    public ResolvedStyle ResolveStyle() => (Context?.Theme ?? UITheme.Dark).Resolve(this, StyleType, State, Context?.DefaultFont);

    public void InvalidateLayout() => Context?.MarkLayoutDirty();

    public void InvalidateVisual() => Context?.MarkVisualDirty();

    public override string ToString() => $"{GetType().Name} '{Name}' {Bounds}";

    internal bool IsHitTestVisible => HitTestVisible ?? DefaultHitTestVisible;

    internal IEnumerable<UIElement> ChildrenInDrawOrder() =>
        _children.Count < 2 || _children.All(c => c.ZIndex == _children[0].ZIndex) ? _children : _children.OrderBy(c => c.ZIndex);

    internal void Attach(UIContext? context)
    {
        if (Context == context)
        {
            return;
        }

        Context?.OnDetached(this);
        Context = context;
        foreach (var child in _children)
        {
            child.Attach(context);
        }

        OnAttached();
    }

    /// <summary>Called when the element joins or leaves a context tree.</summary>
    protected virtual void OnAttached()
    {
    }

    protected void SetLayout<T>(ref T field, T value)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            InvalidateLayout();
        }
    }

    protected void SetVisual<T>(ref T field, T value)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            InvalidateVisual();
        }
    }
}
