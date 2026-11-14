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
    /// <param name="content">Content of an exported game; its runtime configuration is read from it and a path argument is ignored.</param>
    /// <param name="tools">Development builds: diagnostics and the debug UI.</param>
    /// <returns>Process exit code: 0 ok, 1 resource leaks, 2 project or startup scene could not be loaded, 3 game code failed to load, 4 graphics could not start (no Vulkan 1.4 GPU or driver).</returns>
    /// <param name="available">The engine modules built into this host (an export: the generated bootstrap; development hosts: every module). The project's resolved modules are taken from it.</param>
    public static int Run(string[] args, GraphicsDeviceFactory createDevice, ILoggerFactory loggerFactory, IReadOnlyList<IEngineModule> available, Func<GameProject, ComponentRegistry, bool>? prepareComponents = null, GameContent? content = null, DevelopmentTools? tools = null, IReadOnlyList<IAssetImporter>? importers = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(createDevice);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        var logger = loggerFactory.CreateLogger("Game");

        GameProject project;
        try
        {
            project = content is not null
                ? GameProject.LoadPackaged(content)
                : GameProject.Load(args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && !IsOptionValue(args, a)) ?? AppContext.BaseDirectory);
        }
        catch (Exception ex) when (ex is SerializationException or FileNotFoundException)
        {
            LogLoadFailed(logger, ex);
            return 2;
        }

        try
        {
            // A project from before profiles plays with its inferred profile; the editor or `mnvt migrate` saves it.
            if (ProjectProfileMigration.Resolve(project) is { Inference: { } inference } resolution)
            {
                LogProfileInferred(logger, Modules.ProjectProfiles.DisplayName(resolution.Profile), inference.Describe());
                project.Profile = resolution.Profile;
            }
        }
        catch (ProfileConflictException ex)
        {
            LogLoadFailed(logger, ex);
            return 2;
        }

        // Only the project's modules register components and start; a scene that uses another module's components fails to load.
        // Exports were checked when they were built; a project played from its sources is checked here, so content of a
        // disabled module stops the game instead of loading as missing components.
        ArgumentNullException.ThrowIfNull(available);
        var modules = ProjectModules.Resolve(project);
        var (implementations, missing) = ProjectModules.Implementations(modules, available);
        IReadOnlyList<Modules.ModuleDiagnostic> problems = [.. content is null ? ProjectModules.Validate(project).Diagnostics : modules.Diagnostics, .. missing];
        foreach (var problem in problems.Where(d => d.Severity == Modules.ModuleSeverity.Error))
        {
            LogModuleProblem(logger, problem.Code, problem.Message);
        }

        if (problems.Any(d => d.Severity == Modules.ModuleSeverity.Error))
        {
            return 2;
        }

        var registry = ComponentRegistry.CreateDefault();
        var host = new ModuleHost(implementations);
        try
        {
            host.Start(registry);
        }
        catch (ModuleStartupException ex)
        {
            LogLoadFailed(logger, ex);
            return 2;
        }

        try
        {
            return RunGame(args, createDevice, loggerFactory, logger, project, registry, implementations, prepareComponents, content, tools, importers);
        }
        finally
        {
            try
            {
                host.Shutdown();
            }
            catch (ModuleShutdownException ex)
            {
                LogModulesShutdownFailed(logger, ex);
            }
        }
    }

    private static int RunGame(string[] args, GraphicsDeviceFactory createDevice, ILoggerFactory loggerFactory, ILogger logger, GameProject project, ComponentRegistry registry, IReadOnlyList<IEngineModule> modules,
        Func<GameProject, ComponentRegistry, bool>? prepareComponents, GameContent? content, DevelopmentTools? tools, IReadOnlyList<IAssetImporter>? importers)
    {
        // DLC and mods first: DLC can bring code, which loads with the game's (logged by the runtime).
        content?.MountExtraContent(project.Content, Path.GetDirectoryName(Path.GetFullPath(content.Directory))!);
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
            Mode = project.Window.Mode,
            MinWidth = project.Window.MinWidth,
            MinHeight = project.Window.MinHeight,
            MaxWidth = project.Window.MaxWidth,
            MaxHeight = project.Window.MaxHeight,
            Position = project.Window.X is { } x && project.Window.Y is { } y ? (x, y) : null,
            TopMost = project.Window.TopMost,
            Icon = WindowIcons(project, content, logger),
            FixedTimestep = project.FixedTimestep,
            GraphicsValidation = args.Contains("--gpu-validation"),
        });

        var profileReport = Option(args, "--profile");
        var profiler = profileReport is null ? null : new Core.Diagnostics.FrameProfiler(history: 100_000);
        Core.Diagnostics.Profiler.Current = profiler;

        // Diagnostics data lives next to the game (exports) or in the project's Library folder (Player).
        var diagnosticsFolder = content is not null ? Path.Combine(AppContext.BaseDirectory, "Logs") : Path.Combine(project.RootDirectory, "Library", "Diagnostics");
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
            Content = content,
            Modules = modules,
            Importers = importers,
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

    /// <summary>The project's window icon at the standard sizes; none (with a warning) when it cannot be read.</summary>
    private static IReadOnlyList<WindowIcon> WindowIcons(GameProject project, GameContent? content, ILogger logger)
    {
        if (project.Window.Icon is not { Length: > 0 } icon)
        {
            return [];
        }

        try
        {
            var bytes = content is not null
                ? content.Read(AssetDatabase.AssetsEntryFolder + AssetPaths.Canonical(icon))
                : File.ReadAllBytes(project.ResolveAsset(icon));
            return RuntimeWindow.IconsFrom(Graphics.Image.Decode(bytes, icon));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            LogIconFailed(logger, ex, icon);
            return [];
        }
    }

    [LoggerMessage(EventId = 3203, Level = LogLevel.Warning, Message = "Window icon {Icon} could not be loaded; using the default")]
    private static partial void LogIconFailed(ILogger logger, Exception exception, string icon);

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

    [LoggerMessage(EventId = 3205, Level = LogLevel.Critical, Message = "Modules: {Code} {Message}")]
    private static partial void LogModuleProblem(ILogger logger, string code, string message);

    [LoggerMessage(EventId = 3206, Level = LogLevel.Error, Message = "Modules failed to shut down")]
    private static partial void LogModulesShutdownFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3204, Level = LogLevel.Warning, Message = "The project has no profile; running as {Profile}. {Reason} Save it with `mnvt migrate` or the editor")]
    private static partial void LogProfileInferred(ILogger logger, string profile, string reason);

    [LoggerMessage(EventId = 3200, Level = LogLevel.Critical, Message = "Game could not start")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception);
}
