// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-12.

using MNVTEngine.Platform;

namespace MNVTEngine.UI;

/// <summary>The window's system clipboard for UI text fields.</summary>
public sealed class WindowClipboard(GameWindow window) : IClipboard
{
    public string? GetText() => window.ClipboardText;

    public void SetText(string text) => window.ClipboardText = text;
}
