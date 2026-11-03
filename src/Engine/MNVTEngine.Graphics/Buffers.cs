using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MNVTEngine.Graphics;

public enum VertexElementType
{
    Float32,

    /// <summary>Unsigned byte normalized to [0, 1] in the shader (packed colors).</summary>
    UNormByte,
}

/// <param name="Location">Shader attribute location.</param>
/// <param name="Components">1 to 4.</param>
/// <param name="Offset">Byte offset inside the vertex.</param>
public readonly record struct VertexElement(int Location, int Components, VertexElementType Type, int Offset);

public sealed record VertexLayout(int Stride, IReadOnlyList<VertexElement> Elements);

/// <summary>Dynamic vertex buffer with a fixed capacity, re-uploaded by the CPU each use.</summary>
public abstract class VertexBuffer : GraphicsResource
{
    protected VertexBuffer(GraphicsResourceTracker? tracker, string debugName, VertexLayout layout, int capacity)
        : base(tracker, debugName)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Layout = layout;
        Capacity = capacity;
    }

    public VertexLayout Layout { get; }

    /// <summary>Maximum number of vertices.</summary>
    public int Capacity { get; }

    /// <summary>Replaces the buffer contents from offset 0.</summary>
    public void SetData<T>(ReadOnlySpan<T> vertices)
        where T : unmanaged
    {
        ThrowIfDisposed();
        if (Unsafe.SizeOf<T>() != Layout.Stride)
        {
            throw new ArgumentException($"Vertex type {typeof(T).Name} is {Unsafe.SizeOf<T>()} bytes but the layout stride is {Layout.Stride}.", nameof(vertices));
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(vertices.Length, Capacity, nameof(vertices));
        SetDataCore(MemoryMarshal.AsBytes(vertices));
    }

    protected abstract void SetDataCore(ReadOnlySpan<byte> data);
}

/// <summary>16-bit index buffer: immutable, or dynamic (re-uploaded by the CPU, e.g. for UI geometry).</summary>
public abstract class IndexBuffer : GraphicsResource
{
    protected IndexBuffer(GraphicsResourceTracker? tracker, string debugName, int count, bool isDynamic = false)
        : base(tracker, debugName)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        Count = count;
        IsDynamic = isDynamic;
    }

    /// <summary>Number of indices (the capacity, for dynamic buffers).</summary>
    public int Count { get; }

    public bool IsDynamic { get; }

    /// <summary>Replaces the contents from offset 0. Only valid for dynamic buffers.</summary>
    public void SetData(ReadOnlySpan<ushort> indices)
    {
        ThrowIfDisposed();
        if (!IsDynamic)
        {
            throw new InvalidOperationException($"Index buffer '{DebugName}' is immutable.");
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(indices.Length, Count, nameof(indices));
        SetDataCore(indices);
    }

    protected virtual void SetDataCore(ReadOnlySpan<ushort> indices) =>
        throw new NotSupportedException("This backend does not implement dynamic index buffers.");
}
