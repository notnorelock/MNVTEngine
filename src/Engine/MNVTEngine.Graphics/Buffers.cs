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

/// <summary>Size of one index.</summary>
public enum IndexFormat
{
    UInt16,
    UInt32,
}

/// <summary>
/// Vertex buffer. Dynamic buffers have a fixed capacity and are re-uploaded by the CPU each frame they are drawn
/// (sprites, UI). Static buffers (<see cref="IGraphicsDevice.CreateStaticVertexBuffer"/>) are uploaded once to GPU
/// memory and drawn any number of frames (meshes).
/// </summary>
public abstract class VertexBuffer : GraphicsResource
{
    protected VertexBuffer(GraphicsResourceTracker? tracker, string debugName, VertexLayout layout, int capacity, bool isStatic = false)
        : base(tracker, debugName)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Layout = layout;
        Capacity = capacity;
        IsStatic = isStatic;
    }

    public VertexLayout Layout { get; }

    /// <summary>Maximum number of vertices (the vertex count, for static buffers).</summary>
    public int Capacity { get; }

    /// <summary>Uploaded once at creation; <see cref="SetData{T}"/> is not allowed.</summary>
    public bool IsStatic { get; }

    /// <summary>Replaces the buffer contents from offset 0. Only valid for dynamic buffers.</summary>
    /// <exception cref="InvalidOperationException">The buffer is static.</exception>
    public void SetData<T>(ReadOnlySpan<T> vertices)
        where T : unmanaged
    {
        ThrowIfDisposed();
        if (IsStatic)
        {
            throw new InvalidOperationException($"Vertex buffer '{DebugName}' is static.");
        }

        if (Unsafe.SizeOf<T>() != Layout.Stride)
        {
            throw new ArgumentException($"Vertex type {typeof(T).Name} is {Unsafe.SizeOf<T>()} bytes but the layout stride is {Layout.Stride}.", nameof(vertices));
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(vertices.Length, Capacity, nameof(vertices));
        SetDataCore(MemoryMarshal.AsBytes(vertices));
        Tracker?.Statistics.RecordVertexUpload(vertices.Length);
    }

    protected abstract void SetDataCore(ReadOnlySpan<byte> data);
}

/// <summary>
/// Index buffer: immutable (16- or 32-bit, in GPU memory), or dynamic 16-bit (re-uploaded by the CPU, e.g. for UI
/// geometry).
/// </summary>
public abstract class IndexBuffer : GraphicsResource
{
    protected IndexBuffer(GraphicsResourceTracker? tracker, string debugName, int count, bool isDynamic = false, IndexFormat format = IndexFormat.UInt16)
        : base(tracker, debugName)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (isDynamic && format != IndexFormat.UInt16)
        {
            throw new ArgumentException("Dynamic index buffers are 16-bit.", nameof(format));
        }

        Count = count;
        IsDynamic = isDynamic;
        Format = format;
    }

    public IndexFormat Format { get; }

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
