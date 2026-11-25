// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using MNVTEngine.Reactivity;
using MNVTEngine.UI.Widgets;

namespace MNVTEngine.UI;

/// <summary>Button looks of the design theme (style classes).</summary>
public enum ButtonVariant
{
    Primary,
    Secondary,
    Quiet,
    Danger,
    Live,
}

/// <summary>
/// The UI builder: short factories for layout, text, controls and control flow, styled by the design theme. Combine
/// with <see cref="ElementExtensions"/> and mount with <see cref="UIContext.Mount"/>:
/// <code>
/// context.Mount(() => V.Column(V.Title("Hello"), V.Button("Go", onClick: Start)).Gap(12).Padding(24));
/// </code>
/// Controls taking a <see cref="Signal{T}"/> are two-way bound: the control shows the signal and writes edits back.
/// </summary>
public static class V
{
    // Layout.

    public static UIElement Column(params UIElement?[] children) => Box(LayoutMode.Column, children);

    public static UIElement Row(params UIElement?[] children) => Box(LayoutMode.Row, children).AlignItems(Align.Center);

    /// <summary>Children layered on top of each other (absolute layout; place them with <see cref="ElementExtensions.Anchor"/>).</summary>
    public static UIElement Stack(params UIElement?[] children) => Box(LayoutMode.Absolute, children);

    public static UIElement Box(LayoutMode layout, params UIElement?[] children) => new UIElement { Layout = layout }.Children(children);

    /// <summary>A raised surface with padding (<c>.card</c>).</summary>
    public static Panel Card(params UIElement?[] children) =>
        new Panel { Layout = LayoutMode.Column, StyleClass = "card", Gap = Design.Current.Peek().Space3 }.Children(children);

    /// <summary>Takes the free space of a row or column.</summary>
    public static UIElement Spacer() => new UIElement { FlexGrow = 1f };

    /// <summary>A fixed gap.</summary>
    public static UIElement Space(float size) => new UIElement { Width = size, Height = size };

    public static Separator Divider() => new();

    public static ScrollView Scroll(params UIElement?[] children)
    {
        var scroll = new ScrollView();
        scroll.Content.Children(children);
        return scroll;
    }

    // Text.

    public static Label Text(string text) => Styled(text, "body");

    public static Label Text(Func<string> text) => Styled("", "body").Text(text);

    public static Label Display(string text) => Styled(text, "display");

    public static Label Title(string text) => Styled(text, "title");

    public static Label Heading(string text) => Styled(text, "heading");

    public static Label Strong(string text) => Styled(text, "strong");

    public static Label Muted(string text) => Styled(text, "muted");

    public static Label Small(string text) => Styled(text, "small");

    public static Label Caption(string text) => Styled(text, "caption");

    /// <summary>Upper-case spaced label (<c>.label</c>).</summary>
    public static Label Label(string text) => Styled(text.ToUpperInvariant(), "label");

    public static Label Styled(string text, string styleClass) => new() { Text = text, StyleClass = styleClass };

    public static Icon Icon(string glyph) => new() { Text = glyph };

    public static Image Image(Graphics.Texture2D texture) => new() { Texture = texture };

    // Controls.

    public static Button Button(string text, ButtonVariant variant = ButtonVariant.Primary, Action? onClick = null, string? icon = null)
    {
        var tokens = Design.Current.Peek();
        var button = new Button
        {
            Text = text, Glyph = icon, StyleClass = variant.ToString().ToLowerInvariant(),
            HoverScale = tokens.HoverScale, PressScale = tokens.PressScale, Ripple = true,
        };
        return onClick is null ? button : button.OnClick(onClick);
    }

    /// <summary>A square quiet button with only a glyph; <paramref name="tooltip"/> names it.</summary>
    public static Button IconButton(string glyph, string tooltip, Action? onClick = null, ButtonVariant variant = ButtonVariant.Quiet)
    {
        var size = Design.Current.Peek().ControlHeight;
        var button = Button("", variant, onClick, glyph).Tooltip(tooltip).Size(size, size);
        button.Padding = Thickness.Zero;
        return button;
    }

    public static TextField TextField(Signal<string> value, string placeholder = "", Action<string>? onSubmit = null, int maxLength = 0)
    {
        ArgumentNullException.ThrowIfNull(value);
        var field = new TextField { Placeholder = placeholder, MaxLength = maxLength };
        field.Bind(() => value.Value, (f, v) =>
        {
            if (f.Text != v)
            {
                f.Text = v;
            }
        });
        field.TextChanged += t => value.Value = t;
        if (onSubmit is not null)
        {
            field.Submitted += onSubmit;
        }

        return field;
    }

    public static TextField PasswordField(Signal<string> value, string placeholder = "", Action<string>? onSubmit = null)
    {
        var field = TextField(value, placeholder, onSubmit);
        field.Password = true;
        return field;
    }

    public static Toggle Switch(Signal<bool> value, string text = "") => TwoWay(new Toggle { Text = text }, value);

    public static Checkbox Checkbox(Signal<bool> value, string text = "") => TwoWay(new Checkbox { Text = text }, value);

    public static Slider Slider(Signal<float> value, float minimum = 0f, float maximum = 1f, float step = 0f)
    {
        ArgumentNullException.ThrowIfNull(value);
        var slider = new Slider { Minimum = minimum, Maximum = maximum, Step = step };
        slider.Bind(() => value.Value, (s, v) => s.Value = v);
        slider.ValueChanged += v => value.Value = v;
        return slider;
    }

    public static Tabs Tabs(Signal<int> selected, params string[] labels) => new(selected, labels);

    public static ListItem ListItem(string title, string? subtitle = null, Action? onClick = null)
    {
        var item = new ListItem(title, subtitle);
        if (onClick is not null)
        {
            item.Clicked += onClick;
        }

        return item;
    }

    public static Spinner Spinner() => new();

    /// <summary>A label, a field and an error under it; the field gets <c>.invalid</c> while <paramref name="error"/> returns text.</summary>
    public static UIElement FormField(string label, UIElement field, Func<string?>? error = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        field.Width = UIUnit.Percent(100);
        var column = Column(Styled(label, "small"), field).Gap(Design.Current.Peek().Space1 + 2f).FillWidth();
        if (error is not null)
        {
            field.Class("invalid", () => !string.IsNullOrEmpty(error()));
            column.AddChild(ErrorMessage(error));
        }

        return column;
    }

    /// <summary>Danger-colored text with an icon, hidden while <paramref name="message"/> returns null or empty.</summary>
    public static UIElement ErrorMessage(Func<string?> message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Row(Icon(Icons.CircleAlert).Class("danger-text"), Styled("", "danger-text small").Text(() => message() ?? "").Wrap())
            .Gap(6f).Visible(() => !string.IsNullOrEmpty(message()));
    }

    /// <summary>A centred icon, title, explanation and optional action: a list with nothing in it yet.</summary>
    public static UIElement EmptyState(string glyph, string title, string? text = null, UIElement? action = null)
    {
        var icon = Icon(glyph);
        icon.FontSize = 36f;
        return Column(icon, Heading(title), text is null ? null : Muted(text).Wrap().Alignment(Graphics.Text.TextAlign.Center), action)
            .AlignItems(Align.Center).Gap(Design.Current.Peek().Space2).Padding(Design.Current.Peek().Space6);
    }

    // Control flow.

    public static ShowRegion Show(Func<bool> when, Func<UIElement>? then, Func<UIElement>? otherwise = null) => new(when, then, otherwise);

    public static KeyedRegion<T> Keyed<T>(Func<T> key, Func<T, UIElement?> build) => new(key, build);

    public static ForRegion<T> For<T>(Func<IReadOnlyList<T>?> items, Func<T, object> key, Func<IReadable<T>, IReadable<int>, UIElement> build) =>
        new(items, key, build);

    public static PagesRegion Pages(Func<int> index, params Func<UIElement>[] pages) => new(index, pages);

    private static T TwoWay<T>(T box, Signal<bool> value)
        where T : Widgets.Checkbox
    {
        ArgumentNullException.ThrowIfNull(value);
        box.Bind(() => value.Value, (b, v) => b.Checked = v);
        box.CheckedChanged += v => value.Value = v;
        return box;
    }
}
