// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using MNVTEngine.Graphics.Text;

namespace MNVTEngine.UI;

/// <summary>
/// The design theme's built-in faces, embedded in the UI library and loaded on first use: Manrope (body text, SIL OFL
/// 1.1), Space Grotesk (titles and labels, SIL OFL 1.1) and Lucide (icons, ISC). Text faces fall back to the engine's
/// default font for characters they lack.
/// </summary>
public static class DesignFonts
{
    private static readonly Lazy<Font> BodyFace = new(() => Load("Manrope-Medium.ttf"));
    private static readonly Lazy<Font> BodyStrongFace = new(() => Load("Manrope-ExtraBold.ttf"));
    private static readonly Lazy<Font> DisplayFace = new(() => Load("SpaceGrotesk-SemiBold.ttf"));
    private static readonly Lazy<Font> DisplayStrongFace = new(() => Load("SpaceGrotesk-Bold.ttf"));
    private static readonly Lazy<Font> IconsFace = new(() => Load("lucide.ttf", fallback: false));
    private static readonly Lazy<Font> FallbackFace = new(Font.LoadDefault);

    public static Font Body => BodyFace.Value;

    public static Font BodyStrong => BodyStrongFace.Value;

    public static Font Display => DisplayFace.Value;

    public static Font DisplayStrong => DisplayStrongFace.Value;

    public static Font Icons => IconsFace.Value;

    private static Font Load(string file, bool fallback = true)
    {
        var name = "MNVTEngine.UI.Fonts." + file;
        using var stream = typeof(DesignFonts).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The built-in font resource {name} is missing from MNVTEngine.UI.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var font = Font.Load(memory.ToArray(), file + " (built-in)");
        if (fallback)
        {
            font.Fallbacks.Add(FallbackFace.Value);
        }

        return font;
    }
}
