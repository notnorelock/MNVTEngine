// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

using MNVTEngine.Reactivity;

namespace MNVTEngine.UI;

/// <summary>Where asynchronously loaded content is; its owner (the page) sets it, <see cref="LoadingRegion"/> shows it.</summary>
public enum LoadState
{
    Initial,
    Loading,

    /// <summary>Some of it is here and more is coming: the content shows (it may end in skeleton rows).</summary>
    PartiallyLoaded,
    Loaded,
    Empty,
    Failed,
}

/// <summary>
/// Shows loading content by its <see cref="LoadState"/>: a skeleton while loading, then the content (fading in), an
/// empty state or a failure. It fetches nothing; the owner sets the state. A load that finishes within
/// <see cref="ShowAfterSeconds"/> never shows the skeleton (no one-frame flash); a skeleton that did show stays
/// <see cref="MinimumSeconds"/> so it does not flicker. Reloading over shown content keeps the content. Built with
/// <see cref="V.Loading"/>; a column, sized by what it shows.
/// </summary>
public sealed class LoadingRegion : UIElement
{
    private enum View { None, Skeleton, Content, Empty, Failed }

    private readonly Func<LoadState> _state;
    private readonly Func<View, UIElement?> _build;
    private ReactiveScope? _scope;
    private UIElement? _content;
    private View _shown;
    private LoadState _target;
    private float _waited, _skeletonFor;

    public LoadingRegion(Func<LoadState> state, Func<UIElement> skeleton, Func<UIElement> content, Func<UIElement>? empty, Func<UIElement>? failed)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(content);
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _build = view => view switch
        {
            View.Skeleton => skeleton(),
            View.Content => content(),
            View.Empty => empty?.Invoke() ?? V.EmptyState(Icons.Inbox, "Nothing here yet"),
            View.Failed => failed?.Invoke() ?? V.EmptyState(Icons.CircleAlert, "Could not load this", "Check your connection and try again."),
            _ => null,
        };
        Name = "Loading";
        Layout = LayoutMode.Column;
        this.Effect(() =>
        {
            _target = _state();
            _waited = 0f;
            Reactive.Untrack(Advance);
        }, "Loading");
    }

    /// <summary>How long a load runs before its skeleton shows.</summary>
    public float ShowAfterSeconds { get; set; } = 0.12f;

    /// <summary>How long a shown skeleton stays at least.</summary>
    public float MinimumSeconds { get; set; } = 0.35f;

    /// <summary>Seconds of fade-in for what replaces the skeleton.</summary>
    public float FadeSeconds { get; set; } = 0.25f;

    protected internal override bool Tick(float deltaSeconds)
    {
        _waited += deltaSeconds;
        _skeletonFor += deltaSeconds;
        var before = _shown;
        Advance();
        return before != _shown;
    }

    protected override void OnDisposed()
    {
        _scope?.Dispose();
        _scope = null;
    }

    private void Advance()
    {
        var wanted = _target switch
        {
            LoadState.Loaded or LoadState.PartiallyLoaded => View.Content,
            LoadState.Empty => View.Empty,
            LoadState.Failed => View.Failed,
            _ when _shown is View.Content or View.Skeleton => _shown, // a reload keeps what is there
            _ => _waited >= ShowAfterSeconds ? View.Skeleton : View.None,
        };

        if (wanted == _shown || (_shown == View.Skeleton && _skeletonFor < MinimumSeconds && Context is not null))
        {
            return;
        }

        Show(wanted);
    }

    private void Show(View view)
    {
        _content?.Dispose();
        _scope?.Dispose();
        (_shown, _skeletonFor) = (view, 0f);
        _scope = Reactive.CreateScope(detached: true);
        _content = _scope.Run(() => _build(view));
        if (_content is null)
        {
            return;
        }

        AddChild(_content);
        if (view != View.Skeleton && Context is not null && FadeSeconds > 0f)
        {
            _content.Opacity = 0f;
            _content.AnimateOpacity(1f, Motion.Tween(FadeSeconds));
        }
    }
}
