using Microsoft.Extensions.Logging;
using MNVTEngine.Graphics.Vulkan.Device;
using MNVTEngine.Graphics.Vulkan.Instance;
using MNVTEngine.Graphics.Vulkan.Resources;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace MNVTEngine.Graphics.Vulkan.Swapchain;

/// <summary>
/// The window's swapchain. Format: B8G8R8A8 or R8G8B8A8 UNORM in sRGB-nonlinear color space (values are written as is,
/// like GL's default framebuffer). Present mode: FIFO with VSync (always supported), otherwise MAILBOX, then IMMEDIATE,
/// then FIFO, whichever the surface offers first. Each image has its own render-finished semaphore.
/// </summary>
internal sealed unsafe partial class VulkanSwapchain : IDisposable
{
    private readonly VulkanContext _context;
    private readonly ILogger _logger;

    private VulkanSwapchain(VulkanContext context, ILogger logger)
    {
        _context = context;
        _logger = logger;
    }

    public SwapchainKHR Handle { get; private set; }

    public Format Format { get; private set; }

    public Extent2D Extent { get; private set; }

    public PresentModeKHR PresentMode { get; private set; }

    public VulkanImage[] Images { get; private set; } = [];

    public VkSemaphore[] RenderFinished { get; private set; } = [];

    /// <summary>The images can be copied from (screenshots, ReadPixels of the back buffer).</summary>
    public bool SupportsReadback { get; private set; }

    /// <returns>Null when the surface currently has a zero extent (minimized).</returns>
    public static VulkanSwapchain? Create(VulkanContext context, SurfaceKHR surface, int width, int height, bool vsync, VulkanSwapchain? old, ILogger logger)
    {
        var khr = context.KhrSurface!;
        SurfaceCapabilitiesKHR caps;
        VulkanInstance.Check(khr.GetPhysicalDeviceSurfaceCapabilities(context.PhysicalDevice, surface, &caps), "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
        var extent = caps.CurrentExtent.Width != uint.MaxValue
            ? caps.CurrentExtent
            : new Extent2D(
                Math.Clamp((uint)Math.Max(width, 1), caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
                Math.Clamp((uint)Math.Max(height, 1), caps.MinImageExtent.Height, caps.MaxImageExtent.Height));
        if (extent.Width == 0 || extent.Height == 0)
        {
            return null;
        }

        var format = ChooseFormat(Formats(khr, context.PhysicalDevice, surface));
        var mode = ChoosePresentMode(PresentModes(khr, context.PhysicalDevice, surface), vsync);
        var imageCount = caps.MinImageCount + 1;
        if (caps.MaxImageCount > 0)
        {
            imageCount = Math.Min(imageCount, caps.MaxImageCount);
        }

        var usage = ImageUsageFlags.ColorAttachmentBit;
        var readback = (caps.SupportedUsageFlags & ImageUsageFlags.TransferSrcBit) != 0;
        if (readback)
        {
            usage |= ImageUsageFlags.TransferSrcBit;
        }

        var families = stackalloc uint[] { context.GraphicsFamily, context.PresentFamily };
        var info = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = surface,
            MinImageCount = imageCount,
            ImageFormat = format.Format,
            ImageColorSpace = format.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = usage,
            ImageSharingMode = context.GraphicsFamily == context.PresentFamily ? SharingMode.Exclusive : SharingMode.Concurrent,
            QueueFamilyIndexCount = context.GraphicsFamily == context.PresentFamily ? 0u : 2u,
            PQueueFamilyIndices = families,
            PreTransform = caps.CurrentTransform,
            CompositeAlpha = (caps.SupportedCompositeAlpha & CompositeAlphaFlagsKHR.OpaqueBitKhr) != 0 ? CompositeAlphaFlagsKHR.OpaqueBitKhr : CompositeAlphaFlagsKHR.InheritBitKhr,
            PresentMode = mode,
            Clipped = true,
            OldSwapchain = old?.Handle ?? default,
        };

        var swapchain = new VulkanSwapchain(context, logger) { Format = format.Format, Extent = extent, PresentMode = mode, SupportsReadback = readback };
        SwapchainKHR handle;
        VulkanInstance.Check(context.Swapchain!.CreateSwapchain(context.Device, &info, null, &handle), "vkCreateSwapchainKHR");
        swapchain.Handle = handle;
        try
        {
            swapchain.CreateImages();
        }
        catch
        {
            swapchain.Dispose();
            throw;
        }

        swapchain.LogCreated(extent.Width, extent.Height, format.Format, mode, swapchain.Images.Length);
        return swapchain;
    }

    public void Dispose()
    {
        var vk = _context.Vk;
        foreach (var semaphore in RenderFinished)
        {
            vk.DestroySemaphore(_context.Device, semaphore, null);
        }

        foreach (var image in Images)
        {
            vk.DestroyImageView(_context.Device, image.View, null);
        }

        if (Handle.Handle != 0)
        {
            _context.Swapchain!.DestroySwapchain(_context.Device, Handle, null);
        }

        Images = [];
        RenderFinished = [];
    }

    internal static SurfaceFormatKHR ChooseFormat(IReadOnlyList<SurfaceFormatKHR> formats)
    {
        foreach (var preferred in (ReadOnlySpan<Format>)[Format.B8G8R8A8Unorm, Format.R8G8B8A8Unorm])
        {
            foreach (var format in formats)
            {
                if (format.Format == preferred && format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                {
                    return format;
                }
            }
        }

        return formats.Count > 0 ? formats[0] : throw new GraphicsInitializationException("The window surface reports no formats.");
    }

    internal static PresentModeKHR ChoosePresentMode(IReadOnlyList<PresentModeKHR> modes, bool vsync)
    {
        if (!vsync)
        {
            foreach (var preferred in (ReadOnlySpan<PresentModeKHR>)[PresentModeKHR.MailboxKhr, PresentModeKHR.ImmediateKhr])
            {
                if (modes.Contains(preferred))
                {
                    return preferred;
                }
            }
        }

        return PresentModeKHR.FifoKhr; // the only mode every implementation must support
    }

    private void CreateImages()
    {
        var vk = _context.Vk;
        uint count = 0;
        _context.Swapchain!.GetSwapchainImages(_context.Device, Handle, &count, null);
        var images = new Silk.NET.Vulkan.Image[count];
        fixed (Silk.NET.Vulkan.Image* p = images)
        {
            _context.Swapchain.GetSwapchainImages(_context.Device, Handle, &count, p);
        }

        Images = new VulkanImage[count];
        RenderFinished = new VkSemaphore[count];
        for (var i = 0; i < count; i++)
        {
            Images[i] = new VulkanImage
            {
                Handle = images[i],
                View = VulkanImage.CreateView(vk, _context.Device, images[i], Format, 1),
                Format = Format,
                Width = (int)Extent.Width,
                Height = (int)Extent.Height,
                IsSwapchainImage = true,
            };
            _context.SetName(ObjectType.Image, images[i].Handle, $"Swapchain[{i}]");
            var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
            VkSemaphore semaphore;
            VulkanInstance.Check(vk.CreateSemaphore(_context.Device, &semaphoreInfo, null, &semaphore), "vkCreateSemaphore");
            RenderFinished[i] = semaphore;
        }
    }

    private static SurfaceFormatKHR[] Formats(KhrSurface khr, PhysicalDevice device, SurfaceKHR surface)
    {
        uint count = 0;
        khr.GetPhysicalDeviceSurfaceFormats(device, surface, &count, null);
        var formats = new SurfaceFormatKHR[count];
        fixed (SurfaceFormatKHR* p = formats)
        {
            khr.GetPhysicalDeviceSurfaceFormats(device, surface, &count, p);
        }

        return formats;
    }

    private static PresentModeKHR[] PresentModes(KhrSurface khr, PhysicalDevice device, SurfaceKHR surface)
    {
        uint count = 0;
        khr.GetPhysicalDeviceSurfacePresentModes(device, surface, &count, null);
        var modes = new PresentModeKHR[count];
        fixed (PresentModeKHR* p = modes)
        {
            khr.GetPhysicalDeviceSurfacePresentModes(device, surface, &count, p);
        }

        return modes;
    }

    [LoggerMessage(EventId = 1105, Level = LogLevel.Information, Message = "Swapchain {Width}x{Height} {Format}, {Mode}, {Count} images")]
    private partial void LogCreated(uint width, uint height, Format format, PresentModeKHR mode, int count);
}
