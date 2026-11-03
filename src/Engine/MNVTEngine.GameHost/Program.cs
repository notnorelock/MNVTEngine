using Microsoft.Extensions.Logging;
using MNVTEngine.GameAssemblies;
using MNVTEngine.GameHost;
using MNVTEngine.Graphics.OpenGL;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;

// Exported game: runs the package in the Content folder next to the executable.
// Usage: <Game>.exe [--frames N] [--screenshot out.png] [--debug-gl]
var content = Path.Combine(AppContext.BaseDirectory, GamePackage.ContentFolder);
using var fileLog = FileLoggerProvider.TryCreate(Path.Combine(AppContext.BaseDirectory, "Logs", "game.log"), out var fileLogError);
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddSimpleConsole(o => o.SingleLine = true);
    if (fileLog is not null)
    {
        builder.AddProvider(fileLog);
    }
});
var logger = loggerFactory.CreateLogger("Game");
if (fileLogError is not null)
{
    HostLog.NoLogFile(logger, fileLogError);
}

GamePackage package;
try
{
    package = GamePackage.Load(content);
}
catch (SerializationException ex)
{
    HostLog.BadPackage(logger, ex);
    return 2;
}

HostLog.Starting(logger, package.Name, package.Profile, package.EngineVersion);
return GameApplication.Run(
    [content, .. args],
    (window, log) => GLGraphicsDevice.Create(window.GraphicsContext, window.FramebufferWidth, window.FramebufferHeight, log),
    loggerFactory,
    (_, registry) => LoadScripts(package, content, registry, logger));

static bool LoadScripts(GamePackage package, string content, ComponentRegistry registry, ILogger logger)
{
    if (package.ScriptsAssembly is null)
    {
        return true;
    }

    var load = new GameAssemblyHost().Load(Path.Combine(content, package.ScriptsAssembly));
    var diagnostics = load.Diagnostics.Concat(GameAssemblyHost.Register(registry, null, load.Code)).ToList();
    foreach (var diagnostic in diagnostics)
    {
        HostLog.Diagnostic(logger, diagnostic.Severity == ScriptDiagnosticSeverity.Error ? LogLevel.Error : LogLevel.Warning, diagnostic);
    }

    return load.Code is not null && diagnostics.All(d => d.Severity != ScriptDiagnosticSeverity.Error);
}

internal static partial class HostLog
{
    [LoggerMessage(EventId = 7000, Level = LogLevel.Information, Message = "{Game} ({Profile}), MNVTEngine {Version}")]
    public static partial void Starting(ILogger logger, string game, string profile, string version);

    [LoggerMessage(EventId = 7001, Level = LogLevel.Critical, Message = "The game content is missing or damaged; reinstall the game")]
    public static partial void BadPackage(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7002, Level = LogLevel.Warning, Message = "Log file not written: {Reason}")]
    public static partial void NoLogFile(ILogger logger, string reason);

    [LoggerMessage(EventId = 7003, Message = "{Diagnostic}")]
    public static partial void Diagnostic(ILogger logger, LogLevel level, object diagnostic);
}
