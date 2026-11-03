using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using MNVTEngine.Assets;
using MNVTEngine.GameAssemblies;
using MNVTEngine.Graphics.OpenGL;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;

namespace MNVTEngine.GameHost;

// Exported game: runs <Game>_Data/content.mnvtpak next to the executable.
// Usage: <Game>.exe [--frames N] [--screenshot out.png] [--debug-gl]
internal static class Program
{
    private static int Main(string[] args)
    {
        HostLayout.Install();
        return Game.Run(args);
    }
}

internal static class Game
{
    [MethodImpl(MethodImplOptions.NoInlining)] // engine types load only after HostLayout is installed
    public static int Run(string[] args)
    {
        var host = typeof(Game).Assembly;
        var data = Path.Combine(AppContext.BaseDirectory, GamePackage.DataFolder(host.GetName().Name!));
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
        ContentArchive archive;
        try
        {
            package = GamePackage.Load(data);
            archive = ContentArchive.Open(Path.Combine(data, GamePackage.ArchiveFile), ContentKey(host));
        }
        catch (Exception ex) when (ex is SerializationException or IOException or InvalidDataException or FormatException)
        {
            HostLog.BadPackage(logger, ex);
            return 2;
        }

        using (archive)
        {
            HostLog.Starting(logger, package.Name, package.Profile, package.EngineVersion);
            return GameApplication.Run(
                args,
                (window, log) => GLGraphicsDevice.Create(window.GraphicsContext, window.FramebufferWidth, window.FramebufferHeight, log),
                loggerFactory,
                (_, registry) => LoadScripts(package, archive, registry, logger),
                archive);
        }
    }

    /// <exception cref="InvalidDataException">The executable carries no content key.</exception>
    private static byte[] ContentKey(Assembly host) =>
        host.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == GamePackage.ContentKeyMetadata)?.Value is { Length: > 0 } hex
            ? Convert.FromHexString(hex)
            : throw new InvalidDataException("The executable has no content key; rebuild the game with mnvt build.");

    private static bool LoadScripts(GamePackage package, ContentArchive archive, ComponentRegistry registry, ILogger logger)
    {
        if (package.ScriptsAssembly is not { } scripts)
        {
            return true;
        }

        var folder = scripts[..(scripts.LastIndexOf('/') + 1)];
        var load = new GameAssemblyHost().Load(scripts[folder.Length..], file => archive.TryRead(folder + file));
        var diagnostics = load.Diagnostics.Concat(GameAssemblyHost.Register(registry, null, load.Code)).ToList();
        foreach (var diagnostic in diagnostics)
        {
            HostLog.Diagnostic(logger, diagnostic.Severity == ScriptDiagnosticSeverity.Error ? LogLevel.Error : LogLevel.Warning, diagnostic);
        }

        return load.Code is not null && diagnostics.All(d => d.Severity != ScriptDiagnosticSeverity.Error);
    }
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
