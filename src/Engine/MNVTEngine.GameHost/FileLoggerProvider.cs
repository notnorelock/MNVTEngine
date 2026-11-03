using System.Globalization;
using Microsoft.Extensions.Logging;

namespace MNVTEngine.GameHost;

/// <summary>Writes the log to a file, so games without a console window still leave a trace. Overwritten on each start.</summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly Lock _lock = new();

    private FileLoggerProvider(StreamWriter writer) => _writer = writer;

    // shortcut: next to the executable only; games installed in a read-only folder get no log file. Move to %LOCALAPPDATA% when installers exist.
    public static FileLoggerProvider? TryCreate(string path, out string? error)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            error = null;
            return new FileLoggerProvider(new StreamWriter(path, append: false) { AutoFlush = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"{path}: {ex.Message}";
            return null;
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() => _writer.Dispose();

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:HH:mm:ss.fff} {level,-11} {category}: {message}");
        lock (_lock)
        {
            _writer.WriteLine(line);
            if (exception is not null)
            {
                _writer.WriteLine(exception.ToString());
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
