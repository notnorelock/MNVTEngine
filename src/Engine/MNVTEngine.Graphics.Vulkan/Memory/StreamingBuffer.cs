using Silk.NET.Vulkan;

namespace MNVTEngine.Graphics.Vulkan.Memory;

/// <summary>One allocation inside a <see cref="StreamingBuffer"/>: where the GPU reads it and where the CPU writes it.</summary>
internal readonly unsafe struct StreamAllocation(Silk.NET.Vulkan.Buffer buffer, ulong offset, byte* pointer)
{
    public Silk.NET.Vulkan.Buffer Buffer { get; } = buffer;

    public ulong Offset { get; } = offset;

    public byte* Pointer { get; } = pointer;
}

/// <summary>
/// Per-frame linear allocator for data the CPU rewrites every frame: dynamic vertices and indices, uniform blocks. One per
/// frame slot, reset when the slot's fence has signalled, so the GPU never reads memory being rewritten. Grows by adding
/// chunks; chunks are kept for later frames, so steady state allocates nothing.
/// </summary>
internal sealed unsafe class StreamingBuffer(GpuAllocator allocator, ulong chunkSize) : IDisposable
{
    private const BufferUsageFlags Usage = BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit | BufferUsageFlags.UniformBufferBit;
    private readonly List<(GpuBuffer Buffer, ulong Used)> _chunks = [];
    private int _current;

    /// <summary>Bytes handed out since the last reset.</summary>
    public ulong BytesUsed { get; private set; }

    public ulong Capacity => _chunks.Aggregate(0ul, (sum, c) => sum + c.Buffer.Size);

    public StreamAllocation Allocate(ulong size, ulong alignment)
    {
        while (true)
        {
            if (_current < _chunks.Count)
            {
                var (buffer, used) = _chunks[_current];
                var offset = (used + alignment - 1) / alignment * alignment;
                if (offset + size <= buffer.Size)
                {
                    _chunks[_current] = (buffer, offset + size);
                    BytesUsed += size;
                    return new StreamAllocation(buffer.Handle, offset, buffer.Mapped + offset);
                }

                _current++;
                continue;
            }

            var bytes = Math.Max(chunkSize, (size + 0xFFFF) & ~0xFFFFul);
            _chunks.Add((allocator.CreateBuffer(bytes, Usage, MemoryUsage.Upload), 0));
        }
    }

    /// <summary>Makes this frame's writes visible to the GPU; call before submitting.</summary>
    public void Flush()
    {
        foreach (var (buffer, used) in _chunks)
        {
            if (used > 0)
            {
                allocator.Flush(buffer, 0, used);
            }
        }
    }

    /// <summary>Starts over; the GPU must be done with the previous contents.</summary>
    public void Reset()
    {
        for (var i = 0; i < _chunks.Count; i++)
        {
            _chunks[i] = (_chunks[i].Buffer, 0);
        }

        _current = 0;
        BytesUsed = 0;
    }

    public void Dispose()
    {
        foreach (var (buffer, _) in _chunks)
        {
            allocator.DestroyBuffer(buffer);
        }

        _chunks.Clear();
    }
}
