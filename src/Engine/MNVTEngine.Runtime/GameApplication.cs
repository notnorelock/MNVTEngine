using System.Globalization;
using Microsoft.Extensions.Logging;
using MNVTEngine.Assets;
using MNVTEngine.Platform;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;

namespace MNVTEngine.Runtime;

/// <summary>Entry point for standalone games.</summary>
public static partial class GameApplication
{
    /// <summary>
    /// Runs a project. Arguments: <c>[project path] [--frames N] [--screenshot file.png] [--debug-gl] [--profile report.txt]</c>.
    /// Without a path, the single .mnvtproj next to the executable is used.
    /// </summary>
    /// <param name="prepareComponents">Registers game components (e.g. loads compiled scripts); returning false aborts startup.</param>
    /// <param name="archive">Content of an exported game; the project is read from it and a path argument is ignored.</param>
    /// <param name="createAudio">Audio backend; without one (or without an output device) the game runs silently.</param>
    /// <returns>Process exit code: 0 ok, 1 resource leaks, 2 project or startup scene could not be loaded, 3 game code failed to load.</returns>
    public static int Run(string[] args, GraphicsDeviceFactory createDevice, ILoggerFactory loggerFactory, Func<GameProject, ComponentRegistry, bool>? prepareComponents = null, ContentArchive? archive = null, AudioDeviceFactory? createAudio = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(createDevice);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        var logger = loggerFactory.CreateLogger("Game");

        GameProject project;
        try
        {
            project = archive is not null
                ? GameProject.LoadPackaged(archive, Path.GetDirectoryName(archive.FilePath)!)
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
            DebugContext = args.Contains("--debug-gl"),
        });

        using var audio = AudioDevices.CreateOrSilent(createAudio, loggerFactory.CreateLogger("Audio"));
        var profileReport = Option(args, "--profile");
        var profiler = profileReport is null ? null : new Core.Diagnostics.FrameProfiler(history: 100_000);
        Core.Diagnostics.Profiler.Current = profiler;
        var runtime = new GameRuntime(project, registry, createDevice, loggerFactory, new GameRuntimeOptions
        {
            ExitAfterFrames = Option(args, "--frames") is { } frames ? int.Parse(frames, CultureInfo.InvariantCulture) : null,
            ScreenshotPath = Option(args, "--screenshot"),
            Archive = archive,
            Audio = audio,
            Profiler = profiler,
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

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool IsOptionValue(string[] args, string value)
    {
        var i = Array.IndexOf(args, value);
        return i > 0 && args[i - 1] is "--frames" or "--screenshot" or "--profile";
    }

    [LoggerMessage(EventId = 3201, Level = LogLevel.Information, Message = "Profile written to {Path}: {Frames} frames, avg {Average:0.00} ms, p95 {P95:0.00} ms")]
    private static partial void LogProfile(ILogger logger, string path, int frames, double average, double p95);

    [LoggerMessage(EventId = 3200, Level = LogLevel.Critical, Message = "Game could not start")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception);
}
