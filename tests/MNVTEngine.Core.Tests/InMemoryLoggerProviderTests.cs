using Microsoft.Extensions.Logging;
using MNVTEngine.Core.Logging;

namespace MNVTEngine.Core.Tests;

public class InMemoryLoggerProviderTests
{
    [Fact]
    public void CapturesStructuredProperties()
    {
        using var provider = new InMemoryLoggerProvider();
        var logger = provider.CreateLogger("Test");

        logger.LogWarning("Loaded {AssetCount} assets in {Elapsed} ms", 3, 12.5);

        var entry = Assert.Single(provider.Snapshot());
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("Test", entry.Category);
        Assert.Equal("Loaded 3 assets in 12.5 ms", entry.Message);
        Assert.Contains(entry.Properties, p => p.Key == "AssetCount" && Equals(p.Value, 3));
    }

    [Fact]
    public void FiltersBelowMinimumLevel()
    {
        using var provider = new InMemoryLoggerProvider(minimumLevel: LogLevel.Warning);
        var logger = provider.CreateLogger("Test");
        logger.LogInformation("ignored");
        logger.LogError("kept");
        Assert.Equal("kept", Assert.Single(provider.Snapshot()).Message);
    }

    [Fact]
    public void RingBufferDropsOldestEntries()
    {
        using var provider = new InMemoryLoggerProvider(capacity: 3);
        var logger = provider.CreateLogger("Test");
        for (var i = 0; i < 5; i++)
        {
            logger.LogInformation("{Index}", i);
        }

        Assert.Equal(["2", "3", "4"], provider.Snapshot().Select(e => e.Message));
    }

    [Fact]
    public void RaisesEntryAddedAndKeepsException()
    {
        using var provider = new InMemoryLoggerProvider();
        LogEntry? received = null;
        provider.EntryAdded += e => received = e;

        var error = new InvalidOperationException("boom");
        provider.CreateLogger("Test").LogError(error, "Failed");

        Assert.NotNull(received);
        Assert.Same(error, received.Exception);
    }

    [Fact]
    public void IsThreadSafe()
    {
        using var provider = new InMemoryLoggerProvider(capacity: 10_000);
        var logger = provider.CreateLogger("Test");
        Parallel.For(0, 4000, i => logger.LogInformation("{Index}", i));
        Assert.Equal(4000, provider.Snapshot().Count);
    }
}
