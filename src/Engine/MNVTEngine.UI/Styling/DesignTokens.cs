// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using MNVTEngine.Graphics.Text;
using MNVTEngine.Mathematics;
using MNVTEngine.Reactivity;

namespace MNVTEngine.UI;

/// <summary>Which built-in face a text role uses (see <see cref="DesignFonts"/>).</summary>
public enum FontRole
{
    Body,
    BodyStrong,
    Display,
    DisplayStrong,
    Icon,
}

/// <summary>A text role: face, size in logical pixels and letter spacing.</summary>
public readonly record struct TextRole(FontRole Font, float Size, float LetterSpacing = 0f);

/// <summary>
/// Design tokens: colors by role and surface level, type roles, spacing, radii, control sizes and motion. The design
/// theme (<see cref="UITheme.Design"/>) and the components built by <see cref="V"/> take every value from here, never
/// from literals, so the look changes in one place. Sizes are logical pixels for a desktop screen (body text 15).
/// Copy and change one with <c>with</c> or object initializers: <c>DesignTokens.Midnight() with { Accent = ... }</c>.
/// </summary>
public sealed record DesignTokens
{
    public string Name { get; init; } = "Midnight";

    // Surfaces, darkest to lightest. No pure black: it smears on OLED screens and looks flat.
    public Color Void { get; init; } = Hex(0x07090E);
    public Color Backdrop { get; init; } = Hex(0x0E121B, 0.96f);
    public Color Surface { get; init; } = Hex(0x141A26);
    public Color SurfaceRaised { get; init; } = Hex(0x1A2130);
    public Color SurfaceHover { get; init; } = Hex(0x1D2534);
    public Color SurfacePressed { get; init; } = Hex(0x111622);
    public Color Line { get; init; } = Hex(0x222B3B);
    public Color LineStrong { get; init; } = Hex(0x2E3950);
    public Color Divider { get; init; } = Hex(0xFFFFFF, 0.07f);

    /// <summary>Behind dialogs: dims and blocks everything below.</summary>
    public Color Scrim { get; init; } = Hex(0x05070B, 0.72f);

    // Brand.
    public Color Accent { get; init; } = Hex(0xC077CE);
    public Color AccentHover { get; init; } = Hex(0xD196DC);
    public Color AccentPressed { get; init; } = Hex(0xA862B6);
    public Color AccentSoft { get; init; } = Hex(0xC077CE, 0.16f);
    public Color AccentLine { get; init; } = Hex(0xC077CE, 0.45f);

    /// <summary>Live state, presence and links.</summary>
    public Color Secondary { get; init; } = Hex(0x5DA2E1);
    public Color SecondarySoft { get; init; } = Hex(0x5DA2E1, 0.14f);

    /// <summary>The secondary color lightened for text on a secondary-tinted fill.</summary>
    public Color SecondaryText { get; init; } = Hex(0x8EC0EC);

    // Text.
    public Color Text { get; init; } = Hex(0xEEF2F8);
    public Color TextMuted { get; init; } = Hex(0x9AA5BA);

    /// <summary>Captions and labels: the faintest color text that must be read may use.</summary>
    public Color TextFaint { get; init; } = Hex(0x7A869C);

    /// <summary>Disabled controls and offline presence; too faint for text that must be read.</summary>
    public Color TextDisabled { get; init; } = Hex(0x5F6B80);

    /// <summary>Text on an accent fill.</summary>
    public Color OnAccent { get; init; } = Hex(0x1A0E1E);

    // Feedback.
    public Color Success { get; init; } = Hex(0x7EE2A8);
    public Color Warning { get; init; } = Hex(0xFFC857);
    public Color Danger { get; init; } = Hex(0xFF6B81);
    public Color DangerSoft { get; init; } = Hex(0xFF6B81, 0.14f);

    // Shape: small for compact controls, the base for controls, large for surfaces, panel for whole panels.
    public float RadiusSmall { get; init; } = 6f;
    public float Radius { get; init; } = 9f;
    public float RadiusLarge { get; init; } = 12f;
    public float RadiusPanel { get; init; } = 16f;
    public float BorderWidth { get; init; } = 1f;

    // Spacing scale.
    public float Space1 { get; init; } = 4f;
    public float Space2 { get; init; } = 8f;
    public float Space3 { get; init; } = 12f;
    public float Space4 { get; init; } = 16f;
    public float Space5 { get; init; } = 24f;
    public float Space6 { get; init; } = 32f;
    public float Space8 { get; init; } = 48f;

    // Controls.
    public float ControlHeight { get; init; } = 40f;
    public float ControlHeightSmall { get; init; } = 32f;
    public float IconSize { get; init; } = 18f;

    // Type roles.
    public TextRole Display { get; init; } = new(FontRole.DisplayStrong, 34f);
    public TextRole Title { get; init; } = new(FontRole.DisplayStrong, 26f);
    public TextRole Heading { get; init; } = new(FontRole.Display, 19f);
    public TextRole Body { get; init; } = new(FontRole.Body, 15f);
    public TextRole BodyStrong { get; init; } = new(FontRole.BodyStrong, 15f);
    public TextRole Small { get; init; } = new(FontRole.Body, 13f);
    public TextRole Caption { get; init; } = new(FontRole.Body, 12f);

    /// <summary>Small spaced capitals above sections.</summary>
    public TextRole Label { get; init; } = new(FontRole.DisplayStrong, 11f, 1.2f);

    public TextRole ButtonText { get; init; } = new(FontRole.BodyStrong, 14f);

    // Motion: soft and quick; nothing makes anyone wait.
    public Motion Fade { get; init; } = Motion.Tween(0.14f);
    public Motion Hover { get; init; } = Motion.Tween(0.12f);

    /// <summary>Switch knobs, tab indicators and other short slides.</summary>
    public Motion Slide { get; init; } = Motion.Spring(300f);
    public Motion Open { get; init; } = Motion.Tween(0.28f, Easing.BackOut);
    public Motion Close { get; init; } = Motion.Tween(0.14f);

    /// <summary>Press and hover scale: a stiff spring settles in a few frames.</summary>
    public Motion Press { get; init; } = Motion.Spring(480f);
    public float HoverScale { get; init; } = 1.02f;
    public float PressScale { get; init; } = 0.965f;

    /// <summary>The default dark theme: violet accent, sky secondary, deep blue-grey surfaces.</summary>
    public static DesignTokens Midnight() => new();

    /// <summary>A high-contrast variant: brighter text and lines, stronger borders.</summary>
    public static DesignTokens MidnightHighContrast() => new()
    {
        Name = "Midnight High Contrast",
        Text = Color.White,
        TextMuted = Hex(0xC8D0DE),
        TextFaint = Hex(0x9AA5BA),
        TextDisabled = Hex(0x7A869C),
        Line = Hex(0x3A4660),
        LineStrong = Hex(0x5A6884),
        AccentLine = Hex(0xC077CE, 0.9f),
        BorderWidth = 2f,
    };

    /// <summary>The font of a role (built-in faces).</summary>
    public static Font FontFor(FontRole role) => role switch
    {
        FontRole.BodyStrong => DesignFonts.BodyStrong,
        FontRole.Display => DesignFonts.Display,
        FontRole.DisplayStrong => DesignFonts.DisplayStrong,
        FontRole.Icon => DesignFonts.Icons,
        _ => DesignFonts.Body,
    };

    public static Color Hex(int rgb, float alpha = 1f) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);
}

/// <summary>
/// The tokens components read, per UI thread. Setting <see cref="Current"/> restyles every component bound to it;
/// contexts showing the design theme switch with it (see <see cref="UIContext.UseDesign"/>).
/// </summary>
public static class Design
{
    [ThreadStatic]
    private static Signal<DesignTokens>? t_current;

    public static Signal<DesignTokens> Current => t_current ??= Signal.Create(DesignTokens.Midnight());

    /// <summary>The tokens read reactively (inside bindings): shorthand for <c>Current.Value</c>.</summary>
    public static DesignTokens Tokens => Current.Value;
}
