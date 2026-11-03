namespace MNVTEngine.Graphics;

/// <summary>
/// Base for every GPU object. Ownership is explicit: whoever creates a resource disposes it, on the
/// graphics thread. There is deliberately no finalizer, because GPU objects cannot be released from the
/// finalizer thread; resources still alive at device shutdown are reported as leaks by the tracker.
/// </summary>
public abstract class GraphicsResource : IDisposable
{
    private readonly GraphicsResourceTracker? _tracker;

    protected GraphicsResource(GraphicsResourceTracker? tracker, string debugName)
    {
        _tracker = tracker;
        DebugName = debugName;
        tracker?.Track(this);
    }

    public string DebugName { get; }

    /// <summary>The owning device's tracker (null for resources created without one, e.g. in tests).</summary>
    protected GraphicsResourceTracker? Tracker => _tracker;

    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        _tracker?.Untrack(this);
        IsDisposed = true;
        ReleaseCore();
        GC.SuppressFinalize(this);
    }

    /// <summary>Frees the native object. Called exactly once.</summary>
    protected abstract void ReleaseCore();

    protected void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    public override string ToString() => $"{GetType().Name}({DebugName})";
}
