using Microsoft.Extensions.Logging;

namespace MNVTEngine.Core.Logging;

/// <summary>
/// One captured log message. <paramref name="Properties"/> holds the structured template values, if any;
/// <paramref name="Source"/> the namespace, class and method that logged it, when known.
/// </summary>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    EventId EventId,
    string Message,
    Exception? Exception,
    IReadOnlyList<KeyValuePair<string, object?>> Properties,
    LogCallSite? Source = null);
