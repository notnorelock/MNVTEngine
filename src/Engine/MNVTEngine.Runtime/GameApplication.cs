using System.Globalization;
using Microsoft.Extensions.Logging;
using MNVTEngine.Assets;
using MNVTEngine.Platform;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;

namespace MNVTEngine.Runtime;

/// <summary>
/// Development-only parts a host adds (Player, development exports). Without them the game runs with diagnostics off:
/// no debug UI, debug draws are no-ops, budgets are not monitored.
/// </summary>
/// <param name="CreateOverlay">The in-game debug UI; null runs diagnostics without UI (budget warnings still log).</param>
/// <param name="Logs">Ring buffer of recent log entries for the debug console (add it to the host's LogHub).</param>
public sealed record DevelopmentTools(Func<ILogger, IRuntimeOverlay>? CreateOverlay, Core.Logging.InMemoryLoggerProvider? Logs);

/// <summary>Entry point for standalone games.</summary>
public static partial class GameApplication
{
    /// <summary>
    /// Runs a project. Arguments: <c>[project path] [--frames N] [--screenshot file.png] [--gpu-validation] [--profile report.txt]
    /// [--overlay] [--no-diagnostics]</c> (<c>--overlay</c> shows the debug HUD from the start).
    /// Without a path, the single .mnvtproj next to the executable is used.
    /// </summary>
    /// <param name="prepareComponents">Registers game components (e.g. loads compiled scripts); returning false aborts startup.</param>
    /// <param name="archive">Content of an exported game; the project is read from it and a path argument is ignored.</param>
    /// <param name="createAudio">Audio backend; without one (or without an output device) the game runs silently.</param>
    /// <param name="tools">Development builds: diagnostics and the debug UI.</param>
    /// <returns>Process exit code: 0 ok, 1 resource leaks, 2 project or startup scene could not be loaded, 3 game code failed to load, 4 graphics could not start (no Vulkan 1.4 GPU or driver).</returns>
    public static int Run(string[] args, GraphicsDeviceFactory createDevice, ILoggerFactory loggerFactory, Func<GameProject, ComponentRegistry, bool>? prepareComponents = null, ContentArchiveSet? archive = null, AudioDeviceFactory? createAudio = null, DevelopmentTools? tools = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(createDevice);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        var logger = loggerFactory.CreateLogger("Game");

        GameProject project;
        try
        {
            project = archive is not null
                ? GameProject.LoadPackaged(archive, archive.Directory)
                : GameProject.Load(args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && !IsOptionValue(args, a)) ?? AppContext.BaseDirectory);
        }
        catch (Exception ex) when (ex is SerializationException or FileNotFoundException)
        {
            LogLoadFailed(logger, ex);
            return 2;
        }

        var registry = EngineComponents.CreateRegistry();
        if (prepareComponents is not null && !prepareComponents(project, registry))
        {
            return 3;
        }

        using var window = new GameWindow(new GameWindowOptions
        {
            Title = project.Window.Title ?? project.Name,
            Width = project.Window.Width,
            Height = project.Window.Height,
            VSync = project.Window.VSync,
            Resizable = project.Window.Resizable,
            FixedTimestep = project.FixedTimestep,
            GraphicsValidation = args.Contains("--gpu-validation"),
        });

        using var audio = AudioDevices.CreateOrSilent(createAudio, loggerFactory.CreateLogger("Audio"));
        var profileReport = Option(args, "--profile");
        var profiler = profileReport is null ? null : new Core.Diagnostics.FrameProfiler(history: 100_000);
        Core.Diagnostics.Profiler.Current = profiler;

        // Diagnostics data lives next to the game (exports) or in the project's Library folder (Player).
        var diagnosticsFolder = archive is not null ? Path.Combine(AppContext.BaseDirectory, "Logs") : Path.Combine(project.RootDirectory, "Library", "Diagnostics");
        RuntimeDiagnostics? diagnostics = null;
        IRuntimeOverlay? overlay = null;
        if (tools is not null && project.Diagnostics.Enabled && !args.Contains("--no-diagnostics"))
        {
            var settings = project.Diagnostics.Clone();
            settings.ShowOverlayByDefault |= args.Contains("--overlay");
            diagnostics = new RuntimeDiagnostics(settings, loggerFactory.CreateLogger("Diagnostics"), Path.Combine(diagnosticsFolder, "Profiles"), profiler);
            overlay = tools.CreateOverlay?.Invoke(loggerFactory.CreateLogger("DebugUI"));
        }

        var runtime = new GameRuntime(project, registry, createDevice, loggerFactory, new GameRuntimeOptions
        {
            ExitAfterFrames = Option(args, "--frames") is { } frames ? int.Parse(frames, CultureInfo.InvariantCulture) : null,
            ScreenshotPath = Option(args, "--screenshot"),
            Archive = archive,
            Audio = audio,
            Profiler = profiler,
            Diagnostics = diagnostics,
            Overlay = overlay,
            DebugLogs = tools?.Logs,
            DebugLayoutFile = Path.Combine(diagnosticsFolder, "debug-ui.ini"),
            DebugFontPath = DebugFont(),
            DebugCommands = [.. args.Select((a, i) => (a, i)).Where(p => p.a == "--debug-command" && p.i + 1 < args.Length).Select(p => args[p.i + 1])],
        });

        try
        {
            window.Run(runtime);
        }
        catch (SerializationException ex)
        {
            LogLoadFailed(logger, ex);
            return 2;
        }
        catch (Graphics.GraphicsInitializationException ex)
        {
            LogGraphicsFailed(logger, ex.Message);
            return 4;
        }

        if (profiler is not null)
        {
            Core.Diagnostics.Profiler.Current = null;
            var report = profiler.FormatReport();
            File.WriteAllText(profileReport!, report);
            var summary = profiler.Summarize();
            LogProfile(logger, profileReport!, summary.Frames, summary.AverageFrameMilliseconds, summary.P95FrameMilliseconds);
        }

        return runtime.ExitCode;
    }

    /// <summary>Segoe UI on Windows when present; otherwise the debug UI's built-in font.</summary>
    private static string? DebugFont() =>
        OperatingSystem.IsWindows() && Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf") is var path && File.Exists(path) ? path : null;

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool IsOptionValue(string[] args, string value)
    {
        var i = Array.IndexOf(args, value);
        return i > 0 && args[i - 1] is "--frames" or "--screenshot" or "--profile" or "--debug-command";
    }

    [LoggerMessage(EventId = 3201, Level = LogLevel.Information, Message = "Profile written to {Path}: {Frames} frames, avg {Average:0.00} ms, p95 {P95:0.00} ms")]
    private static partial void LogProfile(ILogger logger, string path, int frames, double average, double p95);

    [LoggerMessage(EventId = 3202, Level = LogLevel.Critical, Message = "Graphics could not start: {Reason}")]
    private static partial void LogGraphicsFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 3200, Level = LogLevel.Critical, Message = "Game could not start")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception);
}
