using MNVTEngine.Mathematics;

namespace MNVTEngine.UI;

/// <summary>
/// Base of routed UI events. Pointer, key and text events go to the target (the element under the pointer, or the
/// focused element) and then bubble to its ancestors until a handler sets <see cref="Handled"/>.
/// </summary>
public abstract class UIEventArgs : EventArgs
{
    public UIElement Target { get; internal set; } = null!;

    /// <summary>Stops bubbling to further ancestors.</summary>
    public bool Handled { get; set; }
}

public sealed class PointerEventArgs : UIEventArgs
{
    /// <summary>Pointer position in the context's logical coordinates (origin top-left of the UI viewport).</summary>
    public Vector2 Position { get; internal set; }

    public MouseButton Button { get; internal set; }

    /// <summary>Wheel movement (positive Y = away from the user), for scroll events.</summary>
    public Vector2 Scroll { get; internal set; }

    /// <summary>1 for a single click, 2 for a double click (same element within 0.4 s).</summary>
    public int ClickCount { get; internal set; }

    /// <summary>Position relative to <paramref name="element"/>'s top-left.</summary>
    public Vector2 LocalPosition(UIElement element) => Position - element.Bounds.Min;
}

public sealed class KeyEventArgs : UIEventArgs
{
    public Key Key { get; internal set; }

    public bool Control { get; internal set; }

    public bool Shift { get; internal set; }

    public bool Alt { get; internal set; }

    /// <summary>True for automatic repeats while the key is held.</summary>
    public bool Repeat { get; internal set; }
}

public sealed class TextInputEventArgs : UIEventArgs
{
    /// <summary>Characters typed (from the operating system's text input: layout-, dead-key- and IME-aware).</summary>
    public string Text { get; internal set; } = "";
}

/// <summary>Reads and writes the system clipboard. Hosts provide the platform one; tests use <see cref="MemoryClipboard"/>.</summary>
public interface IClipboard
{
    string? GetText();

    void SetText(string text);
}

public sealed class MemoryClipboard : IClipboard
{
    public string? Text { get; set; }

    public string? GetText() => Text;

    public void SetText(string text) => Text = text;
}
