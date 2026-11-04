using MNVTEngine.Graphics.Vulkan.Device;
using MNVTEngine.Graphics.Vulkan.Instance;
using MNVTEngine.Graphics.Vulkan.Memory;
using Silk.NET.Vulkan;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace MNVTEngine.Graphics.Vulkan.Commands;

/// <summary>
/// Everything one frame in flight owns: a command pool (reset as a whole when the slot comes round again), its command
/// buffers, the fence the frame's last submit signals, the semaphore that swapchain acquisition signals, and the
/// streaming memory for the frame's dynamic data.
/// </summary>
internal sealed unsafe class FrameSlot : IDisposable
{
    private readonly VulkanContext _context;
    private readonly List<CommandBuffer> _buffers = [];
    private int _next;

    public FrameSlot(VulkanContext context, GpuAllocator allocator, int index)
    {
        _context = context;
        var vk = context.Vk;
        var poolInfo = new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo, Flags = CommandPoolCreateFlags.TransientBit, QueueFamilyIndex = context.GraphicsFamily };
        CommandPool pool;
        VulkanInstance.Check(vk.CreateCommandPool(context.Device, &poolInfo, null, &pool), "vkCreateCommandPool");
        Pool = pool;

        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
        Fence fence;
        VulkanInstance.Check(vk.CreateFence(context.Device, &fenceInfo, null, &fence), "vkCreateFence");
        Fence = fence;

        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        VkSemaphore semaphore;
        VulkanInstance.Check(vk.CreateSemaphore(context.Device, &semaphoreInfo, null, &semaphore), "vkCreateSemaphore");
        ImageAvailable = semaphore;

        Stream = new StreamingBuffer(allocator, 4 * 1024 * 1024);
        context.SetName(ObjectType.CommandPool, pool.Handle, $"Frame{index}.Commands");
        context.SetName(ObjectType.Fence, fence.Handle, $"Frame{index}.Fence");
        context.SetName(ObjectType.Semaphore, semaphore.Handle, $"Frame{index}.ImageAvailable");
    }

    public CommandPool Pool { get; }

    public Fence Fence { get; }

    public VkSemaphore ImageAvailable { get; }

    public StreamingBuffer Stream { get; }

    /// <summary>Command buffers recorded this frame (more than one after a mid-frame readback).</summary>
    public int CommandBuffersUsed => _next;

    /// <summary>Starts the slot's frame. The caller has waited for <see cref="Fence"/>.</summary>
    public void Reset()
    {
        VulkanInstance.Check(_context.Vk.ResetCommandPool(_context.Device, Pool, CommandPoolResetFlags.None), "vkResetCommandPool");
        Stream.Reset();
        _next = 0;
    }

    /// <summary>A command buffer in the recording state.</summary>
    public CommandBuffer Begin()
    {
        if (_next == _buffers.Count)
        {
            var info = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = Pool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
            CommandBuffer allocated;
            VulkanInstance.Check(_context.Vk.AllocateCommandBuffers(_context.Device, &info, &allocated), "vkAllocateCommandBuffers");
            _buffers.Add(allocated);
        }

        var cmd = _buffers[_next++];
        var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        VulkanInstance.Check(_context.Vk.BeginCommandBuffer(cmd, &begin), "vkBeginCommandBuffer");
        return cmd;
    }

    public void Dispose()
    {
        var vk = _context.Vk;
        Stream.Dispose();
        vk.DestroySemaphore(_context.Device, ImageAvailable, null);
        vk.DestroyFence(_context.Device, Fence, null);
        vk.DestroyCommandPool(_context.Device, Pool, null); // frees the command buffers
    }
}
