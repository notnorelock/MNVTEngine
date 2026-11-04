namespace MNVTEngine.Graphics;

/// <summary>Rendering work of one frame.</summary>
public readonly record struct GraphicsFrameStatistics(
    int DrawCalls,
    long Indices,
    long Triangles,
    long Lines,
    long VerticesUploaded,
    int TextureSwitches,
    int ShaderSwitches,
    int RenderTargetSwitches,
    int PipelineSwitches = 0,
    int DescriptorWrites = 0,
    int Barriers = 0,
    int Submits = 0,
    long TextureBytesUploaded = 0);

/// <summary>Live GPU objects by kind; texture bytes are estimated as width × height × 4 (RGBA8, no mipmaps).</summary>
public readonly record struct GraphicsResourceCounts(int Textures, int RenderTargets, int Shaders, int Buffers, long TextureBytes);

/// <summary>
/// Counters of the current frame, filled by the device and by buffer uploads; <see cref="EndFrame"/> (called once per
/// frame by the host) publishes them as <see cref="LastFrame"/> and starts over.
/// </summary>
public sealed class GraphicsStatistics
{
    private int _drawCalls;
    private long _indices;
    private long _triangles;
    private long _lines;
    private long _vertices;
    private int _textureSwitches;
    private int _shaderSwitches;
    private int _targetSwitches;
    private int _pipelineSwitches;
    private int _descriptorWrites;
    private int _barriers;
    private int _submits;
    private long _textureBytes;

    public GraphicsFrameStatistics LastFrame { get; private set; }

    /// <summary>Counts so far in the frame being rendered.</summary>
    public GraphicsFrameStatistics Current => new(_drawCalls, _indices, _triangles, _lines, _vertices, _textureSwitches, _shaderSwitches, _targetSwitches, _pipelineSwitches, _descriptorWrites, _barriers, _submits, _textureBytes);

    public void RecordDraw(PrimitiveTopology topology, int indexCount)
    {
        _drawCalls++;
        _indices += indexCount;
        if (topology == PrimitiveTopology.Lines)
        {
            _lines += indexCount / 2;
        }
        else
        {
            _triangles += indexCount / 3;
        }
    }

    public void RecordVertexUpload(int vertices) => _vertices += vertices;

    public void RecordTextureSwitch() => _textureSwitches++;

    public void RecordShaderSwitch() => _shaderSwitches++;

    public void RecordRenderTargetSwitch() => _targetSwitches++;

    /// <summary>A different pipeline (shader and fixed-function state) was bound.</summary>
    public void RecordPipelineSwitch() => _pipelineSwitches++;

    /// <summary>Descriptors written for draws (push descriptor writes).</summary>
    public void RecordDescriptorWrites(int count) => _descriptorWrites += count;

    /// <summary>A pipeline barrier (image layout transition) was recorded.</summary>
    public void RecordBarrier() => _barriers++;

    /// <summary>A batch of command buffers was submitted to the GPU queue.</summary>
    public void RecordSubmit() => _submits++;

    /// <summary>Texture data copied to the GPU (texture creation and region updates).</summary>
    public void RecordTextureUpload(long bytes) => _textureBytes += bytes;

    public void EndFrame()
    {
        LastFrame = Current;
        (_drawCalls, _indices, _triangles, _lines, _vertices, _textureSwitches, _shaderSwitches, _targetSwitches) = (0, 0, 0, 0, 0, 0, 0, 0);
        (_pipelineSwitches, _descriptorWrites, _barriers, _submits, _textureBytes) = (0, 0, 0, 0, 0);
    }
}

/// <summary>Elapsed GPU time of one named scope (from timestamp queries).</summary>
public readonly record struct GpuTiming(string Name, double Milliseconds);

/// <summary>
/// GPU timing with asynchronous queries: results become available a few frames later and are never waited for. A frame
/// whose queries are not ready when its slot is reused is dropped (counted in <see cref="DroppedFrames"/>).
/// </summary>
public interface IGpuTimer
{
    /// <summary>Frames between issuing and reading results.</summary>
    int Latency { get; }

    /// <summary>Total GPU time of the newest completed frame; NaN until one completed.</summary>
    double LastFrameMilliseconds { get; }

    /// <summary>Scopes of that frame, in issue order (the list is reused).</summary>
    IReadOnlyList<GpuTiming> LastScopes { get; }

    /// <summary>Index of the frame <see cref="LastFrameMilliseconds"/> belongs to; -1 before the first result.</summary>
    long LastFrameIndex { get; }

    long DroppedFrames { get; }

    /// <summary>Starts a frame and collects any results that are ready.</summary>
    void BeginFrame();

    void BeginScope(string name);

    void EndScope();

    void EndFrame();
}

public readonly struct GpuScope : IDisposable
{
    private readonly IGpuTimer? _timer;

    public GpuScope(IGpuTimer? timer, string name)
    {
        _timer = timer;
        timer?.BeginScope(name);
    }

    public void Dispose() => _timer?.EndScope();
}

/// <summary>
/// GPU memory the engine allocated through the backend's allocator (exact), plus the driver's budget for this process.
/// </summary>
/// <param name="BlockBytes">Device memory reserved from the driver (allocator blocks).</param>
/// <param name="AllocatedBytes">Bytes of live allocations placed in those blocks.</param>
/// <param name="DeviceLocalBudgetBytes">Device-local memory this process may use; null when unknown.</param>
/// <param name="DeviceLocalUsageBytes">Device-local memory this process uses (all its allocations); null when unknown.</param>
/// <param name="BudgetMeasured">True when the budget comes from the driver; otherwise it is the allocator's estimate.</param>
public readonly record struct GpuAllocationStatistics(
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

/// <summary>Video memory reported by a vendor extension; values the driver does not expose are null.</summary>
/// <param name="Source">Where the numbers come from, e.g. "GL_NVX_gpu_memory_info".</param>
public readonly record struct GpuMemoryInfo(bool Supported, long? TotalBytes, long? AvailableBytes, string Source)
{
    public static GpuMemoryInfo Unsupported => new(false, null, null, "none");

    /// <summary>In use on the adapter by all processes (total − available), when both are known.</summary>
    public long? UsedBytes => TotalBytes is { } total && AvailableBytes is { } available ? total - available : null;
}
