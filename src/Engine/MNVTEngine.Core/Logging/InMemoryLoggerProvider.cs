using Microsoft.Extensions.Logging;

namespace MNVTEngine.Core.Logging;

/// <summary>
/// Thread-safe ring buffer of recent log entries. Backs the editor console and runtime crash reports;
/// use it on its own as a provider, or as a sink of a <see cref="LogHub"/> (which then does the level filtering).
/// </summary>
public sealed class InMemoryLoggerProvider : ILoggerProvider, ILogSink
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

    /// <summary>Stores an entry the hub already filtered (<see cref="MinimumLevel"/> does not apply).</summary>
    public void Write(LogEntry entry) => Add(entry ?? throw new ArgumentNullException(nameof(entry)));

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
