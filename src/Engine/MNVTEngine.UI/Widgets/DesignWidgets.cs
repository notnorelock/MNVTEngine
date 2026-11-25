// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using MNVTEngine.Mathematics;
using MNVTEngine.Reactivity;

namespace MNVTEngine.UI.Widgets;

/// <summary>An icon-font glyph (see <see cref="Icons"/>) drawn like text; the theme's <c>Icon</c> style sets the face and size.</summary>
public class Icon : Label
{
    public Icon() => HitTestVisible = false;
}

/// <summary>Three pulsing dots in the foreground color (<c>Spinner</c> style): work in progress.</summary>
public class Spinner : UIElement
{
    private float _time, _size = 24f;

    public Spinner() => HitTestVisible = false;

    /// <summary>Width of the three dots in logical pixels; the height follows.</summary>
    public float Size { get => _size; set => SetLayout(ref _size, MathF.Max(6f, value)); }

    protected internal override Vector2 MeasureContent(float availableWidth, float availableHeight) => new(Size, Size / 3.4f);

    protected internal override bool Tick(float deltaSeconds)
    {
        _time += deltaSeconds;
        return true;
    }

    protected internal override void Paint(UIPainter painter, in ResolvedStyle style)
    {
        ArgumentNullException.ThrowIfNull(painter);
        base.Paint(painter, style);
        var content = ContentBounds;
        var dot = Size / 3.4f;
        var gap = dot * 0.7f;
        var x = content.X + ((content.Width - ((3f * dot) + (2f * gap))) / 2f);
        var y = content.Y + ((content.Height - dot) / 2f);
        for (var i = 0; i < 3; i++)
        {
            var pulse = 0.3f + (0.7f * (0.5f + (0.5f * MathF.Sin((_time * 6f) - (i * 0.9f)))));
            painter.FillRect(new Rect(x + (i * (dot + gap)), y, dot, dot), style.Foreground with { A = style.Foreground.A * pulse }, dot / 2f);
        }
    }
}

/// <summary>One tab of <see cref="Tabs"/>: a button with a <c>:checked</c> state.</summary>
public class Tab : Button
{
    private bool _selected;

    public bool Selected { get => _selected; set => SetVisual(ref _selected, value); }

    protected override bool IsChecked => Selected;
}

/// <summary>
/// A row of tabs bound to a selected index. Clicking a tab selects it; Left and Right move the selection while a tab
/// has focus.
/// </summary>
public class Tabs : UIElement
{
    private readonly Signal<int> _selected;

    public Tabs(Signal<int> selected, IReadOnlyList<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        _selected = selected ?? throw new ArgumentNullException(nameof(selected));
        Name = "Tabs";
        Layout = LayoutMode.Row;
        AlignItems = Align.Center;
        Gap = 4f;
        for (var i = 0; i < labels.Count; i++)
        {
            var index = i;
            var tab = AddChild(new Tab { Name = labels[i], Text = labels[i] });
            tab.Clicked += () => _selected.Value = index;
            tab.KeyPressed += (_, e) =>
            {
                if (e.Key is Key.Left or Key.Right)
                {
                    var next = Math.Clamp(index + (e.Key == Key.Right ? 1 : -1), 0, labels.Count - 1);
                    _selected.Value = next;
                    Children[next].Focus();
                    e.Handled = true;
                }
            };
            tab.Bind(() => _selected.Value == index, (t, on) => t.Selected = on);
        }
    }
}

/// <summary>
/// A list row: optional leading element (an icon or avatar), a title and an optional subtitle, and an optional
/// trailing element (a button stays its own target). Hover, press, focus and selected (<c>:checked</c>) states.
/// </summary>
public class ListItem : UIElement
{
    private bool _selected;

    public ListItem(string title, string? subtitle = null)
    {
        Name = "ListItem";
        Focusable = true;
        Layout = LayoutMode.Row;
        AlignItems = Align.Center;
        Gap = 12f;
        Body = new UIElement { Name = "Text", Layout = LayoutMode.Column, FlexGrow = 1f, Gap = 2f };
        TitleLabel = Body.AddChild(new Label { Name = "Title", Text = title, StyleClass = "strong" });
        SubtitleLabel = Body.AddChild(new Label { Name = "Subtitle", Text = subtitle ?? "", StyleClass = "small", Visible = !string.IsNullOrEmpty(subtitle) });
        AddChild(Body);
    }

    public UIElement Body { get; }

    public Label TitleLabel { get; }

    public Label SubtitleLabel { get; }

    public bool Selected { get => _selected; set => SetVisual(ref _selected, value); }

    /// <summary>Raised on click, or Enter/Space while focused.</summary>
    public event Action? Clicked;

    public ListItem Leading(UIElement element)
    {
        InsertChild(0, element);
        return this;
    }

    public ListItem Trailing(UIElement element)
    {
        AddChild(element);
        return this;
    }

    protected override bool IsChecked => Selected;

    protected internal override void OnClick(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnClick(e);
        if (Clicked is { } handler)
        {
            RaiseEvent(nameof(Clicked), handler);
            e.Handled = true;
        }
    }
}

/// <summary>
/// A modal dialog in the context's overlay: a scrim that blocks everything behind, and a card that keeps keyboard focus
/// inside (Tab cycles within it; focus returns to where it was on close). Escape and a click on the scrim cancel it when
/// it is dismissible. It fades and pops in, and is removed after its fade-out.
/// </summary>
public sealed class Dialog : UIElement
{
    private readonly UIElement _scrim;
    private readonly UIElement? _previousScope, _previousFocus;
    private readonly DesignTokens _tokens;
    private bool _closing;

    private Dialog(UIContext context, bool dismissible)
    {
        Context0 = context;
        _tokens = Design.Current.Peek();
        Dismissible = dismissible;
        Name = "Dialog";
        Focusable = true;
        Layout = LayoutMode.Column;
        Gap = _tokens.Space4;
        Width = 420f;
        MaxWidth = 560f;
        Anchor = Pivot = new Vector2(0.5f, 0.5f);
        _scrim = new UIElement
        {
            Name = "Scrim", StyleClass = "scrim", HitTestVisible = true, Width = UIUnit.Percent(100), Height = UIUnit.Percent(100),
            ZIndex = 1000,
        };
        _scrim.AddChild(this);
        _scrim.PointerClicked += (_, e) =>
        {
            if (e.Target == _scrim && Dismissible)
            {
                Close();
            }

            e.Handled = true;
        };
        KeyPressed += (_, e) =>
        {
            if (e.Key == Key.Escape && Dismissible)
            {
                Close();
                e.Handled = true;
            }
        };
        (_previousScope, _previousFocus) = (context.FocusScope, context.Focused);
    }

    public bool Dismissible { get; }

    /// <summary>Raised once when the dialog starts closing.</summary>
    public event Action? Closed;

    private UIContext Context0 { get; }

    /// <summary>
    /// Opens a dialog with a title, a body and action buttons (right-aligned, in order). Buttons do not close the
    /// dialog by themselves: call <see cref="Close"/> from their handlers.
    /// </summary>
    public static Dialog Open(UIContext context, string title, UIElement? body = null, bool dismissible = true, params UIElement[] actions)
    {
        ArgumentNullException.ThrowIfNull(context);
        var dialog = new Dialog(context, dismissible);
        dialog.AddChild(new Label { Name = "Title", Text = title, StyleClass = "heading", Wrap = true });
        if (body is not null)
        {
            dialog.AddChild(body);
        }

        if (actions.Length > 0)
        {
            var row = dialog.AddChild(new UIElement { Name = "Actions", Layout = LayoutMode.Row, Justify = Justify.End, Gap = dialog._tokens.Space2 });
            foreach (var action in actions)
            {
                row.AddChild(action);
            }
        }

        context.Overlay.AddChild(dialog._scrim);
        context.FocusScope = dialog;
        var first = dialog.Descendants().FirstOrDefault(e => e != dialog && e.Focusable && e.IsEnabledInHierarchy);
        (first ?? dialog).Focus();
        dialog._scrim.Opacity = 0f;
        dialog._scrim.AnimateOpacity(1f, dialog._tokens.Fade);
        dialog.RenderScale = 0.96f;
        dialog.AnimateScale(1f, dialog._tokens.Open);
        return dialog;
    }

    /// <summary>Closes the dialog (fading out, then removed); returns focus to where it was. Safe to call twice.</summary>
    public void Close()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        var context = Context0;
        if (context.FocusScope == this)
        {
            context.FocusScope = _previousScope;
        }

        context.SetFocus(_previousFocus is { IsDisposed: false } previous && previous.Context == context ? previous : null);
        _scrim.HitTestVisible = false;
        foreach (var e in Descendants())
        {
            e.Enabled = false;
        }

        if (Closed is { } closed)
        {
            RaiseEvent(nameof(Closed), closed);
        }

        _scrim.AnimateOpacity(0f, _tokens.Close);
        this.AnimateScale(0.97f, _tokens.Close);
        _scrim.After(_tokens.Close.Duration, _scrim.Dispose);
    }
}

public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>
/// Short messages at the bottom of a context: at most three at once (the oldest leaves first), never focused and never
/// blocking the pointer; each leaves by itself after its time.
/// </summary>
public static class Toasts
{
    /// <summary>The toast box (styled by the theme's <c>Toast</c> selector).</summary>
    public sealed class Toast : UIElement;

    public const int MaxVisible = 3;

    public static Toast Show(UIContext context, string message, ToastKind kind = ToastKind.Info, float seconds = 3f)
    {
        ArgumentNullException.ThrowIfNull(context);
        var tokens = Design.Current.Peek();
        var host = context.Overlay.Find("Toasts") ?? context.Overlay.AddChild(new UIElement
        {
            Name = "Toasts", Layout = LayoutMode.Column, AlignItems = Align.Center, Gap = tokens.Space2, HitTestVisible = false,
            Anchor = new Vector2(0.5f, 1f), Pivot = new Vector2(0.5f, 1f), Top = -tokens.Space5, ZIndex = 900,
        });
        while (host.Children.Count >= MaxVisible)
        {
            host.Children[0].Dispose();
        }

        var (glyph, color) = kind switch
        {
            ToastKind.Success => (Icons.CircleCheck, tokens.Success),
            ToastKind.Warning => (Icons.TriangleAlert, tokens.Warning),
            ToastKind.Error => (Icons.CircleAlert, tokens.Danger),
            _ => (Icons.Info, tokens.Secondary),
        };
        var toast = host.AddChild(new Toast { Name = "Toast", Layout = LayoutMode.Row, AlignItems = Align.Center, Gap = tokens.Space2, HitTestVisible = false });
        toast.AddChild(new Icon { Text = glyph, TextColor = color });
        toast.AddChild(new Label { Text = message, MaxWidth = 480f, Wrap = true });
        toast.Opacity = 0f;
        toast.Translate = new Vector2(0f, 12f);
        toast.AnimateOpacity(1f, tokens.Fade).AnimateTranslate(Vector2.Zero, tokens.Slide);
        toast.After(MathF.Max(0.5f, seconds), () =>
        {
            toast.AnimateOpacity(0f, tokens.Close);
            toast.After(tokens.Close.Duration, toast.Dispose);
        });
        return toast;
    }
}
