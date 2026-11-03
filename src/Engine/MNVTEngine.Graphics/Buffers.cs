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

/// <summary>Immutable 16-bit index buffer.</summary>
public abstract class IndexBuffer : GraphicsResource
{
    protected IndexBuffer(GraphicsResourceTracker? tracker, string debugName, int count)
        : base(tracker, debugName)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        Count = count;
    }

    public int Count { get; }
}
