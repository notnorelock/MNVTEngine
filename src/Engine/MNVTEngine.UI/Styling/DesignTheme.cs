// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using MNVTEngine.Mathematics;

namespace MNVTEngine.UI;

public sealed partial class UITheme
{
    /// <summary>
    /// The design theme from <paramref name="tokens"/> (the current <see cref="Design.Tokens"/> when null): every
    /// widget, the button variants (<c>.primary</c>, <c>.secondary</c>, <c>.quiet</c>, <c>.danger</c>, <c>.live</c>),
    /// surfaces (<c>.card</c>, <c>.surface</c>) and text roles (<c>.display</c>, <c>.title</c>, <c>.heading</c>,
    /// <c>.strong</c>, <c>.muted</c>, <c>.small</c>, <c>.caption</c>, <c>.label</c>, <c>.danger-text</c>), so layouts
    /// made in the editor and UI built with <see cref="V"/> look the same.
    /// </summary>
    public static UITheme Design(DesignTokens? tokens = null) => DesignTheme.Build(tokens ?? global::MNVTEngine.UI.Design.Current.Peek());
}

public sealed partial class UIContext
{
    /// <summary>
    /// Shows the design theme and follows <see cref="Design.Current"/>: changing the tokens restyles this context.
    /// The binding stops when the context is disposed.
    /// </summary>
    public UIContext UseDesign()
    {
        Root.Bind(() => Design.Tokens, (_, tokens) => Theme = UITheme.Design(tokens), "design theme");
        return this;
    }
}

internal static class DesignTheme
{
    public static UITheme Build(DesignTokens t)
    {
        var theme = new UITheme(t.Name) { DefaultFontSize = t.Body.Size };
        theme["UIElement"] = new UIStyle
        {
            Foreground = t.Text, Accent = t.Accent, BorderColor = t.Line, Opacity = 1f,
            Font = DesignTokens.FontFor(t.Body.Font), FontSize = t.Body.Size,
        };

        // Surfaces.
        theme["Panel"] = new UIStyle { Background = Color.Clear };
        theme[".card"] = theme[".panel"] = new UIStyle
        {
            Background = t.SurfaceRaised, BorderColor = t.Line, BorderWidth = t.BorderWidth, CornerRadius = t.RadiusLarge, Padding = t.Space5,
        };
        theme[".surface"] = new UIStyle { Background = t.Surface, CornerRadius = t.Radius };
        theme[".backdrop"] = new UIStyle { Background = t.Backdrop };
        theme[".scrim"] = new UIStyle { Background = t.Scrim };
        theme["Separator"] = new UIStyle { Background = t.Divider };
        theme["Tooltip"] = new UIStyle
        {
            Background = t.SurfaceRaised, BorderColor = t.LineStrong, BorderWidth = t.BorderWidth, Foreground = t.Text,
            CornerRadius = t.RadiusSmall, Padding = Thickness.Symmetric(t.Space2, t.Space1), FontSize = t.Small.Size,
            Font = DesignTokens.FontFor(t.Small.Font),
        };

        // Text roles.
        theme["Label"] = new UIStyle { Background = Color.Clear };
        Role(theme, ".display", t.Display, t.Text);
        Role(theme, ".title", t.Title, t.Text);
        Role(theme, ".heading", t.Heading, t.Text);
        Role(theme, ".body", t.Body, t.Text);
        Role(theme, ".strong", t.BodyStrong, t.Text);
        Role(theme, ".muted", t.Body, t.TextMuted);
        Role(theme, ".small", t.Small, t.TextMuted);
        Role(theme, ".caption", t.Caption, t.TextFaint);
        Role(theme, ".label", t.Label, t.TextFaint);
        theme[".danger-text"] = new UIStyle { Foreground = t.Danger };
        theme[".success-text"] = new UIStyle { Foreground = t.Success };
        theme["Icon"] = new UIStyle { Background = Color.Clear, Foreground = t.TextMuted, Font = DesignTokens.FontFor(FontRole.Icon), FontSize = t.IconSize };

        // Buttons: the base look is the secondary variant.
        var buttonFont = DesignTokens.FontFor(t.ButtonText.Font);
        theme["Button"] = new UIStyle
        {
            Background = t.SurfaceRaised, Foreground = t.Text, BorderColor = t.Line, BorderWidth = t.BorderWidth, CornerRadius = t.Radius,
            Padding = Thickness.Symmetric(t.Space4, t.Space2), Font = buttonFont, FontSize = t.ButtonText.Size,
        };
        theme["Button:hover"] = new UIStyle { Background = t.SurfaceHover, BorderColor = t.LineStrong };
        theme["Button:pressed"] = new UIStyle { Background = t.SurfacePressed };
        theme["Button:focused"] = new UIStyle { BorderColor = t.Accent };
        theme["Button:disabled"] = new UIStyle { Foreground = t.TextDisabled, Opacity = 0.6f };
        Variant(theme, ".primary", t.Accent, t.AccentHover, t.AccentPressed, t.OnAccent, t.Accent);
        Variant(theme, ".secondary", t.SurfaceRaised, t.SurfaceHover, t.SurfacePressed, t.Text, t.Line);
        Variant(theme, ".quiet", Color.Clear, t.SurfaceHover, t.SurfacePressed, t.TextMuted, Color.Clear);
        theme[".quiet:hover"].Foreground = t.Text;
        Variant(theme, ".danger", t.DangerSoft, t.DangerSoft with { A = 0.24f }, t.DangerSoft with { A = 0.32f }, t.Danger, t.DangerSoft);
        Variant(theme, ".live", t.SecondarySoft, t.SecondarySoft with { A = 0.22f }, t.SecondarySoft with { A = 0.3f }, t.SecondaryText, t.SecondarySoft);

        // Toggles.
        foreach (var control in (string[])["Checkbox", "RadioButton", "Toggle"])
        {
            theme[control] = new UIStyle { Background = Color.Clear, BorderColor = t.LineStrong, CornerRadius = t.RadiusSmall, Padding = Thickness.Zero };
            theme[control + ":hover"] = new UIStyle { BorderColor = t.TextFaint };
            theme[control + ":focused"] = new UIStyle { BorderColor = t.Accent };
            theme[control + ":disabled"] = new UIStyle { Foreground = t.TextDisabled, Opacity = 0.6f };
        }

        // Text input.
        foreach (var field in (string[])["TextField", "TextArea"])
        {
            theme[field] = new UIStyle
            {
                Background = t.Surface, BorderColor = t.Line, BorderWidth = t.BorderWidth, CornerRadius = t.Radius,
                Padding = Thickness.Symmetric(t.Space3, t.Space2 + 2f),
            };
            theme[field + ":hover"] = new UIStyle { BorderColor = t.LineStrong };
            theme[field + ":focused"] = new UIStyle { BorderColor = t.Accent };
            theme[field + ":disabled"] = new UIStyle { Foreground = t.TextDisabled, Opacity = 0.6f };
        }

        theme[".invalid"] = new UIStyle { BorderColor = t.Danger };
        theme[".invalid:focused"] = new UIStyle { BorderColor = t.Danger };

        // Selection and range.
        theme["Dropdown"] = new UIStyle
        {
            Background = t.Surface, BorderColor = t.Line, BorderWidth = t.BorderWidth, CornerRadius = t.Radius, Padding = Thickness.Symmetric(t.Space3, t.Space2 + 2f),
        };
        theme["Dropdown:hover"] = new UIStyle { BorderColor = t.LineStrong };
        theme["Dropdown:focused"] = new UIStyle { BorderColor = t.Accent };
        theme["DropdownList"] = new UIStyle
        {
            Background = t.SurfaceRaised, BorderColor = t.LineStrong, BorderWidth = t.BorderWidth, CornerRadius = t.Radius, Padding = t.Space1,
            Shadow = new Color(0f, 0f, 0f, 0.35f), ShadowOffset = new Vector2(0f, 6f),
        };
        theme["DropdownItem"] = new UIStyle { Background = Color.Clear, CornerRadius = t.RadiusSmall, Padding = Thickness.Symmetric(t.Space3, t.Space2) };
        theme["DropdownItem:hover"] = new UIStyle { Background = t.SurfaceHover };
        theme["Slider"] = new UIStyle { Background = t.LineStrong, CornerRadius = 3f };
        theme["Slider:hover"] = new UIStyle { Background = t.TextDisabled };
        theme["ProgressBar"] = new UIStyle { Background = t.Line, CornerRadius = t.RadiusSmall, FontSize = t.Small.Size };
        theme["ScrollView"] = new UIStyle { Background = Color.Clear, Accent = t.TextDisabled with { A = 0.7f } };

        // Components (see V).
        theme["Spinner"] = new UIStyle { Background = Color.Clear, Foreground = t.Accent };
        theme["Tab"] = new UIStyle { Background = Color.Clear, Foreground = t.TextMuted, CornerRadius = t.RadiusSmall, Padding = Thickness.Symmetric(t.Space3, t.Space2), Font = buttonFont, FontSize = t.ButtonText.Size };
        theme["Tab:hover"] = new UIStyle { Foreground = t.Text, Background = t.SurfaceHover };
        theme["Tab:checked"] = new UIStyle { Foreground = t.Text, Background = t.AccentSoft };
        theme["Tab:focused"] = new UIStyle { BorderColor = t.Accent, BorderWidth = t.BorderWidth };
        theme["ListItem"] = new UIStyle { Background = Color.Clear, CornerRadius = t.Radius, Padding = Thickness.Symmetric(t.Space3, t.Space2) };
        theme["ListItem:hover"] = new UIStyle { Background = t.SurfaceHover };
        theme["ListItem:pressed"] = new UIStyle { Background = t.SurfacePressed };
        theme["ListItem:checked"] = new UIStyle { Background = t.AccentSoft };
        theme["ListItem:focused"] = new UIStyle { BorderColor = t.Accent, BorderWidth = t.BorderWidth };
        theme["Dialog"] = new UIStyle
        {
            Background = t.SurfaceRaised, BorderColor = t.LineStrong, BorderWidth = t.BorderWidth, CornerRadius = t.RadiusPanel, Padding = t.Space5,
            Shadow = new Color(0f, 0f, 0f, 0.45f), ShadowOffset = new Vector2(0f, 10f),
        };
        theme["Toast"] = new UIStyle
        {
            Background = t.SurfaceRaised, BorderColor = t.LineStrong, BorderWidth = t.BorderWidth, CornerRadius = t.Radius,
            Padding = Thickness.Symmetric(t.Space4, t.Space3), Shadow = new Color(0f, 0f, 0f, 0.35f), ShadowOffset = new Vector2(0f, 4f),
        };
        return theme;
    }

    private static void Role(UITheme theme, string selector, TextRole role, Color color) =>
        theme[selector] = new UIStyle { Font = DesignTokens.FontFor(role.Font), FontSize = role.Size, Foreground = color };

    private static void Variant(UITheme theme, string selector, Color fill, Color hover, Color pressed, Color text, Color border)
    {
        theme[selector] = new UIStyle { Background = fill, Foreground = text, BorderColor = border };
        theme[selector + ":hover"] = new UIStyle { Background = hover, BorderColor = border };
        theme[selector + ":pressed"] = new UIStyle { Background = pressed };
        theme[selector + ":disabled"] = new UIStyle { Opacity = 0.5f };
    }
}
