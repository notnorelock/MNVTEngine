using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Vulkan;

namespace MNVTEngine.Build.Tests;

/// <summary>Exported games need a Vulkan 1.4 GPU to run; tests that launch one skip without it, like the GPU tests.</summary>
internal static class Gpu
{
    private static readonly Lazy<string?> Missing = new(() =>
    {
        try
        {
            VulkanGraphicsDevice.CreateHeadless(16, 16, NullLogger.Instance).Dispose();
            return null;
        }
        catch (GraphicsInitializationException ex)
        {
            return ex.Message;
        }
    });

    public static void SkipWithoutVulkan14()
    {
        if (Missing.Value is { } reason)
        {
            Assert.Skip($"No Vulkan 1.4 device available: {reason}");
        }
    }
}
