using Microsoft.Extensions.Logging;
using MNVTEngine.Core.Logging;
using MNVTEngine.Mathematics;
using MNVTEngine.Graphics.Vulkan.ShaderCompilation;

namespace MNVTEngine.Graphics.Vulkan.Tests;

/// <summary>Runs a test body against a headless Vulkan device with validation requested; fails on validation errors.</summary>
internal static class VulkanHarness
{
    public const int Size = 64;

    public static IReadOnlyList<LogEntry> Run(Action<VulkanGraphicsDevice> body, VulkanDeviceOptions? options = null, int width = Size, int height = Size)
    {
        using var logs = new InMemoryLoggerProvider(minimumLevel: LogLevel.Debug);
        VulkanGraphicsDevice device;
        try
        {
            device = VulkanGraphicsDevice.CreateHeadless(width, height, logs.CreateLogger("Vulkan"), (options ?? new VulkanDeviceOptions { Validation = true }).WithShaderc());
        }
        catch (GraphicsInitializationException ex)
        {
            Assert.Skip($"No Vulkan 1.4 device available: {ex.Message}");
            throw;
        }

        VulkanValidationCountersSnapshot validation;
        try
        {
            body(device);
        }
        finally
        {
            device.Dispose();
            validation = new VulkanValidationCountersSnapshot(device.Validation.Enabled, device.Validation.Errors, device.Validation.LastError);
        }

        AssertNoValidationErrors(validation);
        return logs.Snapshot();
    }

    public static void AssertNoValidationErrors(VulkanValidationCountersSnapshot validation) =>
        Assert.True(validation.Errors == 0, $"{validation.Errors} Vulkan validation error(s); last: {validation.LastError}");

    public static Camera2D PixelCamera(int size = Size)
    {
        var camera = new Camera2D(size, size) { Position = new Vector2(size / 2f, size / 2f) };
        camera.UsePixelUnits();
        return camera;
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

    public static byte[] RenderToTarget(IGraphicsDevice device, Renderer2D renderer, Color clear, Action<SpriteBatch> draw, SpriteSortMode sort = SpriteSortMode.Deferred, Material? material = null)
    {
        using var target = device.CreateRenderTarget(Size, Size, SamplerState.PointClamp);
        var batch = renderer.Begin(PixelCamera(), sort, material, target);
        device.Clear(clear);
        draw(batch);
        renderer.End();
        var pixels = new byte[Size * Size * 4];
        device.ReadPixels(pixels);
        device.SetRenderTarget(null);
        return pixels;
    }
}

internal readonly record struct VulkanValidationCountersSnapshot(bool Enabled, int Errors, string? LastError);

/// <summary>Vulkan devices and GLFW windows are created one test at a time.</summary>
[CollectionDefinition(nameof(VulkanGroup), DisableParallelization = true)]
public sealed class VulkanGroup;
