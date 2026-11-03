using Microsoft.Extensions.Logging;
using MNVTEngine.Core.Logging;
using MNVTEngine.Core.Timing;
using MNVTEngine.Mathematics;
using MNVTEngine.Platform;

namespace MNVTEngine.Graphics.OpenGL.Tests;

/// <summary>Runs a test body inside a hidden window with a live GL 4.5 debug context.</summary>
internal static class GpuHarness
{
    public static IReadOnlyList<LogEntry> Run(Action<GLGraphicsDevice> body)
    {
        using var logs = new InMemoryLoggerProvider(minimumLevel: LogLevel.Debug);
        GameWindow window;
        try
        {
            window = new GameWindow(new GameWindowOptions { Hidden = true, Width = 64, Height = 64, VSync = false, DebugContext = true });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Assert.Skip($"No window system available: {ex.Message}");
            throw;
        }

        using (window)
        {
            var loop = new Loop(body, logs.CreateLogger("GL"));
            try
            {
                window.Run(loop);
            }
            catch (Exception ex) when (!loop.Loaded && ex is not OutOfMemoryException)
            {
                Assert.Skip($"No OpenGL 4.5 context available: {ex.Message}");
            }
        }

        return logs.Snapshot();
    }

    /// <summary>Reads a pixel (x right, y down from the top) from top-down RGBA data.</summary>
    public static Color Pixel(byte[] rgba, int width, int x, int y)
    {
        var i = ((y * width) + x) * 4;
        return Color.FromBytes(rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
    }

    public static void AssertColor(Color expected, Color actual, float tolerance = 2f / 255f) =>
        Assert.True(
            MathF.Abs(expected.R - actual.R) <= tolerance && MathF.Abs(expected.G - actual.G) <= tolerance
            && MathF.Abs(expected.B - actual.B) <= tolerance && MathF.Abs(expected.A - actual.A) <= tolerance,
            $"Expected {expected}, got {actual}");

    private sealed class Loop(Action<GLGraphicsDevice> body, ILogger logger) : IGameLoop
    {
        public bool Loaded { get; private set; }

        public void Load(GameWindow window)
        {
            var device = GLGraphicsDevice.Create(window.GraphicsContext, window.FramebufferWidth, window.FramebufferHeight, logger);
            Loaded = true;
            try
            {
                body(device);
            }
            finally
            {
                device.Dispose();
                window.Close();
            }
        }

        public void FixedUpdate(float fixedDeltaTime)
        {
        }

        public void Update(FrameTime time)
        {
        }

        public void Render(FrameTime time, float interpolation)
        {
        }

        public void Resize(int width, int height)
        {
        }

        public void Unload()
        {
        }
    }
}
