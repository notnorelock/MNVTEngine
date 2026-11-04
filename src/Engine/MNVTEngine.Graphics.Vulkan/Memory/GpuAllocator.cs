using MNVTEngine.Graphics.Vulkan.Device;
using Silk.NET.Vulkan;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vma;
using VkBufferHandle = Silk.NET.Vulkan.Buffer;
using VkImageHandle = Silk.NET.Vulkan.Image;

namespace MNVTEngine.Graphics.Vulkan.Memory;

/// <summary>How the CPU uses an allocation.</summary>
internal enum MemoryUsage
{
    /// <summary>GPU only (textures, render targets, static buffers). Filled through staging copies.</summary>
    DeviceLocal,

    /// <summary>Written sequentially by the CPU each frame and read by the GPU (streaming, staging). Persistently mapped.</summary>
    Upload,

    /// <summary>Written by the GPU, read by the CPU (pixel readback). Persistently mapped, cached when the device offers it.</summary>
    Readback,
}

internal enum AllocationKind
{
    Buffer,
    Texture,
    RenderTarget,
}

internal sealed unsafe class GpuBuffer
{
    public required VkBufferHandle Handle { get; init; }

    public required VmaAllocation Allocation { get; init; }

    public required ulong Size { get; init; }

    /// <summary>CPU address for <see cref="MemoryUsage.Upload"/> and <see cref="MemoryUsage.Readback"/>; null otherwise.</summary>
    public required byte* Mapped { get; init; }
}

/// <summary>Memory statistics of the engine's own allocations (exact) and of the heaps (from the driver when <see cref="BudgetMeasured"/>).</summary>
/// <param name="BlockBytes">Device memory VMA reserved (vkAllocateMemory) for this device.</param>
/// <param name="AllocatedBytes">Bytes of live allocations placed in those blocks.</param>
/// <param name="DeviceLocalBudgetBytes">How much device-local memory this process may use; null when unknown.</param>
/// <param name="DeviceLocalUsageBytes">Device-local memory this process uses (all allocations, from the driver); null when unknown.</param>
/// <param name="BudgetMeasured">True with VK_EXT_memory_budget; otherwise VMA estimates the budget as 80 % of the heap.</param>
public readonly record struct VulkanMemoryStatistics(
    long BlockBytes,
    long AllocatedBytes,
    int BlockCount,
    int AllocationCount,
    long TextureBytes,
    long RenderTargetBytes,
    long BufferBytes,
    int FailedAllocations,
    long? DeviceLocalBudgetBytes,
    long? DeviceLocalUsageBytes,
    bool BudgetMeasured);

/// <summary>
/// GPU memory through the Vulkan Memory Allocator (VMA 3, via Vortice.VulkanMemoryAllocator): suballocated blocks per
/// memory type, persistent mapping, memory-type choice from usage. Vortice and Silk.NET share the loader, so handles
/// are converted 1:1. Statistics count what the engine allocated, per kind, and the driver's budget when available.
/// </summary>
internal sealed unsafe class GpuAllocator : IDisposable
{
    private static readonly Lock InitLock = new();
    private static bool _vorticeLoaded;
    private readonly VmaAllocator _allocator;
    private readonly int _heapCount;
    private readonly bool _budgetMeasured;
    private readonly long[] _bytesByKind = new long[3];
    private int _failed;

    public GpuAllocator(VulkanContext context)
    {
        lock (InitLock)
        {
            if (!_vorticeLoaded)
            {
                // VMA looks up Vulkan entry points through Vortice's loader; load it once (same vulkan-1 as Silk.NET).
                var loaded = Vortice.Vulkan.Vulkan.vkInitialize();
                if (loaded != VkResult.Success)
                {
                    throw new GraphicsInitializationException($"VMA could not load the Vulkan runtime: {loaded}.");
                }

                _vorticeLoaded = true;
            }
        }

        var info = new VmaAllocatorCreateInfo
        {
            flags = context.MemoryBudgetSupported ? VmaAllocatorCreateFlags.EXTMemoryBudget : VmaAllocatorCreateFlags.None,
            instance = new VkInstance(context.Instance.Handle.Handle),
            physicalDevice = new VkPhysicalDevice(context.PhysicalDevice.Handle),
            device = new VkDevice(context.Device.Handle),
            vulkanApiVersion = new VkVersion(1, 4, 0),
        };
        var result = vmaCreateAllocator(&info, out _allocator);
        if (result != VkResult.Success)
        {
            throw new GraphicsInitializationException($"vmaCreateAllocator failed: {result}.");
        }

        _heapCount = (int)context.MemoryProperties.MemoryHeapCount;
        _budgetMeasured = context.MemoryBudgetSupported;
        DeviceLocalHeaps = Enumerable.Range(0, _heapCount)
            .Where(h => (context.MemoryProperties.MemoryHeaps[h].Flags & MemoryHeapFlags.DeviceLocalBit) != 0)
            .ToArray();
    }

    public int[] DeviceLocalHeaps { get; }

    public GpuBuffer CreateBuffer(ulong size, BufferUsageFlags usage, MemoryUsage memory)
    {
        var bufferInfo = new VkBufferCreateInfo { size = size, usage = (VkBufferUsageFlags)usage, sharingMode = VkSharingMode.Exclusive };
        var allocationInfo = AllocationInfo(memory);
        VkBuffer buffer;
        VmaAllocation allocation;
        VmaAllocationInfo result;
        var status = vmaCreateBuffer(_allocator, &bufferInfo, &allocationInfo, &buffer, &allocation, &result);
        ThrowIfFailed(status, $"buffer of {size} bytes");
        _bytesByKind[(int)AllocationKind.Buffer] += (long)size;
        return new GpuBuffer { Handle = new VkBufferHandle(buffer.Handle), Allocation = allocation, Size = size, Mapped = (byte*)result.pMappedData };
    }

    public (VkImageHandle Image, VmaAllocation Allocation) CreateImage(in ImageCreateInfo create, AllocationKind kind)
    {
        var imageInfo = new VkImageCreateInfo
        {
            imageType = VkImageType.Image2D,
            format = (VkFormat)create.Format,
            extent = new VkExtent3D(create.Extent.Width, create.Extent.Height, 1),
            mipLevels = create.MipLevels,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = (VkImageUsageFlags)create.Usage,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined,
        };
        var allocationInfo = AllocationInfo(MemoryUsage.DeviceLocal);
        if (kind == AllocationKind.RenderTarget)
        {
            allocationInfo.flags |= VmaAllocationCreateFlags.DedicatedMemory; // large, long-lived, recreated on resize
        }

        VkImage image;
        VmaAllocation allocation;
        VmaAllocationInfo result;
        var status = vmaCreateImage(_allocator, &imageInfo, &allocationInfo, &image, &allocation, &result);
        ThrowIfFailed(status, $"{create.Extent.Width}x{create.Extent.Height} image");
        _bytesByKind[(int)kind] += (long)result.size;
        return (new VkImageHandle(image.Handle), allocation);
    }

    public void DestroyBuffer(GpuBuffer buffer)
    {
        _bytesByKind[(int)AllocationKind.Buffer] -= (long)buffer.Size;
        vmaDestroyBuffer(_allocator, new VkBuffer(buffer.Handle.Handle), buffer.Allocation);
    }

    public void DestroyImage(VkImageHandle image, VmaAllocation allocation, AllocationKind kind)
    {
        VmaAllocationInfo info;
        vmaGetAllocationInfo(_allocator, allocation, &info);
        _bytesByKind[(int)kind] -= (long)info.size;
        vmaDestroyImage(_allocator, new VkImage(image.Handle), allocation);
    }

    /// <summary>Makes CPU writes visible to the GPU (no-op on host-coherent memory).</summary>
    public void Flush(GpuBuffer buffer, ulong offset, ulong size) => vmaFlushAllocation(_allocator, buffer.Allocation, offset, size);

    /// <summary>Makes GPU writes visible to the CPU (no-op on host-coherent memory).</summary>
    public void Invalidate(GpuBuffer buffer, ulong offset, ulong size) => vmaInvalidateAllocation(_allocator, buffer.Allocation, offset, size);

    public VulkanMemoryStatistics Statistics()
    {
        var budgets = stackalloc VmaBudget[_heapCount];
        vmaGetHeapBudgets(_allocator, budgets);
        long blockBytes = 0, allocationBytes = 0, budget = 0, usage = 0;
        int blocks = 0, allocations = 0;
        for (var h = 0; h < _heapCount; h++)
        {
            blockBytes += (long)budgets[h].statistics.blockBytes;
            allocationBytes += (long)budgets[h].statistics.allocationBytes;
            blocks += (int)budgets[h].statistics.blockCount;
            allocations += (int)budgets[h].statistics.allocationCount;
        }

        foreach (var h in DeviceLocalHeaps)
        {
            budget += (long)budgets[h].budget;
            usage += (long)budgets[h].usage;
        }

        return new VulkanMemoryStatistics(
            blockBytes, allocationBytes, blocks, allocations,
            _bytesByKind[(int)AllocationKind.Texture], _bytesByKind[(int)AllocationKind.RenderTarget], _bytesByKind[(int)AllocationKind.Buffer], _failed,
            DeviceLocalHeaps.Length > 0 ? budget : null,
            DeviceLocalHeaps.Length > 0 ? usage : null,
            _budgetMeasured);
    }

    public void Dispose() => vmaDestroyAllocator(_allocator);

    private static VmaAllocationCreateInfo AllocationInfo(MemoryUsage memory) => memory switch
    {
        MemoryUsage.DeviceLocal => new VmaAllocationCreateInfo { usage = VmaMemoryUsage.AutoPreferDevice },
        MemoryUsage.Upload => new VmaAllocationCreateInfo { usage = VmaMemoryUsage.Auto, flags = VmaAllocationCreateFlags.HostAccessSequentialWrite | VmaAllocationCreateFlags.Mapped },
        MemoryUsage.Readback => new VmaAllocationCreateInfo { usage = VmaMemoryUsage.Auto, flags = VmaAllocationCreateFlags.HostAccessRandom | VmaAllocationCreateFlags.Mapped },
        _ => throw new ArgumentOutOfRangeException(nameof(memory), memory, null),
    };

    private void ThrowIfFailed(VkResult status, string what)
    {
        if (status == VkResult.Success)
        {
            return;
        }

        _failed++;
        throw status is VkResult.ErrorOutOfDeviceMemory or VkResult.ErrorOutOfHostMemory
            ? new OutOfGpuMemoryException($"Out of {(status == VkResult.ErrorOutOfDeviceMemory ? "GPU" : "host")} memory allocating a {what}.")
            : new InvalidOperationException($"GPU allocation of a {what} failed: {status}.");
    }
}

/// <summary>The GPU (or the host memory it uses) has no room for an allocation.</summary>
public sealed class OutOfGpuMemoryException : Exception
{
    public OutOfGpuMemoryException()
    {
    }

    public OutOfGpuMemoryException(string message)
        : base(message)
    {
    }

    public OutOfGpuMemoryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
