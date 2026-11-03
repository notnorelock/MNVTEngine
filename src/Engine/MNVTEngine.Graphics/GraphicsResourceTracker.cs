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
