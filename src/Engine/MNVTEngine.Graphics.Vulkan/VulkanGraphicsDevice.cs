using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MNVTEngine.Graphics.Vulkan.Commands;
using MNVTEngine.Graphics.Vulkan.Device;
using MNVTEngine.Graphics.Vulkan.Diagnostics;
using MNVTEngine.Graphics.Vulkan.Instance;
using MNVTEngine.Graphics.Vulkan.Memory;
using MNVTEngine.Graphics.Vulkan.Pipelines;
using MNVTEngine.Graphics.Vulkan.Resources;
using MNVTEngine.Graphics.Vulkan.Shaders;
using MNVTEngine.Graphics.Vulkan.Swapchain;
using MNVTEngine.Mathematics;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace MNVTEngine.Graphics.Vulkan;

/// <summary>
/// Vulkan 1.4 implementation of the immediate-mode <see cref="IGraphicsDevice"/>. Calls are recorded into the current
/// frame's command buffer and submitted by <see cref="Present"/>. Render passes are dynamic rendering instances begun on
/// the first clear or draw of a target. Image layouts are tracked and transitioned with Synchronization2 barriers.
/// Per-draw data (texture, uniform blocks) is bound with push descriptors. Dynamic vertices, indices and uniforms
/// stream through per-frame memory. Destroyed objects wait until the frames that may use them have completed.
/// See docs/graphics/vulkan-migration.md for the synchronization model.
/// </summary>
public sealed unsafe partial class VulkanGraphicsDevice : IGraphicsDevice
{
    public const int FramesInFlight = 2;
    private const Format TargetFormat = Format.R8G8B8A8Unorm;

    private readonly ILogger _logger;
    private readonly VulkanDeviceOptions _options;
    private readonly IVkSurface? _surfaceSource;
    private readonly KhrSurface? _khrSurface;
    private readonly VulkanInstance _instance;
    private readonly GpuAllocator _allocator;
    private readonly ShaderCompiler _compiler;
    private readonly PipelineFactory _pipelines;
    private readonly FrameSlot[] _slots;
    private readonly Fence _immediateFence;
    private readonly VulkanGpuTimer? _timer;
    private readonly Dictionary<SamplerState, Sampler> _samplers = [];
    private readonly Queue<(long Frame, Action Destroy)> _destroyQueue = new();
    // 16 KB per stage: the default uniforms, plus GPU skinning's 128 joint matrices (8 KB) in the vertex stage.
    private readonly byte[] _vertexBlock = new byte[16384];
    private readonly byte[] _fragmentBlock = new byte[16384];
    private readonly byte[] _lastVertexBlock = new byte[16384];
    private readonly byte[] _lastFragmentBlock = new byte[16384];
    private SurfaceKHR _surface;
    private VulkanSwapchain? _swapchain;
    private VulkanImage? _headlessBackBuffer;
    private VulkanImage? _backBufferDepth;
    private VulkanImage? _renderingDepth;
    private Format _depthFormat;
    private bool _swapchainDirty;
    private bool _surfaceLost;
    private bool _disposed;
    private bool _vsync;

    // Frame state
    private bool _recording;
    private CommandBuffer _cmd;
    private CommandBuffer _uploadCmd;
    private int _imageIndex = -1;
    private bool _acquireWaitPending;

    // Recording state (valid within _cmd)
    private RenderTarget2D? _currentTarget;
    private VulkanImage? _renderingImage;
    private Rect _viewport;
    private bool _viewportDirty = true;
    private Rect2D _scissor;
    private Pipeline _boundPipeline;
    private VulkanShader? _lastShader;
    private ImageView _lastTextureView;
    private PipelineLayout _pushedLayout;
    private (VkBuffer Buffer, ulong Offset) _boundVertices;
    private (VkBuffer Buffer, ulong Offset) _boundIndices;
    private (VkBuffer Buffer, ulong Offset, int Size) _vertexUniforms;
    private (VkBuffer Buffer, ulong Offset, int Size) _fragmentUniforms;
    private readonly (ImageView View, Sampler Sampler)[] _pushedTextures = new (ImageView, Sampler)[ShaderCompiler.MaxBindings];
    private readonly List<(uint Binding, IVulkanTexture Texture)> _drawTextures = [];

    private VulkanGraphicsDevice(IVkSurface? surfaceSource, int width, int height, ILogger logger, VulkanDeviceOptions options)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _options = options;
        _surfaceSource = surfaceSource;
        BackBufferWidth = width;
        BackBufferHeight = height;
        _vsync = options.VSync;

        Vk vk;
        try
        {
            vk = Vk.GetApi();
        }
        catch (Exception ex) when (ex is DllNotFoundException or FileNotFoundException or EntryPointNotFoundException)
        {
            throw new GraphicsInitializationException("The Vulkan runtime (vulkan-1.dll) was not found. Install a GPU driver with Vulkan 1.4 support.", ex);
        }

        var surfaceExtensions = new List<string>();
        if (surfaceSource is not null)
        {
            var names = surfaceSource.GetRequiredExtensions(out var count);
            surfaceExtensions.AddRange(SilkMarshal.PtrToStringArray((nint)names, (int)count));
        }

        var (validation, explicitValidation) = options.ResolveValidation();
        _instance = new VulkanInstance(vk, options.ApplicationName, surfaceExtensions, validation, explicitValidation, logger);
        try
        {
            if (surfaceSource is not null)
            {
                if (!vk.TryGetInstanceExtension(_instance.Handle, out KhrSurface khrSurface))
                {
                    throw new GraphicsInitializationException("VK_KHR_surface could not be loaded.");
                }

                _khrSurface = khrSurface;
                _surface = CreateSurface();
            }

            Context = new VulkanContext(_instance, _surface, _khrSurface, options.ResolvePreferredDevice(), logger);
            _allocator = new GpuAllocator(Context);
            _compiler = new ShaderCompiler(options.ShaderCacheDirectory, logger, options.PrecompiledShaders);
            _pipelines = new PipelineFactory(Context, options.PipelineCachePath, logger);
            _slots = new FrameSlot[FramesInFlight];
            for (var i = 0; i < FramesInFlight; i++)
            {
                _slots[i] = new FrameSlot(Context, _allocator, i);
            }

            var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
            Fence fence;
            VulkanInstance.Check(vk.CreateFence(Context.Device, &fenceInfo, null, &fence), "vkCreateFence");
            _immediateFence = fence;

            if (Context.TimestampValidBits > 0 && Context.Limits.TimestampPeriod > 0)
            {
                _timer = new VulkanGpuTimer(this, FramesInFlight);
            }

            if (surfaceSource is null)
            {
                _headlessBackBuffer = CreateImage(width, height, 1, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit, AllocationKind.RenderTarget, "BackBuffer");
            }
            else
            {
                _swapchain = VulkanSwapchain.Create(Context, _surface, width, height, options.VSync, null, logger);
                if (_swapchain is not null)
                {
                    (BackBufferWidth, BackBufferHeight) = ((int)_swapchain.Extent.Width, (int)_swapchain.Extent.Height);
                }
            }
        }
        catch
        {
            ReleaseNative();
            throw;
        }

        Description = $"Vulkan {VulkanVersion.Format(Context.Properties.ApiVersion)} | {Context.DeviceName} | {Context.DriverDescription}";
        _viewport = new Rect(0, 0, BackBufferWidth, BackBufferHeight);
        LogCreated(Description, _instance.Validation.Enabled, surfaceSource is null);
    }

    public GraphicsResourceTracker Resources { get; } = new();

    public string Description { get; }

    public bool VSync
    {
        get => _vsync;
        set
        {
            if (value != _vsync)
            {
                _vsync = value;
                _swapchainDirty = _surfaceSource is not null; // present mode is fixed per swapchain
            }
        }
    }

    public int BackBufferWidth { get; private set; }

    public int BackBufferHeight { get; private set; }

    public GraphicsStatistics Statistics => Resources.Statistics;

    public IGpuTimer? GpuTimer => _timer;

    /// <summary>Validation layer state and message counts.</summary>
    public VulkanValidationCounters Validation => _instance.Validation;

    /// <summary>The engine's GPU allocations and, with VK_EXT_memory_budget, the driver's budget. Cheap: no GPU sync.</summary>
    public GpuAllocationStatistics MemoryStatistics => _allocator.Statistics();

    public GpuAllocationStatistics? AllocationStatistics => _allocator.Statistics();

    public IReadOnlyList<KeyValuePair<string, string>> DescribeBackend()
    {
        var validation = _instance.Validation;
        var budget = Context.MemoryBudgetSupported ? "VK_EXT_memory_budget (measured)" : "not exposed: VMA estimates 80 % of each heap";
        return
        [
            new("API", $"Vulkan {VulkanVersion.Format(Context.Properties.ApiVersion)} (instance {VulkanVersion.Format(_instance.InstanceVersion)})"),
            new("GPU", $"{Context.DeviceName} ({Context.Properties.DeviceType})"),
            new("Driver", Context.DriverDescription),
            new("Back buffer", SwapchainDescription),
            new("Frames in flight", FramesInFlight.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("Validation", validation.Enabled
                ? $"on: {validation.Errors} errors, {validation.Warnings} warnings" + (validation.LastError is { } last ? $"; last: {last}" : string.Empty)
                : "off (Khronos layer not requested or not installed)"),
            new("Pipelines", $"{_pipelines.Created} created; driver cache {(_options.PipelineCachePath is null ? "in memory" : $"{_pipelines.LoadedBytes / 1024} KB loaded from disk")}"),
            new("Streaming memory", $"{_slots[FrameSlot].Stream.BytesUsed / 1024} KB used of {_slots[FrameSlot].Stream.Capacity / 1024} KB this frame"),
            new("Memory budget", budget),
            new("Shaders", $"{_compiler.Precompiled} stages precompiled, {_compiler.Compiled} compiled at run time"),
            new("GPU timer", _timer is null ? "unsupported: the graphics queue has no timestamps" : $"timestamps, {Context.TimestampValidBits} bits, {Context.Limits.TimestampPeriod:0.##} ns/tick"),
        ];
    }

    /// <summary>Back buffer: swapchain size, format, present mode and image count, or "headless".</summary>
    public string SwapchainDescription => _swapchain is { } s
        ? $"{s.Extent.Width}x{s.Extent.Height} {s.Format}, {s.PresentMode}, {s.Images.Length} images"
        : _headlessBackBuffer is { } b ? $"headless {b.Width}x{b.Height} {b.Format}" : "none (minimized)";

    /// <summary>Pipelines created since start (each one exactly once per shader and state combination).</summary>
    public int PipelinesCreated => _pipelines.Created;

    /// <summary>Number of the frame being recorded (or the next one); increases by one per <see cref="Present"/>.</summary>
    public long FrameNumber { get; private set; }

    internal VulkanContext Context { get; } = null!;

    internal int FrameSlot => (int)(FrameNumber % FramesInFlight);

    internal CommandBuffer CurrentCommandBuffer => _cmd;

    /// <summary>Creates the device for a window's Vulkan surface.</summary>
    /// <param name="surface">The window's surface source; null when the window found no Vulkan loader.</param>
    /// <exception cref="GraphicsInitializationException">No Vulkan 1.4 runtime or GPU, or a required feature is missing.</exception>
    public static VulkanGraphicsDevice Create(IVkSurface? surface, int backBufferWidth, int backBufferHeight, ILogger logger, VulkanDeviceOptions? options = null)
    {
        if (surface is null)
        {
            throw new GraphicsInitializationException("The window has no Vulkan surface: the Vulkan loader (vulkan-1.dll) was not found. Install a GPU driver with Vulkan 1.4 support.");
        }

        return new VulkanGraphicsDevice(surface, backBufferWidth, backBufferHeight, logger, options ?? new VulkanDeviceOptions());
    }

    /// <summary>A device without a window: the back buffer is an offscreen image (tests, offline rendering).</summary>
    public static VulkanGraphicsDevice CreateHeadless(int width, int height, ILogger logger, VulkanDeviceOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        return new VulkanGraphicsDevice(null, width, height, logger, options ?? new VulkanDeviceOptions());
    }

    public GpuMemoryInfo QueryGpuMemory()
    {
        if (!Context.MemoryBudgetSupported)
        {
            return GpuMemoryInfo.Unsupported;
        }

        var stats = _allocator.Statistics();
        long total = 0;
        foreach (var heap in _allocator.DeviceLocalHeaps)
        {
            total += (long)Context.MemoryProperties.MemoryHeaps[heap].Size;
        }

        // Budget = what this process may use; usage = what it uses. Available is the room left for this process.
        var available = Math.Max(0, (stats.DeviceLocalBudgetBytes ?? 0) - (stats.DeviceLocalUsageBytes ?? 0));
        return new GpuMemoryInfo(true, total, available, "VK_EXT_memory_budget");
    }

    public void ResizeBackBuffer(int width, int height)
    {
        ThrowIfDisposed();
        if (width <= 0 || height <= 0 || (width == BackBufferWidth && height == BackBufferHeight && !_swapchainDirty))
        {
            return;
        }

        BackBufferWidth = width;
        BackBufferHeight = height;
        if (_headlessBackBuffer is { } old)
        {
            EndRendering();
            DestroyLater(old);
            _headlessBackBuffer = CreateImage(width, height, 1, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit, AllocationKind.RenderTarget, "BackBuffer");
        }
        else
        {
            _swapchainDirty = true;
        }

        if (_currentTarget is null)
        {
            _viewport = new Rect(0, 0, width, height);
            _viewportDirty = true;
        }
    }

    public Texture2D CreateTexture(int width, int height, ReadOnlySpan<byte> rgba, SamplerState sampler, string debugName = "Texture")
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var bytes = (long)width * height * 4;
        if (rgba.Length < bytes)
        {
            throw new ArgumentException($"Texture '{debugName}' needs {bytes} bytes of RGBA8 data, got {rgba.Length}.", nameof(rgba));
        }

        var mips = sampler.GenerateMipmaps ? (uint)Math.Floor(Math.Log2(Math.Max(width, height))) + 1 : 1u;
        var usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | (mips > 1 ? ImageUsageFlags.TransferSrcBit : 0);
        var image = CreateImage(width, height, mips, usage, AllocationKind.Texture, debugName);
        try
        {
            var staging = _allocator.CreateBuffer((ulong)bytes, BufferUsageFlags.TransferSrcBit, MemoryUsage.Upload);
            rgba[..(int)bytes].CopyTo(new Span<byte>(staging.Mapped, (int)bytes));
            _allocator.Flush(staging, 0, (ulong)bytes);
            DestroyLater(staging);

            var cmd = UploadCommands();
            Barriers.Transition(Context.Vk, cmd, image, ImageLayout.TransferDstOptimal);
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D((uint)width, (uint)height, 1),
            };
            Context.Vk.CmdCopyBufferToImage(cmd, staging.Handle, image.Handle, ImageLayout.TransferDstOptimal, 1, &region);
            if (mips > 1)
            {
                GenerateMipmaps(cmd, image);
            }
            else
            {
                Barriers.Transition(Context.Vk, cmd, image, ImageLayout.ShaderReadOnlyOptimal);
            }

            return new VulkanTexture(this, debugName, image, Sampler(sampler), sampler);
        }
        catch
        {
            DestroyLater(image);
            throw;
        }
    }

    public void UpdateTexture(Texture2D texture, int x, int y, int width, int height, ReadOnlySpan<byte> rgba)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(texture);
        ObjectDisposedException.ThrowIf(texture.IsDisposed, texture);
        if (texture is not VulkanTexture vulkan)
        {
            throw new ArgumentException(texture is VulkanRenderTarget ? "Render targets cannot be updated; draw into them." : "Texture was not created by this device.", nameof(texture));
        }

        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > texture.Width || y + height > texture.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"Rectangle {x},{y} {width}x{height} is outside the {texture.Width}x{texture.Height} texture.");
        }

        var bytes = width * height * 4;
        ArgumentOutOfRangeException.ThrowIfLessThan(rgba.Length, bytes, nameof(rgba));
        if (vulkan.Image.MipLevels > 1)
        {
            throw new NotSupportedException($"Texture '{texture.DebugName}' has mipmaps; updating a region would leave the smaller levels stale.");
        }

        var staging = _allocator.CreateBuffer((ulong)bytes, BufferUsageFlags.TransferSrcBit, MemoryUsage.Upload);
        rgba[..bytes].CopyTo(new Span<byte>(staging.Mapped, bytes));
        _allocator.Flush(staging, 0, (ulong)bytes);
        DestroyLater(staging);
        var cmd = UploadCommands();
        Barriers.Transition(Context.Vk, cmd, vulkan.Image, ImageLayout.TransferDstOptimal);
        var region = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D(x, y, 0),
            ImageExtent = new Extent3D((uint)width, (uint)height, 1),
        };
        Context.Vk.CmdCopyBufferToImage(cmd, staging.Handle, vulkan.Image.Handle, ImageLayout.TransferDstOptimal, 1, &region);
        Barriers.Transition(Context.Vk, cmd, vulkan.Image, ImageLayout.ShaderReadOnlyOptimal);
        Statistics.RecordTextureUpload(bytes);
    }

    public RenderTarget2D CreateRenderTarget(int width, int height, SamplerState sampler, string debugName = "RenderTarget", bool depth = false)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var image = CreateImage(width, height, 1, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit, AllocationKind.RenderTarget, debugName);
        VulkanImage? depthImage = null;
        try
        {
            depthImage = depth ? CreateDepthImage(width, height, debugName + " depth") : null;
        }
        catch
        {
            DestroyLater(image);
            throw;
        }

        return new VulkanRenderTarget(this, debugName, image, Sampler(sampler with { GenerateMipmaps = false }), sampler with { GenerateMipmaps = false }, depthImage);
    }

    public Shader CreateShader(ShaderSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        return VulkanShader.Create(this, source, _compiler);
    }

    public VertexBuffer CreateVertexBuffer(VertexLayout layout, int capacity, string debugName = "VertexBuffer")
    {
        ThrowIfDisposed();
        return new VulkanVertexBuffer(this, debugName, layout, capacity);
    }

    public IndexBuffer CreateIndexBuffer(ReadOnlySpan<ushort> indices, string debugName = "IndexBuffer")
    {
        ThrowIfDisposed();
        return new VulkanIndexBuffer(this, debugName, indices);
    }

    public VertexBuffer CreateStaticVertexBuffer(VertexLayout layout, ReadOnlySpan<byte> vertices, string debugName = "StaticVertexBuffer")
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(layout);
        if (vertices.IsEmpty || vertices.Length % layout.Stride != 0)
        {
            throw new ArgumentException($"Vertex data for '{debugName}' must be a non-empty multiple of the {layout.Stride}-byte stride (got {vertices.Length} bytes).", nameof(vertices));
        }

        var buffer = new VulkanVertexBuffer(this, debugName, layout, vertices);
        Statistics.RecordVertexUpload(buffer.Capacity);
        return buffer;
    }

    public IndexBuffer CreateIndexBuffer(ReadOnlySpan<uint> indices, string debugName = "IndexBuffer")
    {
        ThrowIfDisposed();
        if (indices.IsEmpty)
        {
            throw new ArgumentException($"Index buffer '{debugName}' needs at least one index.", nameof(indices));
        }

        return new VulkanIndexBuffer(this, debugName, indices);
    }

    public IndexBuffer CreateDynamicIndexBuffer(int capacity, string debugName = "DynamicIndexBuffer")
    {
        ThrowIfDisposed();
        return new VulkanIndexBuffer(this, debugName, capacity);
    }

    public void SetRenderTarget(RenderTarget2D? target)
    {
        ThrowIfDisposed();
        if (target is not null)
        {
            ObjectDisposedException.ThrowIf(target.IsDisposed, target);
            if (target is not VulkanRenderTarget)
            {
                throw new ArgumentException("Render target was not created by this device.", nameof(target));
            }
        }

        if (!ReferenceEquals(_currentTarget, target))
        {
            EndRendering();
            Statistics.RecordRenderTargetSwitch();
        }

        _currentTarget = target;
        var (w, h) = target is null ? (BackBufferWidth, BackBufferHeight) : (target.Width, target.Height);
        _viewport = new Rect(0, 0, w, h);
        _viewportDirty = true;
    }

    public void SetViewport(int x, int y, int width, int height)
    {
        ThrowIfDisposed();
        _viewport = new Rect(x, y, width, height);
        _viewportDirty = true;
    }

    public void Clear(Color color)
    {
        ThrowIfDisposed();
        BeginRecording();
        if (CurrentImage() is not { } image)
        {
            return; // minimized: nothing to draw into
        }

        var clear = new ClearValue(new ClearColorValue(color.R, color.G, color.B, color.A));
        if (_renderingImage == image)
        {
            // Mid-pass: clear the whole target regardless of the scissor (GL semantics of this API).
            var attachment = new ClearAttachment(ImageAspectFlags.ColorBit, 0, clear);
            var rect = new ClearRect(new Rect2D(default, new Extent2D((uint)image.Width, (uint)image.Height)), 0, 1);
            Context.Vk.CmdClearAttachments(_cmd, 1, &attachment, 1, &rect);
            return;
        }

        EndRendering();
        BeginRendering(image, CurrentDepth(image), clear, null);
    }

    public void ClearDepth(float depth = 1f)
    {
        ThrowIfDisposed();
        BeginRecording();
        if (CurrentImage() is not { } image)
        {
            return;
        }

        if (CurrentDepth(image) is not { } depthImage)
        {
            throw new InvalidOperationException($"The current target has no depth buffer; create render targets with depth: true.");
        }

        if (_renderingImage == image && _renderingDepth == depthImage)
        {
            var attachment = new ClearAttachment(ImageAspectFlags.DepthBit, 0, new ClearValue(depthStencil: new ClearDepthStencilValue(depth, 0)));
            var rect = new ClearRect(new Rect2D(default, new Extent2D((uint)image.Width, (uint)image.Height)), 0, 1);
            Context.Vk.CmdClearAttachments(_cmd, 1, &attachment, 1, &rect);
            return;
        }

        EndRendering();
        BeginRendering(image, depthImage, null, depth);
    }

    public void Draw(in DrawCommand command)
    {
        ThrowIfDisposed();
        var shader = command.Material.Shader as VulkanShader ?? throw new ArgumentException("Shader was not created by this device.", nameof(command));
        var texture = command.Texture as IVulkanTexture ?? throw new ArgumentException("Texture was not created by this device.", nameof(command));
        var vertices = command.Vertices as VulkanVertexBuffer ?? throw new ArgumentException("Vertex buffer was not created by this device.", nameof(command));
        var indices = command.Indices as VulkanIndexBuffer ?? throw new ArgumentException("Index buffer was not created by this device.", nameof(command));
        ObjectDisposedException.ThrowIf(command.Texture.IsDisposed, command.Texture);
        ObjectDisposedException.ThrowIf(shader.IsDisposed, shader);
        if (ReferenceEquals(command.Texture, _currentTarget))
        {
            throw new InvalidOperationException($"Cannot sample render target '{_currentTarget.DebugName}' while drawing into it.");
        }

        if (command.IndexOffset < 0 || command.IndexCount <= 0 || command.IndexOffset + command.IndexCount > indices.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(command), $"Index range {command.IndexOffset}+{command.IndexCount} exceeds buffer of {indices.Count}.");
        }

        BeginRecording();
        var staleVertices = !vertices.IsStatic && vertices.Frame != FrameNumber;
        if (staleVertices || (indices.IsDynamic && indices.Frame != FrameNumber))
        {
            throw new InvalidOperationException($"Dynamic buffer '{(staleVertices ? vertices.DebugName : indices.DebugName)}' must be filled with SetData in the frame it is drawn.");
        }

        if (CurrentImage() is not { } target)
        {
            return;
        }

        CollectTextures(shader, texture, command);
        foreach (var (_, sampled) in _drawTextures)
        {
            if (sampled.Image.Layout != ImageLayout.ShaderReadOnlyOptimal)
            {
                EndRendering(); // layout transitions are not allowed inside a rendering instance
                Barriers.Transition(Context.Vk, _cmd, sampled.Image, ImageLayout.ShaderReadOnlyOptimal, Statistics);
            }
        }

        if (_renderingImage != target)
        {
            EndRendering();
            BeginRendering(target, CurrentDepth(target), null, null);
        }

        if ((command.State.DepthTest || command.State.DepthWrite) && _renderingDepth is null)
        {
            throw new InvalidOperationException($"Draw with depth testing into '{(_currentTarget?.DebugName ?? "the back buffer")}', which has no depth buffer; create render targets with depth: true.");
        }

        var vk = Context.Vk;
        var key = new PipelineKey(command.Material.BlendMode, command.Topology, vertices.Layout, target.Format, _renderingDepth?.Format ?? Format.Undefined, command.State);
        if (!shader.Pipelines.TryGetValue(key, out var pipeline))
        {
            pipeline = _pipelines.Create(shader, key);
            shader.Pipelines.Add(key, pipeline);
        }

        if (pipeline.Handle != _boundPipeline.Handle)
        {
            vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, pipeline);
            _boundPipeline = pipeline;
            Statistics.RecordPipelineSwitch();
        }

        if (!ReferenceEquals(shader, _lastShader))
        {
            _lastShader = shader;
            Statistics.RecordShaderSwitch();
        }

        ApplyViewportAndScissor(target, command.Scissor);
        BindDescriptors(shader, texture, command);

        var vb = vertices.Current;
        if (vb.Buffer.Handle != _boundVertices.Buffer.Handle || vb.Offset != _boundVertices.Offset)
        {
            var buffer = vb.Buffer;
            var offset = vb.Offset;
            vk.CmdBindVertexBuffers(_cmd, 0, 1, &buffer, &offset);
            _boundVertices = (vb.Buffer, vb.Offset);
        }

        var ib = indices.Current;
        if (ib.Buffer.Handle != _boundIndices.Buffer.Handle || ib.Offset != _boundIndices.Offset)
        {
            vk.CmdBindIndexBuffer(_cmd, ib.Buffer, ib.Offset, indices.Format == IndexFormat.UInt32 ? IndexType.Uint32 : IndexType.Uint16);
            _boundIndices = (ib.Buffer, ib.Offset);
        }

        vk.CmdDrawIndexed(_cmd, (uint)command.IndexCount, (uint)Math.Max(1, command.Instances.Length), (uint)command.IndexOffset, 0, 0);
        if (texture.Image.View.Handle != _lastTextureView.Handle)
        {
            _lastTextureView = texture.Image.View;
            Statistics.RecordTextureSwitch();
        }

        Statistics.RecordDraw(command.Topology, command.IndexCount);
    }

    public void ReadPixels(Span<byte> destination)
    {
        ThrowIfDisposed();
        BeginRecording();
        var image = CurrentImage() ?? throw new InvalidOperationException("The back buffer is unavailable while the window is minimized.");
        if (image.IsSwapchainImage && !_swapchain!.SupportsReadback)
        {
            throw new NotSupportedException("This surface does not allow copying from the swapchain; render to a render target to read pixels.");
        }

        var rowBytes = image.Width * 4;
        var bytes = rowBytes * image.Height;
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, bytes, nameof(destination));

        EndRendering();
        var readback = _allocator.CreateBuffer((ulong)bytes, BufferUsageFlags.TransferDstBit, MemoryUsage.Readback);
        try
        {
            Barriers.Transition(Context.Vk, _cmd, image, ImageLayout.TransferSrcOptimal, Statistics);
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D((uint)image.Width, (uint)image.Height, 1),
            };
            Context.Vk.CmdCopyImageToBuffer(_cmd, image.Handle, ImageLayout.TransferSrcOptimal, readback.Handle, 1, &region);
            var barrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.AllTransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.HostBit,
                DstAccessMask = AccessFlags2.HostReadBit,
            };
            var dependency = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &barrier };
            Context.Vk.CmdPipelineBarrier2(_cmd, &dependency);

            // Readback is the one place that waits: only for the work recorded so far, then the frame continues.
            Submit(_immediateFence, present: false);
            var fence = _immediateFence;
            VulkanInstance.Check(Context.Vk.WaitForFences(Context.Device, 1, &fence, true, ulong.MaxValue), "vkWaitForFences");
            VulkanInstance.Check(Context.Vk.ResetFences(Context.Device, 1, &fence), "vkResetFences");
            _cmd = _slots[FrameSlot].Begin();
            ResetBindings();

            _allocator.Invalidate(readback, 0, (ulong)bytes);
            var source = new ReadOnlySpan<byte>(readback.Mapped, bytes);
            source.CopyTo(destination);
            if (image.Format is Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb)
            {
                for (var i = 0; i < bytes; i += 4)
                {
                    (destination[i], destination[i + 2]) = (destination[i + 2], destination[i]);
                }
            }
        }
        finally
        {
            DestroyLater(readback);
        }
    }

    public void Present()
    {
        ThrowIfDisposed();
        BeginRecording();
        EndRendering();
        var slot = _slots[FrameSlot];
        if (_surfaceSource is not null)
        {
            if (AcquireBackBuffer() is { } image)
            {
                if (image.Layout == ImageLayout.Undefined)
                {
                    BeginRendering(image, null, new ClearValue(new ClearColorValue(0f, 0f, 0f, 1f)), null); // nothing drawn: show black, not garbage
                    EndRendering();
                }

                Barriers.Transition(Context.Vk, _cmd, image, ImageLayout.PresentSrcKhr, Statistics);
            }

            var fence = slot.Fence;
            VulkanInstance.Check(Context.Vk.ResetFences(Context.Device, 1, &fence), "vkResetFences");
            var presenting = _imageIndex >= 0;
            Submit(slot.Fence, presenting);
            if (presenting)
            {
                QueuePresent();
            }
        }
        else
        {
            var fence = slot.Fence;
            VulkanInstance.Check(Context.Vk.ResetFences(Context.Device, 1, &fence), "vkResetFences");
            Submit(slot.Fence, present: false);
        }

        _recording = false;
        _imageIndex = -1;
        FrameNumber++;

        // Throttle here, like a GL buffer swap: wait until the next frame's slot is free (the frame FramesInFlight back).
        // Waiting at the start of the next frame instead would count VSync and GPU time as that frame's CPU time.
        var next = _slots[FrameSlot].Fence;
        VulkanInstance.Check(Context.Vk.WaitForFences(Context.Device, 1, &next, true, ulong.MaxValue), "vkWaitForFences");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (Context is not null)
        {
            Context.Vk.DeviceWaitIdle(Context.Device);
            foreach (var leaked in Resources.DisposeAll())
            {
                LogLeak(leaked.ToString());
            }

            _pipelines.Save();
            LogShaderStats(_compiler.Precompiled, _compiler.Compiled, _pipelines.Created);
        }

        _disposed = true;
        ReleaseNative();
    }

    /// <summary>Starts recording the frame if it has not started: waits for the slot's previous frame and recycles its resources.</summary>
    /// <returns>The frame number.</returns>
    internal long BeginRecording()
    {
        if (_recording)
        {
            return FrameNumber;
        }

        var slot = _slots[FrameSlot];
        var fence = slot.Fence;
        VulkanInstance.Check(Context.Vk.WaitForFences(Context.Device, 1, &fence, true, ulong.MaxValue), "vkWaitForFences");
        RunDestroyQueue(FrameNumber - FramesInFlight);
        slot.Reset();
        _cmd = slot.Begin();
        _uploadCmd = default;
        _recording = true;
        ResetBindings();
        return FrameNumber;
    }

    /// <summary>Copies dynamic data into this frame's streaming memory.</summary>
    internal StreamAllocation Stream(ReadOnlySpan<byte> data, ulong alignment)
    {
        BeginRecording();
        var allocation = _slots[FrameSlot].Stream.Allocate((ulong)Math.Max(data.Length, 4), Math.Max(alignment, 4));
        data.CopyTo(new Span<byte>(allocation.Pointer, data.Length));
        return allocation;
    }

    /// <summary>Device-local buffer filled through a staging copy recorded before this frame's draws.</summary>
    internal GpuBuffer CreateStaticBuffer(ReadOnlySpan<byte> data, BufferUsageFlags usage, string debugName)
    {
        var buffer = _allocator.CreateBuffer((ulong)data.Length, usage | BufferUsageFlags.TransferDstBit, MemoryUsage.DeviceLocal);
        Context.SetName(ObjectType.Buffer, buffer.Handle.Handle, debugName);
        var staging = _allocator.CreateBuffer((ulong)data.Length, BufferUsageFlags.TransferSrcBit, MemoryUsage.Upload);
        data.CopyTo(new Span<byte>(staging.Mapped, data.Length));
        _allocator.Flush(staging, 0, (ulong)data.Length);
        var copy = new BufferCopy(0, 0, (ulong)data.Length);
        Context.Vk.CmdCopyBuffer(UploadCommands(), staging.Handle, buffer.Handle, 1, &copy);
        DestroyLater(staging);
        return buffer;
    }

    internal void DestroyLater(VulkanImage image)
    {
        if (ReferenceEquals(_renderingImage, image))
        {
            EndRendering();
        }

        Defer(() =>
        {
            Context.Vk.DestroyImageView(Context.Device, image.View, null);
            _allocator.DestroyImage(image.Handle, image.Allocation, image.Kind);
        });
    }

    internal void DestroyLater(GpuBuffer buffer) => Defer(() => _allocator.DestroyBuffer(buffer));

    internal void DestroyLater(QueryPool pool) => Defer(() => Context.Vk.DestroyQueryPool(Context.Device, pool, null));

    internal void DestroyLater(VulkanShader shader)
    {
        var pipelines = shader.Pipelines.Values.ToArray();
        shader.Pipelines.Clear();
        if (ReferenceEquals(_lastShader, shader))
        {
            _lastShader = null;
        }

        Defer(() =>
        {
            foreach (var pipeline in pipelines)
            {
                Context.Vk.DestroyPipeline(Context.Device, pipeline, null);
            }

            Context.Vk.DestroyPipelineLayout(Context.Device, shader.Layout, null);
            Context.Vk.DestroyDescriptorSetLayout(Context.Device, shader.SetLayout, null);
            Context.Vk.DestroyShaderModule(Context.Device, shader.VertexModule, null);
            Context.Vk.DestroyShaderModule(Context.Device, shader.FragmentModule, null);
        });
    }

    private void Defer(Action destroy)
    {
        // Tagged with the frame being (or about to be) recorded: between frames an object may still be used by the next
        // one (a staging buffer queued before its copy is recorded), so the previous frame is not a safe tag.
        _destroyQueue.Enqueue((FrameNumber, destroy));
    }

    private void RunDestroyQueue(long completedFrame)
    {
        while (_destroyQueue.TryPeek(out var item) && item.Frame <= completedFrame)
        {
            _destroyQueue.Dequeue().Destroy();
        }
    }

    private VulkanImage? CurrentImage() => _currentTarget is IVulkanTexture target ? target.Image : AcquireBackBuffer();

    /// <summary>
    /// The depth buffer of the current target: a render target's own, or the back buffer's (created on first use and
    /// recreated when the back buffer size changes).
    /// </summary>
    private VulkanImage? CurrentDepth(VulkanImage color)
    {
        if (_currentTarget is VulkanRenderTarget target)
        {
            return target.Depth;
        }

        if (_backBufferDepth is { } existing && existing.Width == color.Width && existing.Height == color.Height)
        {
            return existing;
        }

        if (_backBufferDepth is not null)
        {
            DestroyLater(_backBufferDepth);
        }

        _backBufferDepth = CreateDepthImage(color.Width, color.Height, "BackBuffer depth");
        return _backBufferDepth;
    }

    private VulkanImage CreateDepthImage(int width, int height, string debugName)
    {
        if (_depthFormat == Format.Undefined)
        {
            _depthFormat = ChooseDepthFormat();
        }

        var info = new ImageCreateInfo { Format = _depthFormat, Extent = new Extent3D((uint)width, (uint)height, 1), MipLevels = 1, Usage = ImageUsageFlags.DepthStencilAttachmentBit };
        var (image, allocation) = _allocator.CreateImage(info, AllocationKind.RenderTarget);
        ImageView view;
        try
        {
            view = VulkanImage.CreateView(Context.Vk, Context.Device, image, _depthFormat, 1, ImageAspectFlags.DepthBit);
        }
        catch
        {
            _allocator.DestroyImage(image, allocation, AllocationKind.RenderTarget);
            throw;
        }

        Context.SetName(ObjectType.Image, image.Handle, debugName);
        return new VulkanImage { Handle = image, View = view, Format = _depthFormat, Width = width, Height = height, Allocation = allocation, Kind = AllocationKind.RenderTarget, Aspect = ImageAspectFlags.DepthBit };
    }

    /// <summary>32-bit float depth when the device can render to it (nearly all desktop GPUs), else a packed format.</summary>
    private Format ChooseDepthFormat()
    {
        foreach (var format in (ReadOnlySpan<Format>)[Format.D32Sfloat, Format.D32SfloatS8Uint, Format.D24UnormS8Uint])
        {
            FormatProperties properties;
            Context.Vk.GetPhysicalDeviceFormatProperties(Context.PhysicalDevice, format, &properties);
            if ((properties.OptimalTilingFeatures & FormatFeatureFlags.DepthStencilAttachmentBit) != 0)
            {
                return format;
            }
        }

        throw new GraphicsInitializationException("The GPU supports none of the depth formats D32_SFLOAT, D32_SFLOAT_S8_UINT, D24_UNORM_S8_UINT.");
    }

    private VulkanImage? AcquireBackBuffer()
    {
        if (_headlessBackBuffer is { } headless)
        {
            return headless;
        }

        if (_imageIndex >= 0)
        {
            return _swapchain!.Images[_imageIndex];
        }

        if (_surfaceLost)
        {
            RecreateSurface();
        }

        if (_swapchainDirty || _swapchain is null)
        {
            RecreateSwapchain();
        }

        for (var attempt = 0; attempt < 2 && _swapchain is not null; attempt++)
        {
            uint index;
            var semaphore = _slots[FrameSlot].ImageAvailable;
            var result = Context.Swapchain!.AcquireNextImage(Context.Device, _swapchain.Handle, ulong.MaxValue, semaphore, default, &index);
            switch (result)
            {
                case Result.Success or Result.SuboptimalKhr:
                    _swapchainDirty |= result == Result.SuboptimalKhr;
                    _imageIndex = (int)index;
                    _acquireWaitPending = true;
                    var image = _swapchain.Images[index];
                    image.Layout = ImageLayout.Undefined; // previous contents are not needed
                    image.LastStage = PipelineStageFlags2.None;
                    return image;
                case Result.ErrorOutOfDateKhr:
                    RecreateSwapchain();
                    break;
                case Result.ErrorSurfaceLostKhr:
                    RecreateSurface();
                    break;
                default:
                    VulkanInstance.Check(result, "vkAcquireNextImageKHR");
                    break;
            }
        }

        return null; // minimized (zero-sized surface)
    }

    private void QueuePresent()
    {
        var swapchain = _swapchain!.Handle;
        var index = (uint)_imageIndex;
        var wait = _swapchain.RenderFinished[_imageIndex];
        var info = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &wait,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &index,
        };
        var result = Context.Swapchain!.QueuePresent(Context.PresentQueue, &info);
        switch (result)
        {
            case Result.Success:
                break;
            case Result.SuboptimalKhr or Result.ErrorOutOfDateKhr:
                _swapchainDirty = true;
                break;
            case Result.ErrorSurfaceLostKhr:
                _surfaceLost = true;
                break;
            default:
                VulkanInstance.Check(result, "vkQueuePresentKHR");
                break;
        }
    }

    private void RecreateSwapchain()
    {
        // Rare (resize, display change): wait for the GPU so the old images and semaphores are idle.
        Context.Vk.DeviceWaitIdle(Context.Device);
        var old = _swapchain;
        _swapchain = VulkanSwapchain.Create(Context, _surface, BackBufferWidth, BackBufferHeight, _vsync, old, _logger);
        old?.Dispose();
        _swapchainDirty = false;
        if (_swapchain is not null)
        {
            (BackBufferWidth, BackBufferHeight) = ((int)_swapchain.Extent.Width, (int)_swapchain.Extent.Height);
            if (_currentTarget is null)
            {
                _viewport = new Rect(0, 0, BackBufferWidth, BackBufferHeight);
                _viewportDirty = true;
            }
        }
    }

    private void RecreateSurface()
    {
        LogSurfaceLost();
        Context.Vk.DeviceWaitIdle(Context.Device);
        _swapchain?.Dispose();
        _swapchain = null;
        _khrSurface!.DestroySurface(_instance.Handle, _surface, null);
        _surface = CreateSurface();
        _surfaceLost = false;
        _swapchainDirty = true;
    }

    private SurfaceKHR CreateSurface()
    {
        var handle = _surfaceSource!.Create<AllocationCallbacks>(_instance.Handle.ToHandle(), null);
        if (handle.Handle == 0)
        {
            throw new GraphicsInitializationException("The window could not create a Vulkan surface.");
        }

        return handle.ToSurface();
    }

    /// <summary>Ends the frame's command buffers and submits them in order: uploads first, then draws.</summary>
    private void Submit(Fence fence, bool present)
    {
        var vk = Context.Vk;
        var buffers = stackalloc CommandBufferSubmitInfo[2];
        var count = 0u;
        if (_uploadCmd.Handle != 0)
        {
            Barriers.UploadsToReads(vk, _uploadCmd);
            VulkanInstance.Check(vk.EndCommandBuffer(_uploadCmd), "vkEndCommandBuffer");
            buffers[count++] = new CommandBufferSubmitInfo { SType = StructureType.CommandBufferSubmitInfo, CommandBuffer = _uploadCmd };
            _uploadCmd = default;
        }

        VulkanInstance.Check(vk.EndCommandBuffer(_cmd), "vkEndCommandBuffer");
        buffers[count++] = new CommandBufferSubmitInfo { SType = StructureType.CommandBufferSubmitInfo, CommandBuffer = _cmd };
        _slots[FrameSlot].Stream.Flush();

        var wait = new SemaphoreSubmitInfo { SType = StructureType.SemaphoreSubmitInfo, Semaphore = _slots[FrameSlot].ImageAvailable, StageMask = PipelineStageFlags2.ColorAttachmentOutputBit };
        var signal = new SemaphoreSubmitInfo { SType = StructureType.SemaphoreSubmitInfo, StageMask = PipelineStageFlags2.AllCommandsBit };
        if (present)
        {
            signal.Semaphore = _swapchain!.RenderFinished[_imageIndex];
        }

        var info = new SubmitInfo2
        {
            SType = StructureType.SubmitInfo2,
            WaitSemaphoreInfoCount = _acquireWaitPending ? 1u : 0u,
            PWaitSemaphoreInfos = &wait,
            CommandBufferInfoCount = count,
            PCommandBufferInfos = buffers,
            SignalSemaphoreInfoCount = present ? 1u : 0u,
            PSignalSemaphoreInfos = &signal,
        };
        VulkanInstance.Check(vk.QueueSubmit2(Context.GraphicsQueue, 1, &info, fence), "vkQueueSubmit2");
        _acquireWaitPending = false;
        Statistics.RecordSubmit();
    }

    private CommandBuffer UploadCommands()
    {
        BeginRecording();
        if (_uploadCmd.Handle == 0)
        {
            _uploadCmd = _slots[FrameSlot].Begin();
        }

        return _uploadCmd;
    }

    /// <param name="depth">Depth attachment of the target, if it has one (pipelines then declare its format).</param>
    /// <param name="clearDepth">Clears the depth attachment to this value; otherwise its contents are loaded.</param>
    private void BeginRendering(VulkanImage image, VulkanImage? depth, ClearValue? clear, float? clearDepth)
    {
        Barriers.Transition(Context.Vk, _cmd, image, ImageLayout.ColorAttachmentOptimal, Statistics);
        if (depth is not null)
        {
            // Always a barrier, also from the same layout: it orders this pass after earlier frames' depth writes.
            Barriers.Transition(Context.Vk, _cmd, depth, ImageLayout.DepthAttachmentOptimal, Statistics);
        }

        var attachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = image.View,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = clear is null ? AttachmentLoadOp.Load : AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = clear ?? default,
        };
        var depthAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = depth?.View ?? default,
            ImageLayout = ImageLayout.DepthAttachmentOptimal,
            LoadOp = clearDepth is null ? AttachmentLoadOp.Load : AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(clearDepth ?? 1f, 0)),
        };
        var info = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(default, new Extent2D((uint)image.Width, (uint)image.Height)),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &attachment,
            PDepthAttachment = depth is null ? null : &depthAttachment,
        };
        Context.Vk.CmdBeginRendering(_cmd, &info);
        _renderingImage = image;
        _renderingDepth = depth;
        _viewportDirty = true;
        _scissor = default;
    }

    private void EndRendering()
    {
        if (_renderingImage is not null)
        {
            Context.Vk.CmdEndRendering(_cmd);
            _renderingImage = null;
            _renderingDepth = null;
        }
    }

    private void ResetBindings()
    {
        _renderingImage = null;
        _boundPipeline = default;
        _pushedLayout = default;
        _boundVertices = default;
        _boundIndices = default;
        _vertexUniforms = default;
        _fragmentUniforms = default;
        Array.Clear(_pushedTextures);
        _viewportDirty = true;
        _scissor = default;
    }

    private void ApplyViewportAndScissor(VulkanImage target, Rect? scissor)
    {
        var vk = Context.Vk;
        if (_viewportDirty)
        {
            // Negative height keeps clip space y-up like GL; the public viewport origin is bottom-left.
            var viewport = new Viewport(_viewport.X, target.Height - _viewport.Y, _viewport.Width, -_viewport.Height, 0f, 1f);
            vk.CmdSetViewport(_cmd, 0, 1, &viewport);
            _viewportDirty = false;
        }

        Rect2D rect;
        if (scissor is { } r)
        {
            var x0 = Math.Clamp((int)MathF.Floor(r.X), 0, target.Width);
            var x1 = Math.Clamp((int)MathF.Floor(r.X) + (int)Math.Max(0f, MathF.Ceiling(r.Width)), 0, target.Width);
            var bottom = Math.Clamp((int)MathF.Floor(r.Y), 0, target.Height);
            var top = Math.Clamp((int)MathF.Floor(r.Y) + (int)Math.Max(0f, MathF.Ceiling(r.Height)), 0, target.Height);
            rect = new Rect2D(new Offset2D(x0, target.Height - top), new Extent2D((uint)(x1 - x0), (uint)(top - bottom)));
        }
        else
        {
            rect = new Rect2D(default, new Extent2D((uint)target.Width, (uint)target.Height));
        }

        if (rect.Offset.X != _scissor.Offset.X || rect.Offset.Y != _scissor.Offset.Y || rect.Extent.Width != _scissor.Extent.Width || rect.Extent.Height != _scissor.Extent.Height)
        {
            vk.CmdSetScissor(_cmd, 0, 1, &rect);
            _scissor = rect;
        }
    }

    /// <summary>The texture of every sampler the shader declares: <c>uTexture</c> from the draw, the rest from the material.</summary>
    /// <exception cref="InvalidOperationException">The material has no texture for a sampler, or one cannot be sampled here.</exception>
    private void CollectTextures(VulkanShader shader, IVulkanTexture drawTexture, in DrawCommand command)
    {
        _drawTextures.Clear();
        foreach (var sampler in shader.Samplers)
        {
            IVulkanTexture sampled;
            if (sampler.Name == "uTexture")
            {
                sampled = drawTexture;
            }
            else
            {
                var source = command.Material.GetTexture(sampler.Name)
                    ?? throw new InvalidOperationException($"Material '{command.Material.Name}' has no texture for sampler '{sampler.Name}' of shader '{shader.DebugName}'.");
                ObjectDisposedException.ThrowIf(source.IsDisposed, source);
                if (ReferenceEquals(source, _currentTarget))
                {
                    throw new InvalidOperationException($"Cannot sample render target '{source.DebugName}' while drawing into it.");
                }

                sampled = source as IVulkanTexture ?? throw new ArgumentException($"Texture '{source.DebugName}' was not created by this device.", nameof(command));
            }

            _drawTextures.Add((sampler.Binding, sampled));
        }
    }

    private void BindDescriptors(VulkanShader shader, IVulkanTexture texture, in DrawCommand command)
    {
        var layoutChanged = shader.Layout.Handle != _pushedLayout.Handle;
        var writes = stackalloc WriteDescriptorSet[_drawTextures.Count + 2];
        var imageInfos = stackalloc DescriptorImageInfo[Math.Max(1, _drawTextures.Count)];
        DescriptorBufferInfo vertexInfo = default, fragmentInfo = default;
        var count = 0u;
        if (layoutChanged)
        {
            Array.Clear(_pushedTextures);
        }

        for (var i = 0; i < _drawTextures.Count; i++)
        {
            var (binding, sampled) = _drawTextures[i];
            ref var pushed = ref _pushedTextures[binding];
            if (pushed.View.Handle != sampled.Image.View.Handle || pushed.Sampler.Handle != sampled.VkSampler.Handle)
            {
                imageInfos[i] = new DescriptorImageInfo(sampled.VkSampler, sampled.Image.View, ImageLayout.ShaderReadOnlyOptimal);
                writes[count++] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = binding, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &imageInfos[i] };
                pushed = (sampled.Image.View, sampled.VkSampler);
            }
        }

        if (shader.VertexBlock is { } vertexBlock && WriteBlock(shader, ShaderStage.Vertex, vertexBlock, command, _vertexBlock, _lastVertexBlock, ref _vertexUniforms) | layoutChanged)
        {
            vertexInfo = new DescriptorBufferInfo(_vertexUniforms.Buffer, _vertexUniforms.Offset, (ulong)vertexBlock.Size);
            writes[count++] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = ShaderCompiler.VertexUniformBinding, DescriptorCount = 1, DescriptorType = DescriptorType.UniformBuffer, PBufferInfo = &vertexInfo };
        }

        if (shader.FragmentBlock is { } fragmentBlock && WriteBlock(shader, ShaderStage.Fragment, fragmentBlock, command, _fragmentBlock, _lastFragmentBlock, ref _fragmentUniforms) | layoutChanged)
        {
            fragmentInfo = new DescriptorBufferInfo(_fragmentUniforms.Buffer, _fragmentUniforms.Offset, (ulong)fragmentBlock.Size);
            writes[count++] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = ShaderCompiler.FragmentUniformBinding, DescriptorCount = 1, DescriptorType = DescriptorType.UniformBuffer, PBufferInfo = &fragmentInfo };
        }

        if (count > 0)
        {
            Context.Vk.CmdPushDescriptorSet(_cmd, PipelineBindPoint.Graphics, shader.Layout, 0, count, writes);
            _pushedLayout = shader.Layout;
            Statistics.RecordDescriptorWrites((int)count);
        }
    }

    /// <summary>Writes per-draw matrices (joints, instances) into a <c>mat4</c> array uniform of the stage, up to its length.</summary>
    private static void WriteMatrices(VulkanShader shader, ShaderStage stage, string name, ReadOnlySpan<Matrix4x4> matrices, Span<byte> data)
    {
        if (matrices.IsEmpty)
        {
            return;
        }

        foreach (var slot in shader.Slots(name))
        {
            if (slot.Stage == stage && slot.Member.ElementKind == UniformKind.Matrix4x4)
            {
                for (var i = 0; i < Math.Min(matrices.Length, slot.Member.ArrayLength); i++)
                {
                    MemoryMarshal.Write(data[(slot.Member.Offset + (i * slot.Member.ArrayStride))..], in matrices[i]);
                }
            }
        }
    }

    /// <summary>Fills a stage's uniform block; streams it only when it differs from the last one. Returns true when it changed.</summary>
    private bool WriteBlock(VulkanShader shader, ShaderStage stage, SpirvUniformBlock block, in DrawCommand command, byte[] scratch, byte[] last, ref (VkBuffer Buffer, ulong Offset, int Size) current)
    {
        if (block.Size > scratch.Length)
        {
            throw new NotSupportedException($"Shader '{shader.DebugName}' has {block.Size} bytes of {stage} uniforms; at most {scratch.Length} are supported.");
        }

        var data = scratch.AsSpan(0, block.Size);
        data.Clear();
        foreach (var slot in shader.Slots("uViewProjection"))
        {
            if (slot.Stage == stage && slot.Member.Kind == UniformKind.Matrix4x4)
            {
                var viewProjection = command.ViewProjection;
                MemoryMarshal.Write(data[slot.Member.Offset..], in viewProjection);
            }
        }

        if (shader.Slots("uModel").Count > 0 || shader.Slots("uNormalMatrix").Count > 0)
        {
            var world = command.World ?? Matrix4x4.Identity;
            foreach (var slot in shader.Slots("uModel"))
            {
                if (slot.Stage == stage && slot.Member.Kind == UniformKind.Matrix4x4)
                {
                    MemoryMarshal.Write(data[slot.Member.Offset..], in world);
                }
            }

            // Normals transform by the inverse transpose, so non-uniform scale keeps them perpendicular to surfaces.
            var normalMatrix = Matrix4x4.TryInvert(world, out var inverse) ? inverse.Transposed : Matrix4x4.Identity;
            foreach (var slot in shader.Slots("uNormalMatrix"))
            {
                if (slot.Stage == stage && slot.Member.Kind == UniformKind.Matrix4x4)
                {
                    MemoryMarshal.Write(data[slot.Member.Offset..], in normalMatrix);
                }
            }
        }

        WriteMatrices(shader, stage, "uJoints", command.Joints.Span, data);
        WriteMatrices(shader, stage, "uInstances", command.Instances.Span, data);

        foreach (var (name, value) in command.Material.Uniforms)
        {
            foreach (var slot in shader.Slots(name))
            {
                if (slot.Stage == stage && slot.Member.Kind == value.Kind)
                {
                    WriteValue(data[slot.Member.Offset..], value);
                }
            }
        }

        if (current.Buffer.Handle != 0 && current.Size == block.Size && data.SequenceEqual(last.AsSpan(0, block.Size)))
        {
            return false;
        }

        var allocation = Stream(data, Context.Limits.MinUniformBufferOffsetAlignment);
        data.CopyTo(last);
        current = (allocation.Buffer, allocation.Offset, block.Size);
        return true;
    }

    private static void WriteValue(Span<byte> destination, in UniformValue value)
    {
        var v = value.Vector;
        switch (value.Kind)
        {
            case UniformKind.Float:
                MemoryMarshal.Write(destination, in v.X);
                break;
            case UniformKind.Int:
                var integer = value.Integer;
                MemoryMarshal.Write(destination, in integer);
                break;
            case UniformKind.Vector2:
                MemoryMarshal.Write(destination, in v.X);
                MemoryMarshal.Write(destination[4..], in v.Y);
                break;
            case UniformKind.Vector4:
                MemoryMarshal.Write(destination, in v);
                break;
            case UniformKind.Matrix4x4:
                var matrix = value.Matrix; // column-major, as std140 mat4
                MemoryMarshal.Write(destination, in matrix);
                break;
        }
    }

    private VulkanImage CreateImage(int width, int height, uint mipLevels, ImageUsageFlags usage, AllocationKind kind, string debugName)
    {
        var info = new ImageCreateInfo { Format = TargetFormat, Extent = new Extent3D((uint)width, (uint)height, 1), MipLevels = mipLevels, Usage = usage };
        var (image, allocation) = _allocator.CreateImage(info, kind);
        ImageView view;
        try
        {
            view = VulkanImage.CreateView(Context.Vk, Context.Device, image, TargetFormat, mipLevels);
        }
        catch
        {
            _allocator.DestroyImage(image, allocation, kind);
            throw;
        }

        Context.SetName(ObjectType.Image, image.Handle, debugName);
        return new VulkanImage { Handle = image, View = view, Format = TargetFormat, Width = width, Height = height, MipLevels = mipLevels, Allocation = allocation, Kind = kind };
    }

    /// <summary>Blits each level from the previous one. Expects every level in TRANSFER_DST; leaves all in SHADER_READ_ONLY.</summary>
    private void GenerateMipmaps(CommandBuffer cmd, VulkanImage image)
    {
        var vk = Context.Vk;
        int w = image.Width, h = image.Height;
        for (var level = 1u; level < image.MipLevels; level++)
        {
            LevelBarrier(cmd, image, level - 1, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal, PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit, PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferReadBit);
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, level - 1, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, level, 0, 1),
            };
            blit.SrcOffsets[1] = new Offset3D(w, h, 1);
            blit.DstOffsets[1] = new Offset3D(nw, nh, 1);
            vk.CmdBlitImage(cmd, image.Handle, ImageLayout.TransferSrcOptimal, image.Handle, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Linear);
            LevelBarrier(cmd, image, level - 1, ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal, PipelineStageFlags2.AllTransferBit, AccessFlags2.None, PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit);
            (w, h) = (nw, nh);
        }

        LevelBarrier(cmd, image, image.MipLevels - 1, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit, PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit);
        image.Layout = ImageLayout.ShaderReadOnlyOptimal;
        image.LastStage = PipelineStageFlags2.FragmentShaderBit;
    }

    private void LevelBarrier(CommandBuffer cmd, VulkanImage image, uint level, ImageLayout from, ImageLayout to, PipelineStageFlags2 srcStage, AccessFlags2 srcAccess, PipelineStageFlags2 dstStage, AccessFlags2 dstAccess)
    {
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = srcStage,
            SrcAccessMask = srcAccess,
            DstStageMask = dstStage,
            DstAccessMask = dstAccess,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image.Handle,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, level, 1, 0, 1),
        };
        var dependency = new DependencyInfo { SType = StructureType.DependencyInfo, ImageMemoryBarrierCount = 1, PImageMemoryBarriers = &barrier };
        Context.Vk.CmdPipelineBarrier2(cmd, &dependency);
    }

    private Sampler Sampler(SamplerState state)
    {
        if (_samplers.TryGetValue(state, out var sampler))
        {
            return sampler;
        }

        var filter = state.Filter == TextureFilter.Point ? Filter.Nearest : Filter.Linear;
        var address = state.Wrap switch
        {
            TextureWrap.Clamp => SamplerAddressMode.ClampToEdge,
            TextureWrap.Repeat => SamplerAddressMode.Repeat,
            TextureWrap.MirroredRepeat => SamplerAddressMode.MirroredRepeat,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state.Wrap, "Unknown wrap mode."),
        };
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = filter,
            MinFilter = filter,
            MipmapMode = state.GenerateMipmaps && state.Filter == TextureFilter.Linear ? SamplerMipmapMode.Linear : SamplerMipmapMode.Nearest,
            AddressModeU = address,
            AddressModeV = address,
            AddressModeW = address,
            MaxLod = state.GenerateMipmaps ? Vk.LodClampNone : 0f,
        };
        VulkanInstance.Check(Context.Vk.CreateSampler(Context.Device, &info, null, &sampler), "vkCreateSampler");
        _samplers.Add(state, sampler);
        return sampler;
    }

    private void ReleaseNative()
    {
        if (Context is not null)
        {
            Context.Vk.DeviceWaitIdle(Context.Device);
            RunDestroyQueue(long.MaxValue);
            if (_headlessBackBuffer is { } backBuffer)
            {
                Context.Vk.DestroyImageView(Context.Device, backBuffer.View, null);
                _allocator.DestroyImage(backBuffer.Handle, backBuffer.Allocation, backBuffer.Kind);
            }

            if (_backBufferDepth is { } depth)
            {
                Context.Vk.DestroyImageView(Context.Device, depth.View, null);
                _allocator.DestroyImage(depth.Handle, depth.Allocation, depth.Kind);
            }

            _swapchain?.Dispose();
            foreach (var sampler in _samplers.Values)
            {
                Context.Vk.DestroySampler(Context.Device, sampler, null);
            }

            _timer?.Dispose();
            if (_immediateFence.Handle != 0)
            {
                Context.Vk.DestroyFence(Context.Device, _immediateFence, null);
            }

            foreach (var slot in _slots ?? [])
            {
                slot?.Dispose();
            }

            _pipelines?.Dispose();
            _compiler?.Dispose();
            _allocator?.Dispose();
            Context.Dispose();
        }

        if (_surface.Handle != 0)
        {
            _khrSurface!.DestroySurface(_instance.Handle, _surface, null);
        }

        _khrSurface?.Dispose();
        _instance.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    [LoggerMessage(EventId = 1106, Level = LogLevel.Information, Message = "Graphics device created: {Description} (validation: {Validation}, headless: {Headless})")]
    private partial void LogCreated(string description, bool validation, bool headless);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "GPU resource leaked and released at device shutdown: {Resource}")]
    private partial void LogLeak(string resource);

    [LoggerMessage(EventId = 1109, Level = LogLevel.Information, Message = "Shader stages: {Precompiled} precompiled, {Compiled} compiled at run time; {Pipelines} pipelines created")]
    private partial void LogShaderStats(int precompiled, int compiled, int pipelines);

    [LoggerMessage(EventId = 1107, Level = LogLevel.Warning, Message = "Vulkan surface lost; recreating it and the swapchain")]
    private partial void LogSurfaceLost();
}
