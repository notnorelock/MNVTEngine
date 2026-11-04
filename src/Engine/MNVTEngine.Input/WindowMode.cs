namespace MNVTEngine;

/// <summary>How the game window occupies the screen.</summary>
public enum WindowMode
{
    /// <summary>A normal window with a title bar.</summary>
    Windowed,

    /// <summary>A window without title bar or border, at its own size and position.</summary>
    Borderless,

    /// <summary>Exclusive fullscreen on the window's monitor (may change the display mode).</summary>
    Fullscreen,

    /// <summary>A borderless window covering the whole monitor ("windowed fullscreen"): fast to switch, no mode change.</summary>
    BorderlessFullscreen,
}

/// <summary>One RGBA8 image of a window icon, row 0 at the top.</summary>
public sealed record WindowIcon(int Width, int Height, byte[] Rgba);
