// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using MNVTEngine.Reactivity;

namespace MNVTEngine.UI;

/// <summary>
/// Shows one of two branches by a condition. Only the shown branch exists: switching disposes the old one (its
/// bindings and resources) and builds the new one once. A condition rerunning with the same answer does nothing. The
/// region takes no space while it shows nothing. Build branch content inside the lambdas.
/// </summary>
public sealed class ShowRegion : UIElement
{
    private readonly Func<bool> _condition;
    private readonly Func<UIElement>? _then, _otherwise;
    private ReactiveScope? _branchScope;
    private int _branch = -1;

    public ShowRegion(Func<bool> condition, Func<UIElement>? then, Func<UIElement>? otherwise)
    {
        _condition = condition ?? throw new ArgumentNullException(nameof(condition));
        (_then, _otherwise) = (then, otherwise);
        Name = "Show";
        Layout = LayoutMode.Column;
        Visible = false;
        this.Effect(Update, "Show");
    }

    /// <summary>How many times a branch was built (an unchanged condition builds nothing).</summary>
    public int Builds { get; private set; }

    protected override void OnDisposed()
    {
        _branchScope?.Dispose();
        _branchScope = null;
    }

    private void Update()
    {
        var branch = _condition() ? 1 : 0;
        if (branch == _branch)
        {
            return;
        }

        _branch = branch;
        Reactive.Untrack(Swap);
    }

    private void Swap()
    {
        foreach (var old in Children.ToList())
        {
            old.Dispose();
        }

        _branchScope?.Dispose();
        _branchScope = null;
        var build = _branch == 1 ? _then : _otherwise;
        UIElement? content = null;
        if (build is not null)
        {
            _branchScope = Reactive.CreateScope(detached: true);
            content = _branchScope.Run(build);
            Builds++;
        }

        if (content is not null)
        {
            AddChild(content);
        }

        Visible = content is not null;
    }
}

/// <summary>
/// Builds its content from a value and rebuilds it whenever the value changes (by
/// <see cref="EqualityComparer{T}.Default"/>): a loaded result, the current screen. The old content and its bindings
/// are disposed first, or after fading out with <see cref="Crossfade"/>. A null build result shows nothing.
/// </summary>
public sealed class KeyedRegion<T> : UIElement
{
    private readonly Func<T> _key;
    private readonly Func<T, UIElement?> _build;
    private ReactiveScope? _scope, _leavingScope;
    private UIElement? _content, _leaving;
    private T _current = default!;
    private bool _built;
    private float _out, _in;

    public KeyedRegion(Func<T> key, Func<T, UIElement?> build)
    {
        _key = key ?? throw new ArgumentNullException(nameof(key));
        _build = build ?? throw new ArgumentNullException(nameof(build));
        Name = "Keyed";
        Layout = LayoutMode.Absolute;
        this.Effect(Update, "Keyed");
    }

    public int Builds { get; private set; }

    /// <summary>
    /// Changes fade: the old content fades out over <paramref name="outSeconds"/> and stops taking the pointer at once;
    /// the new one fades in over <paramref name="inSeconds"/>. Contents fill the region (they are layered while
    /// fading), so give the region a size.
    /// </summary>
    public KeyedRegion<T> Crossfade(float outSeconds = 0.16f, float inSeconds = 0.3f)
    {
        (_out, _in) = (MathF.Max(0f, outSeconds), MathF.Max(0f, inSeconds));
        return this;
    }

    protected override void OnDisposed()
    {
        _leavingScope?.Dispose();
        _leavingScope = null;
        _scope?.Dispose();
        _scope = null;
    }

    private void Update()
    {
        var key = _key();
        if (_built && EqualityComparer<T>.Default.Equals(key, _current))
        {
            return;
        }

        (_built, _current) = (true, key);
        Reactive.Untrack(Swap);
    }

    private void Swap()
    {
        var fade = Context is not null && (_out > 0f || _in > 0f);
        DropLeaving();
        if (fade && _content is not null)
        {
            // The old content stays a moment to fade out; it takes no pointer meanwhile.
            var leaving = _leaving = _content;
            _leavingScope = _scope;
            leaving.HitTestVisible = false;
            foreach (var d in leaving.Descendants())
            {
                d.Enabled = d != leaving && d.Enabled;
            }

            leaving.AnimateOpacity(0f, Motion.Tween(_out, Easing.CubicInOut)).AnimateScale(0.98f, Motion.Tween(_out, Easing.CubicInOut));
            leaving.After(_out, () =>
            {
                if (_leaving == leaving)
                {
                    DropLeaving();
                }
            });
        }
        else
        {
            _content?.Dispose();
            _scope?.Dispose();
        }

        _scope = Reactive.CreateScope(detached: true);
        var key = _current;
        var content = _scope.Run(() => _build(key));
        Builds++;
        _content = content;
        if (content is null)
        {
            return;
        }

        content.Width = content.Width.Kind == UIUnitKind.Auto ? UIUnit.Percent(100) : content.Width;
        content.Height = content.Height.Kind == UIUnitKind.Auto ? UIUnit.Percent(100) : content.Height;
        AddChild(content);
        if (fade)
        {
            content.Opacity = 0f;
            content.RenderScale = 1.02f;
            content.AnimateOpacity(1f, Motion.Tween(_in)).AnimateScale(1f, Motion.Tween(_in));
        }
    }

    private void DropLeaving()
    {
        if (_leaving is null)
        {
            return;
        }

        var leaving = _leaving;
        _leaving = null;
        leaving.Dispose();
        _leavingScope?.Dispose();
        _leavingScope = null;
    }
}

/// <summary>
/// A keyed list. Each key keeps its element (and the bindings inside it) while it stays in the list: it is moved, not
/// rebuilt. An item replaced under the same key updates in place through its item signal; a new key builds one
/// element; a key that left is disposed. The region is the list's container: give it a layout and gap like any element
/// (a column by default).
/// </summary>
public sealed class ForRegion<T> : UIElement
{
    private readonly Func<IReadOnlyList<T>?> _items;
    private readonly Func<T, object> _key;
    private readonly Func<IReadable<T>, IReadable<int>, UIElement> _build;
    private readonly Dictionary<object, Entry> _entries = [];
    private readonly List<Entry> _order = [];
    private int _stamp;

    public ForRegion(Func<IReadOnlyList<T>?> items, Func<T, object> key, Func<IReadable<T>, IReadable<int>, UIElement> build)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _key = key ?? throw new ArgumentNullException(nameof(key));
        _build = build ?? throw new ArgumentNullException(nameof(build));
        Name = "For";
        Layout = LayoutMode.Column;
        this.Effect(Update, "For");
    }

    /// <summary>Elements built so far (reordering builds nothing).</summary>
    public int Builds { get; private set; }

    public int Count => _order.Count;

    protected override void OnDisposed()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Scope.Dispose();
        }

        _entries.Clear();
        _order.Clear();
    }

    private void Update()
    {
        var items = _items() ?? [];
        Reactive.Untrack(() => Reconcile(items));
    }

    private void Reconcile(IReadOnlyList<T> items)
    {
        var stamp = ++_stamp;
        _order.Clear();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var key = _key(item) ?? throw new InvalidOperationException("For: an item has a null key.");
            if (_entries.TryGetValue(key, out var entry))
            {
                if (entry.Seen == stamp)
                {
                    throw new InvalidOperationException($"For: duplicate key '{key}'.");
                }

                entry.Item.Set(item);
                entry.Index.Set(i);
            }
            else
            {
                var scope = Reactive.CreateScope(detached: true);
                var itemSignal = Signal.Create(item);
                var indexSignal = Signal.Create(i);
                entry = new Entry(itemSignal, indexSignal, scope, scope.Run(() => _build(itemSignal, indexSignal)));
                _entries.Add(key, entry);
                Builds++;
            }

            entry.Seen = stamp;
            _order.Add(entry);
        }

        // Dispose keys that left, then put the rest in order (moves, not rebuilds).
        if (_entries.Count != _order.Count)
        {
            foreach (var (key, entry) in _entries.Where(p => p.Value.Seen != stamp).ToList())
            {
                _entries.Remove(key);
                entry.Element.Dispose();
                entry.Scope.Dispose();
            }
        }

        for (var i = 0; i < _order.Count; i++)
        {
            var element = _order[i].Element;
            if (i < Children.Count && Children[i] == element)
            {
                continue;
            }

            if (element.Parent == this)
            {
                element.SetSiblingIndex(i);
            }
            else
            {
                InsertChild(i, element);
            }
        }
    }

    private sealed class Entry(Signal<T> item, Signal<int> index, ReactiveScope scope, UIElement element)
    {
        public Signal<T> Item { get; } = item;

        public Signal<int> Index { get; } = index;

        public ReactiveScope Scope { get; } = scope;

        public UIElement Element { get; } = element;

        public int Seen { get; set; }
    }
}

/// <summary>
/// Pages by index (tabs, navigation): each page is built the first time it is shown and kept, so switching back is a
/// visibility change and its state (scroll position, typed text) stays.
/// </summary>
public sealed class PagesRegion : UIElement
{
    private readonly Func<int> _index;
    private readonly Func<UIElement>[] _pages;
    private readonly UIElement?[] _built;
    private int _shown = -1;

    public PagesRegion(Func<int> index, Func<UIElement>[] pages)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _pages = pages ?? throw new ArgumentNullException(nameof(pages));
        _built = new UIElement?[pages.Length];
        Name = "Pages";
        Layout = LayoutMode.Column;
        this.Effect(Update, "Pages");
    }

    public int BuiltCount => _built.Count(p => p is not null);

    private void Update()
    {
        var index = _index();
        if (index != _shown)
        {
            Reactive.Untrack(() => Show(index));
        }
    }

    private void Show(int index)
    {
        if (_shown >= 0 && _shown < _built.Length && _built[_shown] is { } previous)
        {
            previous.Visible = false;
        }

        _shown = index;
        if (index < 0 || index >= _pages.Length)
        {
            return;
        }

        var page = _built[index] ??= AddChild(Scope.Run(_pages[index]));
        page.Visible = true;
    }
}
