using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Core.Logging;

namespace MNVTEngine;

/// <summary>
/// A named logger for game and engine code: <c>GameLog.For&lt;Player&gt;()</c> or <c>GameLog.For("AI.Pathfinding")</c>;
/// every component also has one as <c>Log</c>. Messages carry their namespace, class, method, file and line, and the
/// name decides the level (see <see cref="LogLevels"/>, set in Settings/logging.json or the editor Console).
/// </summary>
public sealed class GameLog
{
    private ILoggerFactory? _factory;
    private ILogger _logger = NullLogger.Instance;

    private GameLog(string name, Type? owner)
    {
        Name = name;
        Owner = owner;
    }

    /// <summary>Set by the host (editor, game, player) at startup. Until then messages go nowhere.</summary>
    public static ILoggerFactory Factory { get; set; } = NullLoggerFactory.Instance;

    public string Name { get; }

    /// <summary>The class this logger belongs to, if created for one.</summary>
    public Type? Owner { get; }

    public static GameLog For<T>() => For(typeof(T));

    public static GameLog For(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return new GameLog(type.FullName ?? type.Name, type);
    }

    public static GameLog For(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new GameLog(name, null);
    }

    public bool IsEnabled(LogLevel level) => Logger.IsEnabled(level);

    public void Trace(string message, [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Write(LogLevel.Trace, message, null, member, file, line);

    public void Debug(string message, [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Write(LogLevel.Debug, message, null, member, file, line);

    public void Info(string message, [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Write(LogLevel.Information, message, null, member, file, line);

    public void Warn(string message, [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Write(LogLevel.Warning, message, null, member, file, line);

    public void Error(string message, Exception? exception = null, [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Write(LogLevel.Error, message, exception, member, file, line);

    public void Critical(string message, Exception? exception = null, [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Write(LogLevel.Critical, message, exception, member, file, line);

    private ILogger Logger
    {
        get
        {
            var factory = Factory;
            if (!ReferenceEquals(factory, _factory))
            {
                (_factory, _logger) = (factory, factory.CreateLogger(Name)); // picks up a host set after this logger was made
            }

            return _logger;
        }
    }

    private void Write(LogLevel level, string message, Exception? exception, string member, string file, int line)
    {
        var logger = Logger;
        if (!logger.IsEnabled(level))
        {
            return;
        }

        LogCallSite.Pending = Owner is { } owner
            ? LogCallSite.For(owner, member, file, line)
            : LogCallSite.Capture() is { } site ? site with { Method = member, File = file, Line = line } : new LogCallSite("", Name, member, file, line);
        try
        {
            logger.Log(level, default, message, exception, static (text, _) => text);
        }
        finally
        {
            LogCallSite.Pending = null;
        }
    }
}
