using MNVTEngine.Graphics.Vulkan.Memory;
using Silk.NET.Vulkan;

namespace MNVTEngine.Graphics.Vulkan.Resources;

/// <summary>A texture the device can sample: its image and sampler.</summary>
internal interface IVulkanTexture
{
    VulkanImage Image { get; }

    Sampler VkSampler { get; }
}

internal sealed class VulkanTexture : Texture2D, IVulkanTexture
{
    private readonly VulkanGraphicsDevice _device;

    public VulkanTexture(VulkanGraphicsDevice device, string debugName, VulkanImage image, Sampler sampler, SamplerState state)
        : base(device.Resources, debugName, image.Width, image.Height, state)
    {
        _device = device;
        Image = image;
        VkSampler = sampler;
    }

    public VulkanImage Image { get; }

    public Sampler VkSampler { get; }

    protected override void ReleaseCore() => _device.DestroyLater(Image);
}

/// <summary>
/// Offscreen color target. With the device's flipped viewport, row 0 is the top of the image, as for uploaded
/// textures, so it is not <see cref="Texture2D.IsFlippedVertically"/>.
/// </summary>
internal sealed class VulkanRenderTarget : RenderTarget2D, IVulkanTexture
{
    private readonly VulkanGraphicsDevice _device;

    public VulkanRenderTarget(VulkanGraphicsDevice device, string debugName, VulkanImage image, Sampler sampler, SamplerState state)
        : base(device.Resources, debugName, image.Width, image.Height, state)
    {
        _device = device;
        Image = image;
        VkSampler = sampler;
    }

    public VulkanImage Image { get; }

    public Sampler VkSampler { get; }

    public override bool IsFlippedVertically => false;

    protected override void ReleaseCore() => _device.DestroyLater(Image);
}

/// <summary>
/// "Replace the contents" vertex buffer: each <see cref="VertexBuffer.SetData{T}"/> writes into the current frame's
/// streaming memory and later draws read from there, so data still read by frames in flight is never overwritten.
/// </summary>
internal sealed unsafe class VulkanVertexBuffer(VulkanGraphicsDevice device, string debugName, VertexLayout layout, int capacity)
    : VertexBuffer(device.Resources, debugName, layout, capacity)
{
    public StreamAllocation Current { get; private set; }

    /// <summary>Frame the data was written in; drawing it in a later frame would read reused memory.</summary>
    public long Frame { get; private set; } = -1;

    protected override void SetDataCore(ReadOnlySpan<byte> data)
    {
        Current = device.Stream(data, (ulong)Layout.Stride);
        Frame = device.FrameNumber;
    }

    protected override void ReleaseCore()
    {
        // Streaming memory belongs to the frame slots; nothing to free.
    }
}

/// <summary>Immutable indices live in device-local memory (staged); dynamic indices stream like <see cref="VulkanVertexBuffer"/>.</summary>
internal sealed unsafe class VulkanIndexBuffer : IndexBuffer
{
    private readonly VulkanGraphicsDevice _device;
    private readonly GpuBuffer? _static;

    public VulkanIndexBuffer(VulkanGraphicsDevice device, string debugName, ReadOnlySpan<ushort> indices)
        : base(device.Resources, debugName, indices.Length)
    {
        _device = device;
        _static = device.CreateStaticBuffer(System.Runtime.InteropServices.MemoryMarshal.AsBytes(indices), BufferUsageFlags.IndexBufferBit, debugName);
        Current = new StreamAllocation(_static.Handle, 0, null);
    }

    public VulkanIndexBuffer(VulkanGraphicsDevice device, string debugName, int capacity)
        : base(device.Resources, debugName, capacity, isDynamic: true)
    {
        _device = device;
    }

    public StreamAllocation Current { get; private set; }

    public long Frame { get; private set; } = -1;

    protected override void SetDataCore(ReadOnlySpan<ushort> indices)
    {
        Current = _device.Stream(System.Runtime.InteropServices.MemoryMarshal.AsBytes(indices), sizeof(ushort));
        Frame = _device.FrameNumber;
    }

    protected override void ReleaseCore()
    {
        if (_static is not null)
        {
            _device.DestroyLater(_static);
        }
    }
}
