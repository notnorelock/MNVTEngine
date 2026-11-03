using Microsoft.Extensions.Logging;
using MNVTEngine.Core.Logging;
using MNVTEngine.Runtime;

namespace MNVTEngine.Runtime.Tests;

public sealed class LoggingSettingsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-logging-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void SaveAndLoad_RoundTripLevels_WithReadableJson()
    {
        using var hub = new LogHub();
        hub.Levels.Default = LogLevel.Warning;
        hub.Levels.Set("MNVTEngine.Physics2D", LogLevel.Debug);
        hub.CaptureCallSites = false;
        var path = Path.Combine(_root, "Settings", "logging.json");
        LoggingSettings.From(hub).Save(path);
        var json = File.ReadAllText(path);
        Assert.Contains("\"MNVTEngine.Physics2D\": \"Debug\"", json, StringComparison.Ordinal); // names kept as written
        Assert.Contains("\"default\": \"Warning\"", json, StringComparison.Ordinal);

        using var loaded = new LogHub();
        LoggingSettings.Load(path).ApplyTo(loaded);
        Assert.Equal(LogLevel.Debug, loaded.Levels.For("MNVTEngine.Physics2D.PhysicsWorld2D"));
        Assert.Equal(LogLevel.Warning, loaded.Levels.For("Other"));
        Assert.False(loaded.CaptureCallSites);
    }

    [Fact]
    public void MissingFile_GivesDefaults_AndBrokenFileIsReported()
    {
        Assert.Equal(LogLevel.Information, LoggingSettings.Load(Path.Combine(_root, "none.json")).Default);
        var path = Path.Combine(_root, "bad.json");
        File.WriteAllText(path, """{ "default": "Loud" }""");
        Assert.Throws<Serialization.SerializationException>(() => LoggingSettings.Load(path));
    }
}
