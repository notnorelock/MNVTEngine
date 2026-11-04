namespace MNVTEngine.Graphics.Vulkan;

public sealed record VulkanDeviceOptions
{
    /// <summary>Presents with FIFO (wait for vertical blank). Off: MAILBOX or IMMEDIATE when the surface offers them.</summary>
    public bool VSync { get; init; } = true;

    /// <summary>
    /// Khronos validation layer. Null: on in Debug builds of the engine when the layer is installed. The environment
    /// variable <c>MNVT_VULKAN_VALIDATION</c> (1/0) overrides both.
    /// </summary>
    public bool? Validation { get; init; }

    /// <summary>File for the driver's pipeline cache (loaded at start, saved at dispose); null keeps it in memory only.</summary>
    public string? PipelineCachePath { get; init; }

    /// <summary>Directory for compiled SPIR-V; null keeps it in memory only.</summary>
    public string? ShaderCacheDirectory { get; init; }

    /// <summary>Part of a GPU name to prefer (case-insensitive), e.g. "Intel". Environment variable <c>MNVT_VULKAN_DEVICE</c> overrides it.</summary>
    public string? PreferredDevice { get; init; }

    public string ApplicationName { get; init; } = "MNVTEngine";

    internal (bool Enabled, bool Explicit) ResolveValidation()
    {
        switch (Environment.GetEnvironmentVariable("MNVT_VULKAN_VALIDATION")?.Trim().ToUpperInvariant())
        {
            case "1" or "TRUE" or "ON":
                return (true, true);
            case "0" or "FALSE" or "OFF":
                return (false, true);
        }

        if (Validation is { } requested)
        {
            return (requested, true);
        }
#if DEBUG
        return (true, false);
#else
        return (false, false);
#endif
    }

    internal string? ResolvePreferredDevice() =>
        Environment.GetEnvironmentVariable("MNVT_VULKAN_DEVICE") is { Length: > 0 } fromEnvironment ? fromEnvironment : PreferredDevice;
}
