namespace MNVTEngine.Graphics;

/// <summary>
/// Tracks live GPU resources for one device and enforces that they are created and disposed on the
/// thread that owns the graphics context.
/// </summary>
public sealed class GraphicsResourceTracker
{
    private readonly HashSet<GraphicsResource> _live = new(ReferenceEqualityComparer.Instance);
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;

    public int LiveCount => _live.Count;

    /// <summary>Per-frame rendering counters of this device.</summary>
    public GraphicsStatistics Statistics { get; } = new();

    /// <summary>Live objects by kind (walks the live set; no allocation).</summary>
    public GraphicsResourceCounts Count()
    {
        int textures = 0, targets = 0, shaders = 0, buffers = 0;
        long textureBytes = 0;
        foreach (var resource in _live)
        {
            switch (resource)
            {
                case Texture2D texture:
                    textures++;
                    targets += texture is RenderTarget2D ? 1 : 0;
                    textureBytes += (long)texture.Width * texture.Height * 4;
                    break;
                case Shader:
                    shaders++;
                    break;
                case VertexBuffer or IndexBuffer:
                    buffers++;
                    break;
            }
        }

        return new GraphicsResourceCounts(textures, targets, shaders, buffers, textureBytes);
    }

    public IReadOnlyList<GraphicsResource> LiveResources() => [.. _live];

    /// <summary>Disposes every resource still alive (leaks) and returns them for reporting.</summary>
    public IReadOnlyList<GraphicsResource> DisposeAll()
    {
        var leaked = LiveResources();
        foreach (var resource in leaked)
        {
            resource.Dispose();
        }

        return leaked;
    }

    internal void Track(GraphicsResource resource)
    {
        CheckThread();
        _live.Add(resource);
    }

    internal void Untrack(GraphicsResource resource)
    {
        CheckThread();
        _live.Remove(resource);
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException("GPU resources must be created and disposed on the graphics thread.");
        }
    }
}
