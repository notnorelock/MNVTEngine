using Microsoft.Extensions.Logging;

namespace MNVTEngine.Core.Logging;

/// <summary>One captured log message. <paramref name="Properties"/> holds the structured template values, if any.</summary>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    EventId EventId,
    string Message,
    Exception? Exception,
    IReadOnlyList<KeyValuePair<string, object?>> Properties);
