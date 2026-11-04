using Microsoft.Extensions.Logging;
using MNVTEngine.Core.Logging;
using MNVTEngine.Core.Timing;
using MNVTEngine.Mathematics;
using MNVTEngine.Platform;
using static MNVTEngine.Graphics.Vulkan.Tests.VulkanHarness;

namespace MNVTEngine.Graphics.Vulkan.Tests;

/// <summary>A real window: surface, swapchain, clear and present, resize and recreation, back buffer readback.</summary>
[Collection(nameof(VulkanGroup))]
public class VulkanSwapchainTests
{
    [Fact]
    public void Window_ClearsPresents_AndRecreatesTheSwapchainOnResize()
    {
        var logs = RunWindow(160, 120, (window, device) =>
        {
            Assert.Equal((160, 120), (device.BackBufferWidth, device.BackBufferHeight));
            for (var frame = 0; frame < 5; frame++)
            {
                device.SetRenderTarget(null);
                device.Clear(new Color(0f, 0.25f * frame, 0f, 1f));
                device.Present();
            }

            device.ResizeBackBuffer(200, 100); // the hidden window keeps its size: the swapchain is recreated at the surface's extent
            device.SetRenderTarget(null);
            device.Clear(Color.Red);
            var (w, h) = (device.BackBufferWidth, device.BackBufferHeight);
            var pixels = new byte[w * h * 4];
            device.ReadPixels(pixels);
            AssertColor(Color.Red, Pixel(pixels, w, w / 2, h / 2));
            device.Present();
            Assert.Equal(6, device.FrameNumber);
        });
        Assert.Equal(2, logs.Count(e => e.Message.StartsWith("Swapchain ", StringComparison.Ordinal)));
    }

    private static IReadOnlyList<LogEntry> RunWindow(int width, int height, Action<GameWindow, VulkanGraphicsDevice> body)
    {
        using var logs = new InMemoryLoggerProvider(minimumLevel: LogLevel.Debug);
        GameWindow window;
        try
        {
            window = new GameWindow(new GameWindowOptions { Hidden = true, Width = width, Height = height, VSync = false, OpenGL = false });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Assert.Skip($"No window system available: {ex.Message}");
            throw;
        }

        using (window)
        {
            var loop = new Loop(body, logs.CreateLogger("Vulkan"));
            window.Run(loop);
            if (loop.Skipped is { } reason)
            {
                Assert.Skip(reason);
            }

            loop.Failure?.Throw();
            AssertNoValidationErrors(loop.Validation);
        }

        return logs.Snapshot();
    }

    private sealed class Loop(Action<GameWindow, VulkanGraphicsDevice> body, ILogger logger) : IGameLoop
    {
        public string? Skipped { get; private set; }

        public System.Runtime.ExceptionServices.ExceptionDispatchInfo? Failure { get; private set; }

        public VulkanValidationCountersSnapshot Validation { get; private set; }

        public void Load(GameWindow window)
        {
            try
            {
                VulkanGraphicsDevice device;
                try
                {
                    device = VulkanGraphicsDevice.Create(window.VulkanSurface!, window.FramebufferWidth, window.FramebufferHeight, logger, new VulkanDeviceOptions { Validation = true, VSync = false });
                }
                catch (GraphicsInitializationException ex)
                {
                    Skipped = $"No Vulkan 1.4 device available: {ex.Message}";
                    return;
                }

                try
                {
                    body(window, device);
                }
                finally
                {
                    device.Dispose();
                    Validation = new VulkanValidationCountersSnapshot(device.Validation.Enabled, device.Validation.Errors, device.Validation.LastError);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); // rethrown outside the window loop
            }
            finally
            {
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
