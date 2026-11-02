using Microsoft.Extensions.Logging;

namespace MNVTEngine.Core.Logging;

/// <summary>
/// Thread-safe ring buffer of recent log entries. Backs the editor console and runtime crash reports;
/// other sinks (console, file) are ordinary <see cref="ILoggerProvider"/>s added alongside it.
/// </summary>
public sealed class InMemoryLoggerProvider : ILoggerProvider
{
    private readonly Queue<LogEntry> _entries;
    private readonly Lock _lock = new();
    private readonly TimeProvider _timeProvider;

    public InMemoryLoggerProvider(int capacity = 2000, LogLevel minimumLevel = LogLevel.Information, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
        MinimumLevel = minimumLevel;
        _entries = new Queue<LogEntry>(capacity);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int Capacity { get; }

    public LogLevel MinimumLevel { get; set; }

    /// <summary>Raised on the logging thread after an entry is stored. Handlers must be fast and must not log.</summary>
    public event Action<LogEntry>? EntryAdded;

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_lock)
        {
            return [.. _entries];
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    public void Dispose()
    {
        EntryAdded = null;
    }

    private void Add(LogEntry entry)
    {
        lock (_lock)
        {
            if (_entries.Count == Capacity)
            {
                _entries.Dequeue();
            }

            _entries.Enqueue(entry);
        }

        EntryAdded?.Invoke(entry);
    }

    private sealed class Logger(InMemoryLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= owner.MinimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var properties = state is IReadOnlyList<KeyValuePair<string, object?>> structured
                ? [.. structured]
                : (IReadOnlyList<KeyValuePair<string, object?>>)[];

            owner.Add(new LogEntry(owner._timeProvider.GetUtcNow(), logLevel, category, eventId, formatter(state, exception), exception, properties));
        }
    }
}
