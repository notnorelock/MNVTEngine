using MNVTEngine.Graphics.Vulkan.Memory;
using Silk.NET.Vulkan;
using Vortice.Vulkan;
using VkImageHandle = Silk.NET.Vulkan.Image;

namespace MNVTEngine.Graphics.Vulkan.Resources;

/// <summary>
/// A 2D color image with one view and its tracked layout. The device records every layout change through
/// <see cref="Barriers.Transition"/>, so <see cref="Layout"/> is the layout after all commands recorded so far.
/// Swapchain images have no allocation (the swapchain owns them).
/// </summary>
internal sealed unsafe class VulkanImage
{
    public required VkImageHandle Handle { get; init; }

    public required ImageView View { get; init; }

    public required Format Format { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public uint MipLevels { get; init; } = 1;

    public VmaAllocation Allocation { get; init; }

    public AllocationKind Kind { get; init; }

    public ImageLayout Layout { get; set; } = ImageLayout.Undefined;

    /// <summary>Pipeline stage of the last use, for the source half of the next barrier.</summary>
    public PipelineStageFlags2 LastStage { get; set; } = PipelineStageFlags2.None;

    public bool IsSwapchainImage { get; init; }

    public static ImageView CreateView(Vk vk, Silk.NET.Vulkan.Device device, VkImageHandle image, Format format, uint mipLevels)
    {
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, mipLevels, 0, 1),
        };
        ImageView view;
        Instance.VulkanInstance.Check(vk.CreateImageView(device, &info, null, &view), "vkCreateImageView");
        return view;
    }
}

/// <summary>Image layout transitions with Synchronization2. Stage and access masks follow from the layouts.</summary>
internal static unsafe class Barriers
{
    /// <summary>Records a transition of all mip levels to <paramref name="layout"/>; nothing when already there and the use is read-only.</summary>
    public static void Transition(Vk vk, CommandBuffer cmd, VulkanImage image, ImageLayout layout, GraphicsStatistics? statistics = null)
    {
        if (image.Layout == layout && layout == ImageLayout.ShaderReadOnlyOptimal)
        {
            return; // read after read needs no barrier
        }

        var (dstStage, dstAccess) = Usage(layout, destination: true);
        var (srcStage, srcAccess) = image.Layout == ImageLayout.Undefined
            ? (image.IsSwapchainImage ? PipelineStageFlags2.ColorAttachmentOutputBit : PipelineStageFlags2.None, AccessFlags2.None)
            : (image.LastStage, Usage(image.Layout, destination: false).Access);
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = srcStage,
            SrcAccessMask = srcAccess,
            DstStageMask = dstStage,
            DstAccessMask = dstAccess,
            OldLayout = image.Layout,
            NewLayout = layout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image.Handle,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, Vk.RemainingMipLevels, 0, 1),
        };
        var dependency = new DependencyInfo { SType = StructureType.DependencyInfo, ImageMemoryBarrierCount = 1, PImageMemoryBarriers = &barrier };
        vk.CmdPipelineBarrier2(cmd, &dependency);
        image.Layout = layout;
        image.LastStage = dstStage;
        statistics?.RecordBarrier();
    }

    /// <summary>Makes transfer writes (staging uploads) visible to every later read of buffers and images.</summary>
    public static void UploadsToReads(Vk vk, CommandBuffer cmd)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AllTransferBit,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.VertexInputBit | PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit,
            DstAccessMask = AccessFlags2.IndexReadBit | AccessFlags2.VertexAttributeReadBit | AccessFlags2.UniformReadBit | AccessFlags2.ShaderSampledReadBit,
        };
        var dependency = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &barrier };
        vk.CmdPipelineBarrier2(cmd, &dependency);
    }

    private static (PipelineStageFlags2 Stage, AccessFlags2 Access) Usage(ImageLayout layout, bool destination) => layout switch
    {
        ImageLayout.ColorAttachmentOptimal => (PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.ColorAttachmentReadBit),
        ImageLayout.ShaderReadOnlyOptimal => (PipelineStageFlags2.FragmentShaderBit, destination ? AccessFlags2.ShaderSampledReadBit : AccessFlags2.None),
        ImageLayout.TransferSrcOptimal => (PipelineStageFlags2.AllTransferBit, destination ? AccessFlags2.TransferReadBit : AccessFlags2.None),
        ImageLayout.TransferDstOptimal => (PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit),
        ImageLayout.PresentSrcKhr => (PipelineStageFlags2.None, AccessFlags2.None),
        _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Layout not used by the device."),
    };
}
