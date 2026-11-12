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

    /// <summary>
    /// Reads precompiled SPIR-V by entry name (<c>Shaders/&lt;hash&gt;.spv</c>, from <see cref="Shaders.VulkanShaderPrecompiler"/>);
    /// returns null for a shader that was not precompiled, which is then compiled at run time.
    /// </summary>
    public Func<string, byte[]?>? PrecompiledShaders { get; init; }

    /// <summary>
    /// Creates the GLSL compiler for shaders that are neither precompiled nor cached; the device owns it. Null (exported
    /// games): such a shader fails with <see cref="ShaderCompilationException"/>. Development hosts pass shaderc
    /// (<c>WithShaderc()</c> from MNVTEngine.Graphics.Vulkan.ShaderCompiler).
    /// </summary>
    public Func<Shaders.IGlslCompiler>? ShaderCompiler { get; init; }

    /// <summary>
    /// Options for an application window: pipeline and SPIR-V caches in <c>%LOCALAPPDATA%\MNVTEngine\GpuCache\&lt;name&gt;</c>
    /// (per user, per application; safe to delete).
    /// </summary>
    /// <param name="validation">True forces the validation layer (e.g. <c>--gpu-validation</c>); false leaves the default.</param>
    public static VulkanDeviceOptions ForApplication(string applicationName, bool vsync, bool validation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        var safeName = string.Concat(applicationName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MNVTEngine", "GpuCache", safeName);
        return new VulkanDeviceOptions
        {
            ApplicationName = applicationName,
            VSync = vsync,
            Validation = validation ? true : null,
            PipelineCachePath = Path.Combine(cache, "pipelines.bin"),
            ShaderCacheDirectory = Path.Combine(cache, "Shaders"),
        };
    }

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
