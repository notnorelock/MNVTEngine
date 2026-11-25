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

    /// <summary>Visual scale around the element's centre (1 = none), applied after layout; hit testing ignores it.</summary>
    public float RenderScale { get => _renderScale; set => SetVisual(ref _renderScale, MathF.Max(0f, value)); }

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

        for (var i = animations.Count - 1; i >= 0; i--)
        {
            if (!animations[i].Step(deltaSeconds) && i < animations.Count)
            {
                animations.RemoveAt(i);
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
