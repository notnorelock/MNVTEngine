namespace MNVTEngine;

/// <summary>The game window as game code sees it; implemented by the host (backend-independent).</summary>
public interface IApplicationWindow
{
    /// <summary>Title bar text; changes take effect immediately.</summary>
    string Title { get; set; }

    /// <summary>Client area in screen coordinates.</summary>
    int Width { get; }

    int Height { get; }

    /// <summary>Rendering resolution in pixels (larger than the client area on high-DPI displays).</summary>
    int FramebufferWidth { get; }

    int FramebufferHeight { get; }

    WindowMode Mode { get; set; }

    bool VSync { get; set; }

    bool Resizable { get; set; }

    bool TopMost { get; set; }

    /// <summary>Top-left corner in screen coordinates.</summary>
    (int X, int Y) Position { get; set; }

    /// <summary>Resizes the client area (windowed and borderless modes).</summary>
    void SetSize(int width, int height);

    void Center();

    /// <summary>Uses a texture asset (PNG) as the window and taskbar icon, at several resolutions.</summary>
    /// <exception cref="InvalidDataException">The asset is not a readable image.</exception>
    void SetIcon(AssetRef texture);
}

/// <summary>Process-wide application services for game code: the window and quitting.</summary>
public static class Application
{
    private static IApplicationWindow? _window;

    /// <summary>The game window.</summary>
    /// <exception cref="InvalidOperationException">No host window (tests, tools).</exception>
    public static IApplicationWindow Window => _window ?? throw new InvalidOperationException("There is no game window in this process.");

    /// <summary>True when a host window exists.</summary>
    public static bool HasWindow => _window is not null;

    /// <summary>Raised by <see cref="Quit"/>; the host closes the window at the end of the frame.</summary>
    public static event Action? QuitRequested;

    /// <summary>Ends the game after the current frame.</summary>
    public static void Quit() => QuitRequested?.Invoke();

    /// <summary>Set by the game host when its window exists, and cleared when it closes.</summary>
    public static void SetHostWindow(IApplicationWindow? window) => _window = window;
}
