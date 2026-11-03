namespace MNVTEngine.Serialization;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <param name="Path">JSON path or logical location the message refers to.</param>
public sealed record SerializationDiagnostic(DiagnosticSeverity Severity, string Path, string Message)
{
    public override string ToString() => $"{Severity} at '{Path}': {Message}";
}

/// <summary>Fatal load failure. <see cref="Diagnostics"/> holds everything reported up to the failure.</summary>
public sealed class SerializationException : Exception
{
    public SerializationException(string source, string message, IReadOnlyList<SerializationDiagnostic> diagnostics, Exception? inner = null)
        : base($"Failed to load '{source}': {message}", inner)
    {
        SourceName = source;
        Diagnostics = diagnostics;
    }

    public string SourceName { get; }

    public IReadOnlyList<SerializationDiagnostic> Diagnostics { get; }
}
