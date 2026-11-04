namespace MNVTEngine.Graphics;

/// <summary>The graphics backend cannot start on this machine (missing runtime, no suitable GPU, unsupported API version).</summary>
public sealed class GraphicsInitializationException : Exception
{
    public GraphicsInitializationException()
    {
    }

    public GraphicsInitializationException(string message)
        : base(message)
    {
    }

    public GraphicsInitializationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
