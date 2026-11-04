using MNVTEngine.Platform;
using MNVTEngine.UI;

namespace MNVTEngine.Runtime;

/// <summary>The window's system clipboard for UI text fields.</summary>
public sealed class WindowClipboard(GameWindow window) : IClipboard
{
    public string? GetText() => window.ClipboardText;

    public void SetText(string text) => window.ClipboardText = text;
}
