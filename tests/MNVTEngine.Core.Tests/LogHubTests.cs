using Microsoft.Extensions.Logging;
using MNVTEngine.Core.Logging;

namespace MNVTEngine.Core.Tests;

public sealed partial class LogHubTests
{
    private sealed class Sink : ILogSink
    {
        public List<LogEntry> Entries { get; } = [];

        public void Write(LogEntry entry) => Entries.Add(entry);
    }

    private static (LogHub Hub, Sink Sink, ILoggerFactory Factory) Create()
    {
        var sink = new Sink();
        var hub = new LogHub().AddSink(sink);
        return (hub, sink, LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(hub)));
    }

    [Fact]
    public void Levels_LongestMatchingNameWins()
    {
        var levels = new LogLevels { Default = LogLevel.Warning };
        levels.Set("Game", LogLevel.Information);
        levels.Set("Game.AI", LogLevel.Trace);
        Assert.Equal(LogLevel.Trace, levels.For("Game.AI.Pathfinder"));
        Assert.Equal(LogLevel.Information, levels.For("Game.Player"));
        Assert.Equal(LogLevel.Warning, levels.For("GameX.Player")); // dot boundary, not a plain prefix
        levels.Remove("Game.AI");
        Assert.Equal(LogLevel.Information, levels.For("Game.AI.Pathfinder")); // cache invalidated
    }

    [Fact]
    public void LoggerMessageCalls_RecordTheCallingMethod()
    {
        var (hub, sink, factory) = Create();
        using (hub)
        using (factory)
        {
            var logger = factory.CreateLogger<LogHubTests>();
            Report(logger, 3);
            hub.Levels.Set(typeof(LogHubTests).FullName!, LogLevel.Warning);
            Report(logger, 4); // filtered
        }

        var entry = Assert.Single(sink.Entries);
        Assert.Equal("Reported 3", entry.Message);
        Assert.Equal("MNVTEngine.Core.Tests", entry.Source!.Namespace);
        Assert.Equal(nameof(LogHubTests), entry.Source.Class);
        Assert.Equal(nameof(LoggerMessageCalls_RecordTheCallingMethod), entry.Source.Method);
        Assert.Contains(new KeyValuePair<string, object?>("Count", 3), entry.Properties);
        Assert.Equal($"{typeof(LogHubTests).FullName}.{nameof(LoggerMessageCalls_RecordTheCallingMethod)}", LogFormat.Source(entry));
    }

    [Fact]
    public async Task CompilerGeneratedMethods_MapBackToTheSourceMethod()
    {
        var (hub, sink, factory) = Create();
        using (hub)
        using (factory)
        {
            var logger = factory.CreateLogger("Async");
            await Task.Yield();
            Action lambda = () => Report(logger, 1);
            lambda();
        }

        var site = Assert.Single(sink.Entries).Source!;
        Assert.Equal(nameof(LogHubTests), site.Class);
        Assert.Equal(nameof(CompilerGeneratedMethods_MapBackToTheSourceMethod), site.Method);
        Assert.StartsWith("[Async] MNVTEngine.Core.Tests.LogHubTests.", LogFormat.Source(sink.Entries[0]), StringComparison.Ordinal);
    }

    [Fact]
    public void GameLog_RecordsExactSite_AndUsesItsNameForLevels()
    {
        var (hub, sink, factory) = Create();
        var previous = GameLog.Factory;
        GameLog.Factory = factory;
        try
        {
            var log = GameLog.For<LogHubTests>();
            log.Info("hello");
            log.Debug("hidden"); // default level is Information
            GameLog.For("AI.Brain").Error("boom", new InvalidOperationException("x"));
        }
        finally
        {
            GameLog.Factory = previous;
            factory.Dispose();
            hub.Dispose();
        }

        Assert.Equal(2, sink.Entries.Count);
        var info = sink.Entries[0];
        Assert.Equal(typeof(LogHubTests).FullName, info.Category);
        Assert.Equal(nameof(GameLog_RecordsExactSite_AndUsesItsNameForLevels), info.Source!.Method);
        Assert.EndsWith("LogHubTests.cs", info.Source.File, StringComparison.Ordinal);
        Assert.True(info.Source.Line > 0);
        var error = sink.Entries[1];
        Assert.Equal("AI.Brain", error.Category);
        Assert.Equal(nameof(LogHubTests), error.Source!.Class); // named logger: class from the stack
        Assert.IsType<InvalidOperationException>(error.Exception);
        Assert.Contains("ERR [AI.Brain] MNVTEngine.Core.Tests.LogHubTests.", LogFormat.Line(error), StringComparison.Ordinal);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Reported {Count}")]
    private static partial void Report(ILogger logger, int count);
}
