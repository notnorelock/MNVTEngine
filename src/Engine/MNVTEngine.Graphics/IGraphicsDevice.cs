using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics;

public enum PrimitiveTopology
{
    Triangles,
    Lines,
}

/// <summary>One indexed draw. The device binds <see cref="Texture"/> to unit 0 and sets <c>uViewProjection</c>.</summary>
/// <param name="Scissor">Clip rectangle in pixels of the current target, origin bottom-left; null draws unclipped.</param>
public readonly record struct DrawCommand(
    Material Material,
    Texture2D Texture,
    VertexBuffer Vertices,
    IndexBuffer Indices,
    int IndexOffset,
    int IndexCount,
    Matrix4x4 ViewProjection,
    PrimitiveTopology Topology = PrimitiveTopology.Triangles,
    Rect? Scissor = null);

/// <summary>
/// Backend-independent rendering device. All members must be called on the graphics thread.
/// The device owns <see cref="Resources"/>; disposing it releases (and reports) anything still alive.
/// </summary>
public interface IGraphicsDevice : IDisposable
{
    GraphicsResourceTracker Resources { get; }

    /// <summary>Human-readable adapter/driver description.</summary>
    string Description { get; }

    int BackBufferWidth { get; }

    int BackBufferHeight { get; }

    /// <summary>Called by the host when the window framebuffer changes size.</summary>
    void ResizeBackBuffer(int width, int height);

    /// <param name="rgba">Width * Height * 4 bytes, row 0 = top of the image.</param>
    Texture2D CreateTexture(int width, int height, ReadOnlySpan<byte> rgba, SamplerState sampler, string debugName = "Texture");

    RenderTarget2D CreateRenderTarget(int width, int height, SamplerState sampler, string debugName = "RenderTarget");

    /// <summary>
    /// Replaces a rectangle of a texture (not a render target) with RGBA8 pixels, row 0 = top of the rectangle. Queued
    /// with the frame's uploads: no GPU wait. Draws recorded earlier in the same frame may see the new pixels, so only
    /// write regions nothing has drawn from yet (a glyph atlas fills empty space).
    /// </summary>
    /// <exception cref="NotSupportedException">The backend cannot update textures.</exception>
    void UpdateTexture(Texture2D texture, int x, int y, int width, int height, ReadOnlySpan<byte> rgba) =>
        throw new NotSupportedException("This graphics backend cannot update textures.");

    /// <exception cref="ShaderCompilationException">Compilation or linking failed.</exception>
    Shader CreateShader(ShaderSource source);

    VertexBuffer CreateVertexBuffer(VertexLayout layout, int capacity, string debugName = "VertexBuffer");

    /// <summary>Vertex data uploaded once to GPU memory (meshes); drawn any number of frames, never rewritten.</summary>
    /// <param name="vertices">Interleaved vertices: a multiple of <see cref="VertexLayout.Stride"/> bytes.</param>
    VertexBuffer CreateStaticVertexBuffer(VertexLayout layout, ReadOnlySpan<byte> vertices, string debugName = "StaticVertexBuffer");

    IndexBuffer CreateIndexBuffer(ReadOnlySpan<ushort> indices, string debugName = "IndexBuffer");

    /// <summary>Immutable 32-bit indices, for meshes with more than 65,536 vertices.</summary>
    IndexBuffer CreateIndexBuffer(ReadOnlySpan<uint> indices, string debugName = "IndexBuffer");

    /// <summary>Index buffer updated with <see cref="IndexBuffer.SetData"/>.</summary>
    IndexBuffer CreateDynamicIndexBuffer(int capacity, string debugName = "DynamicIndexBuffer");

    /// <summary>Redirects drawing to <paramref name="target"/>, or to the back buffer when null. Resets the viewport to the target size.</summary>
    void SetRenderTarget(RenderTarget2D? target);

    /// <summary>Viewport in pixels of the current target, origin bottom-left.</summary>
    void SetViewport(int x, int y, int width, int height);

    void Clear(Color color);

    void Draw(in DrawCommand command);

    /// <summary>Reads the whole current target as RGBA8, row 0 = top of the image.</summary>
    void ReadPixels(Span<byte> destination);

    /// <summary>
    /// Ends the frame: submits the recorded work and shows the back buffer. Hosts call it once at the end of each rendered
    /// frame. The next frame starts with the next command. Backends whose window presents by itself do nothing.
    /// </summary>
    void Present()
    {
    }

    /// <summary>Draw calls, uploads and state switches per frame (<c>Resources.Statistics</c>).</summary>
    GraphicsStatistics Statistics => Resources.Statistics;

    /// <summary>GPU timer queries; null when the backend has none.</summary>
    IGpuTimer? GpuTimer => null;

    /// <summary>Video memory from a vendor extension; <see cref="GpuMemoryInfo.Unsupported"/> without one. May cost a driver call: sample, don't poll.</summary>
    GpuMemoryInfo QueryGpuMemory() => GpuMemoryInfo.Unsupported;

    /// <summary>Waits for vertical blank when presenting. Changing it takes effect from the next frame (the swapchain is recreated).</summary>
    /// <exception cref="NotSupportedException">Setting it on a backend without presentation control.</exception>
    bool VSync
    {
        get => true;
        set => throw new NotSupportedException("This graphics backend cannot change VSync.");
    }

    /// <summary>The engine's own GPU allocations (exact, cheap to read); null when the backend has no allocator.</summary>
    GpuAllocationStatistics? AllocationStatistics => null;

    /// <summary>Backend facts for diagnostics panels: API and driver, swapchain, validation, caches... in display order.</summary>
    IReadOnlyList<KeyValuePair<string, string>> DescribeBackend() => [];
}

public static class GraphicsDeviceExtensions
{
    public static Texture2D CreateTexture(this IGraphicsDevice device, Image image, SamplerState sampler, string debugName = "Texture")
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(image);
        return device.CreateTexture(image.Width, image.Height, image.Pixels, sampler, debugName);
    }

    public static Texture2D LoadTexture(this IGraphicsDevice device, string path, SamplerState sampler) =>
        device.CreateTexture(Image.Load(path), sampler, Path.GetFileName(path));
}
