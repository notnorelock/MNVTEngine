// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using MNVTEngine.Mathematics;
using MNVTEngine.Reactivity;

namespace MNVTEngine.UI;

public partial class UIElement
{
    private ReactiveScope? _scope;
    private List<UIAnimation>? _animations;
    private Vector2 _translate;
    private float _renderScale = 1f;
    private Dictionary<string, string>? _dataBindings;

    /// <summary>
    /// Owns this element's bindings, effects and cleanups (see <see cref="Bindings"/>). Created on first use; disposed
    /// with the element.
    /// </summary>
    public ReactiveScope Scope
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return _scope ??= Reactive.CreateScope(detached: true);
        }
    }

    /// <summary>True after <see cref="Dispose"/>: the element is out of its tree and its bindings have stopped.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// Visual offset in logical pixels, applied after layout to this element and its children. Moving it lays nothing
    /// out; hit testing follows it. Used for slides and animated positions.
    /// </summary>
    public Vector2 Translate { get => _translate; set => SetVisual(ref _translate, value); }

    /// <summary>Visual scale around the element's centre (1 = none), applied after layout; hit testing follows it.</summary>
    public float RenderScale { get => _renderScale; set => SetVisual(ref _renderScale, MathF.Max(0f, value)); }

    /// <summary>
    /// Data bindings of a layout document (its <c>bind</c> block): property name to data name, resolved by
    /// <see cref="UIData"/>.
    /// </summary>
    public IDictionary<string, string> DataBindings => _dataBindings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    internal bool HasDataBindings => _dataBindings is { Count: > 0 };

    /// <summary>
    /// A point in context coordinates (where the pointer is) in this element's layout space (where its
    /// <see cref="Bounds"/> are), undoing the <see cref="Translate"/> and <see cref="RenderScale"/> of the element and
    /// its ancestors. NaN when a scale of zero hides it.
    /// </summary>
    public Vector2 ToLayout(Vector2 contextPoint) => UndoOwnTransform(Parent is { } parent ? parent.ToLayout(contextPoint) : contextPoint);

    /// <summary>The inverse of <see cref="ToLayout"/>: where a point of this element's layout space is drawn.</summary>
    public Vector2 ToContext(Vector2 layoutPoint)
    {
        var drawn = HasTransform ? Bounds.Center + ((layoutPoint - Bounds.Center) * RenderScale) + Translate : layoutPoint;
        return Parent is { } parent ? parent.ToContext(drawn) : drawn;
    }

    internal bool HasTransform => _translate != Vector2.Zero || _renderScale != 1f;

    /// <summary>A point in the parent's layout space into this element's (its own transform undone).</summary>
    internal Vector2 UndoOwnTransform(Vector2 point)
    {
        if (!HasTransform)
        {
            return point;
        }

        if (_renderScale <= 1e-6f)
        {
            return new Vector2(float.NaN, float.NaN);
        }

        var pivot = Bounds.Center;
        return pivot + ((point - _translate - pivot) / _renderScale);
    }

    /// <summary>
    /// Removes the element from its parent and disposes it and its descendants: their bindings, effects and animations
    /// stop and their cleanups run. Use it for elements that will not come back; <see cref="RemoveChild"/> keeps an
    /// element reusable.
    /// </summary>
    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        RemoveFromParent();
        DisposeTree();
    }

    /// <summary>Runs <paramref name="cleanup"/> when the element is disposed.</summary>
    public void OnCleanup(Action cleanup) => Scope.OnCleanup(cleanup);

    internal IReadOnlyList<UIAnimation>? Animations => _animations;

    internal void AddAnimation(UIAnimation animation)
    {
        _animations ??= [];
        _animations.RemoveAll(a => a.Property == animation.Property); // a new animation of a property replaces the running one
        _animations.Add(animation);
        Context?.MarkVisualDirty();
    }

    internal void StopAnimations(string? property = null)
    {
        if (property is null)
        {
            _animations?.Clear();
        }
        else
        {
            _animations?.RemoveAll(a => a.Property == property);
        }
    }

    /// <summary>Steps running animations; true while any changed the element.</summary>
    internal bool StepAnimations(float deltaSeconds)
    {
        if (_animations is not { Count: > 0 } animations)
        {
            return false;
        }

        // A step can change the list (an After action starts, replaces or stops animations of this element): step a
        // snapshot, skip what was removed meanwhile, and remove finished ones by reference, not by a stale index.
        foreach (var animation in animations.ToArray())
        {
            if (animations.Contains(animation) && !animation.Step(deltaSeconds))
            {
                animations.Remove(animation);
            }
        }

        return true;
    }

    private void DisposeTree()
    {
        IsDisposed = true;
        OnDisposed();
        while (_children.Count > 0)
        {
            var child = _children[^1];
            RemoveChild(child);
            child.DisposeTree();
        }

        _animations?.Clear();
        _scope?.Dispose();
        _scope = null;
    }

    /// <summary>Called once when the element is disposed, before its children and its scope.</summary>
    protected virtual void OnDisposed()
    {
    }
}
