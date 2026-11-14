using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using MNVTEngine.Assets;
using MNVTEngine.Core.Logging;
using MNVTEngine.GameAssemblies;
using MNVTEngine.IO.Packages;
using MNVTEngine.Graphics.Vulkan;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;

namespace MNVTEngine.GameHost;

// Exported game: runs the content packages listed in <Game>_Data/package.json next to the executable.
// Usage: <Game>.exe [--frames N] [--screenshot out.png] [--gpu-validation] [--no-crash-reporter]
internal static class Program
{
    private static int Main(string[] args)
    {
        HostLayout.Install();
        return Crashes.Handle(args) ?? Game.Run(args);
    }
}

internal static class Crashes
{
    /// <summary>
    /// The first process starts MNVTCrashHandler.exe (next to the executable), which runs the game as its child and
    /// watches it: crashes leave a report in Logs/Crashes next to the executable, with the dump and game.log, and a
    /// crash window. See docs/engine/crash-reporting.md.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)] // loads the crash reporting assembly only after HostLayout is installed
    public static int? Handle(string[] args) =>
        CrashReporting.CrashReporter.Run(args, new CrashReporting.CrashReporterOptions(
            typeof(Program).Assembly.GetName().Name!,
            Path.Combine(AppContext.BaseDirectory, "Logs", "Crashes"),
            Path.Combine(AppContext.BaseDirectory, "Logs", "game.log")));
}

internal static class Game
{
    [MethodImpl(MethodImplOptions.NoInlining)] // engine types load only after HostLayout is installed
    public static int Run(string[] args)
    {
        var host = typeof(Game).Assembly;
        var data = Path.Combine(AppContext.BaseDirectory, GamePackage.DataFolder(host.GetName().Name!));
        using var hub = new LogHub().AddSink(new ConsoleLogSink());
#if MNVT_DEVTOOLS
        var debugLogs = new InMemoryLoggerProvider(capacity: 5000);
        hub.AddSink(debugLogs);
        var tools = new DevelopmentTools(log => new MNVTEngine.DebugUI.DebugOverlay(log), debugLogs);
#else
        DevelopmentTools? tools = null; // Release: no debug UI, no diagnostics
#endif
        var fileLog = FileLogSink.TryCreate(Path.Combine(AppContext.BaseDirectory, "Logs", "game.log"), out var fileLogError);
        if (fileLog is not null)
        {
            hub.AddSink(fileLog);
        }

        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(hub));
        GameLog.Factory = loggerFactory;
        var logger = loggerFactory.CreateLogger("Game");
        if (fileLogError is not null)
        {
            HostLog.NoLogFile(logger, fileLogError);
        }

        // Optional logging.json next to the executable raises or lowers levels per logger without rebuilding.
        try
        {
            LoggingSettings.Load(Path.Combine(AppContext.BaseDirectory, "logging.json")).ApplyTo(hub);
        }
        catch (Exception ex) when (ex is SerializationException or IOException)
        {
            HostLog.BadLoggingSettings(logger, ex.Message);
        }

        GamePackage package;
        GameContent content;
        try
        {
            // The manifest must carry a valid signature by the key compiled into this executable, and every package
            // must be signed by it and be exactly the one the manifest lists.
            var signer = Metadata(host, GamePackage.VerificationKeyMetadata) is { } spki ? PackageVerificationKey.FromSubjectPublicKeyInfo(Convert.FromHexString(spki))
                : throw new InvalidDataException("The executable has no package verification key; rebuild the game with mnvt build.");
            var key = Metadata(host, GamePackage.PackageKeyMetadata) is { } embedded ? EmbeddedPackageKey.Decode(embedded)
                : throw new InvalidDataException("The executable has no package key; rebuild the game with mnvt build.");
            package = GamePackage.Load(data, signer);
            content = package.OpenContent(data, new PackageOpenOptions(new PackageKeyRing(key), [signer], RequireSignature: true));
        }
        catch (Exception ex) when (ex is SerializationException or IOException or InvalidDataException or FormatException or ArgumentException)
        {
            HostLog.BadPackage(logger, ex);
            return 2;
        }

        using (content)
        {
            HostLog.Starting(logger, package.Name, package.Profile, package.EngineVersion);
            return GameApplication.Run(
                args,
                (window, log) => VulkanGraphicsDevice.Create(window.VulkanSurface, window.FramebufferWidth, window.FramebufferHeight, log, VulkanDeviceOptions.ForApplication(package.Name, window.Options.VSync, window.Options.GraphicsValidation) with { PrecompiledShaders = content.TryRead }),
                loggerFactory,
                HostModules.Create(loggerFactory.CreateLogger("Audio")),
                (_, registry) => LoadScripts(package, content, registry, logger),
                content,
                tools);
        }
    }

    private static string? Metadata(Assembly host, string key) =>
        host.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } value ? value : null;

    private static bool LoadScripts(GamePackage package, GameContent content, ComponentRegistry registry, ILogger logger)
    {
#if MNVT_AOT
        // Ahead-of-time build: the game's C# (and the code of the DLC the project lists) is compiled into this
        // executable; components are registered from the lists the build generated (GameComponents.g.cs), with no
        // assembly loading and no type scanning. A DLC's components are registered once its package is mounted.
        _ = package;
        GameComponents.Register(registry);
        void LoadDlc(ExtraContentResult dlc)
        {
            if (dlc.Scripts is not null && !GameComponents.RegisterDlc(registry, dlc.Dlc ?? ""))
            {
                HostLog.DlcCodeNotBuiltIn(logger, dlc.Name);
            }
            else if (dlc.Scripts is not null)
            {
                HostLog.DlcCodeLoaded(logger, dlc.Name);
            }
        }
#else
        // Code comes only from trusted mounts: the official packages and DLC signed with the game's key (untrusted
        // mounts never serve Scripts/). DLC assemblies load into the game's context, so they see its types.
        var host = new GameAssemblyHost();
        var ok = true;
        byte[]? Read(string file) => content.TryRead(GamePackage.ScriptsEntryFolder + file);
        if (package.ScriptsAssembly is { } scripts)
        {
            var folder = scripts[..(scripts.LastIndexOf('/') + 1)];
            var load = host.Load(scripts[folder.Length..], file => content.TryRead(folder + file));
            ok = Report(load.Diagnostics.Concat(GameAssemblyHost.Register(registry, null, load.Code))) && load.Code is not null;
        }

        void LoadDlc(ExtraContentResult dlc)
        {
            if (dlc.Scripts is not { } file)
            {
                return;
            }

            if (Path.GetFileName(file) != file || !file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                HostLog.DlcCodeRejected(logger, dlc.Name, file);
                return;
            }

            var load = host.Current is null ? host.Load(file, Read) : host.LoadAdditional(file);
            if (Report(load.Diagnostics.Concat(GameAssemblyHost.Register(registry, null, load.Code))) && load.Code is not null)
            {
                HostLog.DlcCodeLoaded(logger, dlc.Name);
            }
        }

        bool Report(IEnumerable<ScriptDiagnostic> diagnostics)
        {
            var success = true;
            foreach (var diagnostic in diagnostics)
            {
                success &= diagnostic.Severity != ScriptDiagnosticSeverity.Error;
                HostLog.Diagnostic(logger, diagnostic.Severity == ScriptDiagnosticSeverity.Error ? LogLevel.Error : LogLevel.Warning, diagnostic);
            }

            return success;
        }
#endif

        // DLC mounted at startup, then DLC mounted later (keys from the game's server): failures are logged and the game runs on.
        foreach (var dlc in content.ExtraContent.Where(r => r.Status == ExtraContentStatus.Mounted))
        {
            LoadDlc(dlc);
        }

        content.ExtraContentMounted += LoadDlc;
#if MNVT_AOT
        return true;
#else
        return ok;
#endif
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

    [LoggerMessage(EventId = 7003, Level = LogLevel.Warning, Message = "logging.json ignored: {Reason}")]
    public static partial void BadLoggingSettings(ILogger logger, string reason);

    [LoggerMessage(EventId = 7004, Level = LogLevel.Information, Message = "Code of {Dlc} loaded")]
    public static partial void DlcCodeLoaded(ILogger logger, string dlc);

    [LoggerMessage(EventId = 7005, Level = LogLevel.Error, Message = "{Dlc} has code this build of the game does not contain (ahead-of-time builds compile DLC code in); update the game. Its content is available, its components are not")]
    public static partial void DlcCodeNotBuiltIn(ILogger logger, string dlc);

    [LoggerMessage(EventId = 7006, Level = LogLevel.Error, Message = "{Dlc} names '{File}' as its code, which is not an assembly file name; its code is not loaded")]
    public static partial void DlcCodeRejected(ILogger logger, string dlc, string file);

    [LoggerMessage(EventId = 7003, Message = "{Diagnostic}")]
    public static partial void Diagnostic(ILogger logger, LogLevel level, object diagnostic);
}
