using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MNVTEngine.Core.Logging;

/// <summary>Receives every message that passed the level filter (console, file, editor console...).</summary>
public interface ILogSink
{
    void Write(LogEntry entry);
}

/// <summary>
/// Per-logger levels: a rule for a name applies to that logger and every logger below it ("MNVTEngine.Physics2D"
/// covers "MNVTEngine.Physics2D.PhysicsWorld2D"); the longest matching rule wins, otherwise <see cref="Default"/>.
/// Logger names are categories: the full class name for <c>GameLog.For&lt;T&gt;()</c>, or names like "Scripts".
/// </summary>
public sealed class LogLevels
{
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<string, LogLevel> _cache = new(StringComparer.Ordinal);
    private Dictionary<string, LogLevel> _rules = new(StringComparer.OrdinalIgnoreCase);
    private LogLevel _default = LogLevel.Information;

    public LogLevel Default
    {
        get => _default;
        set
        {
            _default = value;
            _cache.Clear();
        }
    }

    public IReadOnlyDictionary<string, LogLevel> Rules => _rules;

    public void Set(string name, LogLevel level)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_lock)
        {
            _rules = new Dictionary<string, LogLevel>(_rules, StringComparer.OrdinalIgnoreCase) { [name.Trim()] = level };
            _cache.Clear();
        }
    }

    public void Remove(string name)
    {
        lock (_lock)
        {
            var rules = new Dictionary<string, LogLevel>(_rules, StringComparer.OrdinalIgnoreCase);
            rules.Remove(name);
            _rules = rules;
            _cache.Clear();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _rules = new Dictionary<string, LogLevel>(StringComparer.OrdinalIgnoreCase);
            _cache.Clear();
        }
    }

    public LogLevel For(string name) => _cache.GetOrAdd(name, Resolve);

    private LogLevel Resolve(string name)
    {
        var rules = _rules;
        for (var prefix = name; prefix.Length > 0; prefix = prefix.LastIndexOf('.') is var dot and > 0 ? prefix[..dot] : "")
        {
            if (rules.TryGetValue(prefix, out var level))
            {
                return level;
            }
        }

        return _default;
    }
}

/// <summary>
/// The engine's logger provider: filters by <see cref="Levels"/>, records the call site (namespace, class, method) once
/// per message, and hands the entry to every sink. Register it as the only provider and let it decide the level:
/// <c>LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(hub))</c>.
/// </summary>
public sealed class LogHub(LogLevels? levels = null, TimeProvider? timeProvider = null) : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, Logger> _loggers = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private ILogSink[] _sinks = [];

    public LogLevels Levels { get; } = levels ?? new LogLevels();

    /// <summary>Walk the stack for messages logged through ILogger (GameLog always knows its site). Costs a few µs per message.</summary>
    public bool CaptureCallSites { get; set; } = true;

    /// <summary>Every logger name created so far (for level editors).</summary>
    public IReadOnlyCollection<string> Names => [.. _loggers.Keys.Order(StringComparer.Ordinal)];

    /// <summary>Adds a sink; the hub disposes disposable sinks with itself.</summary>
    public LogHub AddSink(ILogSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sinks = [.. _sinks, sink];
        return this;
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new Logger(this, name));

    public void Dispose()
    {
        foreach (var sink in _sinks.OfType<IDisposable>())
        {
            sink.Dispose();
        }

        _sinks = [];
    }

    private sealed class Logger(LogHub hub, string name) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= hub.Levels.For(name);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var site = LogCallSite.Pending ?? (hub.CaptureCallSites ? LogCallSite.Capture() : null);
            var properties = state is IReadOnlyList<KeyValuePair<string, object?>> structured
                ? [.. structured.Where(p => p.Key != "{OriginalFormat}")]
                : (IReadOnlyList<KeyValuePair<string, object?>>)[];
            var entry = new LogEntry(hub._time.GetUtcNow(), logLevel, name, eventId, formatter(state, exception), exception, properties, site);
            foreach (var sink in hub._sinks)
            {
                sink.Write(entry);
            }
        }
    }
}

/// <summary>The one line format shared by console and file: <c>12:00:01.250 WRN [Scripts] Ns.Class.Method: message</c>.</summary>
public static class LogFormat
{
    public static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "---",
    };

    /// <summary>"Ns.Class.Method", prefixed with the logger name when it is not simply that class ("[Scripts] ...").</summary>
    public static string Source(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Source switch
        {
            null => entry.Category,
            { } site when site.FullClass == entry.Category => site.ToString(),
            { } site => $"[{entry.Category}] {site}",
        };
    }

    public static string Line(LogEntry entry, bool includeException = true)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var text = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"{entry.Timestamp.ToLocalTime():HH:mm:ss.fff} {Level(entry.Level)} {Source(entry)}: {entry.Message}");
        if (includeException && entry.Exception is { } exception)
        {
            text.AppendLine().Append(exception);
        }

        return text.ToString();
    }
}

/// <summary>Colored console output in <see cref="LogFormat"/>.</summary>
public sealed class ConsoleLogSink : ILogSink
{
    private readonly Lock _lock = new();

    public void Write(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var line = LogFormat.Line(entry);
        lock (_lock)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = entry.Level switch
            {
                <= LogLevel.Debug => ConsoleColor.DarkGray,
                LogLevel.Warning => ConsoleColor.Yellow,
                >= LogLevel.Error => ConsoleColor.Red,
                _ => previous,
            };
            Console.Out.WriteLine(line);
            Console.ForegroundColor = previous;
        }
    }
}

/// <summary>Writes the log to a file (overwritten each run). On a write error it reports once to stderr and stops writing.</summary>
public sealed class FileLogSink : ILogSink, IDisposable
{
    private readonly Lock _lock = new();
    private StreamWriter? _writer;

    private FileLogSink(StreamWriter writer, string path)
    {
        _writer = writer;
        FullPath = path;
    }

    public string FullPath { get; }

    public static FileLogSink? TryCreate(string path, out string? error)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            error = null;
            return new FileLogSink(new StreamWriter(path, append: false) { AutoFlush = true }, Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"{path}: {ex.Message}";
            return null;
        }
    }

    public void Write(LogEntry entry)
    {
        var line = LogFormat.Line(entry);
        lock (_lock)
        {
            if (_writer is null)
            {
                return;
            }

            try
            {
                _writer.WriteLine(line);
            }
            catch (IOException ex)
            {
                // Logging must not take the game down (disk full, file removed); say so once and stop.
                Console.Error.WriteLine($"Log file '{FullPath}' failed, file logging stopped: {ex.Message}");
                _writer.Dispose();
                _writer = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
