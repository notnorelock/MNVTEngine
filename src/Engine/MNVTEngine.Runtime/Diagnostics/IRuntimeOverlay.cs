using MNVTEngine.Graphics;
using MNVTEngine.Scenes;

namespace MNVTEngine.Runtime;

/// <summary>Size of the view the overlay draws into: logical pixels (input coordinates) and the framebuffer scale.</summary>
public readonly record struct OverlayDisplay(int Width, int Height, float FramebufferScale);

/// <summary>Everything a debug overlay needs from its host (standalone window or editor Game View).</summary>
/// <param name="Input">Input of the game: the overlay reads it first and captures it while its windows have focus.</param>
/// <param name="Display">Current view size; queried every frame.</param>
/// <param name="FontPath">TrueType font for the debug UI; null uses the built-in one.</param>
/// <param name="Logs">Recent log entries for the debug console; null shows command output only.</param>
/// <param name="LayoutFile">Where window positions are kept between runs; null keeps them for this run only.</param>
public sealed record RuntimeOverlayContext(
    IGraphicsDevice Device,
    Renderer2D Renderer,
    RuntimeDiagnostics Diagnostics,
    InputState Input,
    Func<OverlayDisplay> Display,
    Func<Scene?> Scene,
    string? FontPath = null,
    Core.Logging.InMemoryLoggerProvider? Logs = null,
    string? LayoutFile = null);

/// <summary>
/// An in-game debug UI drawn over the running game (implemented by MNVTEngine.DebugUI, which only development builds
/// contain). The runtime calls <see cref="BeginFrame"/> before the frame's first update and <see cref="Render"/> after the
/// scene; the overlay must leave the device's render target, viewport and blend state as the game expects.
/// </summary>
public interface IRuntimeOverlay : IDisposable
{
    void Load(RuntimeOverlayContext context);

    /// <summary>Reads input (shortcuts, UI) and decides what gameplay may see this frame.</summary>
    void BeginFrame();

    /// <param name="camera">The scene's view (for world-space debug draws); null without a camera.</param>
    /// <param name="target">Where the game was rendered; null for the back buffer.</param>
    void Render(Camera2D? camera, RenderTarget2D? target);
}
