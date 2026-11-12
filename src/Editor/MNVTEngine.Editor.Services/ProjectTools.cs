using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MNVTEngine.Build;
using MNVTEngine.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Editor.Services;

/// <summary>Recently opened projects (most recent first), kept per user.</summary>
public sealed class RecentProjects(string file)
{
    private const int Capacity = 12;

    public static string DefaultFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MNVTEngine", "recent-projects.json");

    /// <summary>Project files that still exist; an unreadable list is treated as empty.</summary>
    public IReadOnlyList<string> Load()
    {
        try
        {
            return File.Exists(file)
                ? [.. JArray.Parse(File.ReadAllText(file)).Values<string>().OfType<string>().Where(File.Exists)]
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void Add(string projectFile) => Save([Path.GetFullPath(projectFile), .. Load().Where(p => !SamePath(p, projectFile))]);

    public void Remove(string projectFile) => Save([.. Load().Where(p => !SamePath(p, projectFile))]);

    private void Save(IEnumerable<string> projects)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, new JArray(projects.Take(Capacity)).ToString(Formatting.Indented));
    }

    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Builds, validates and runs exported games from the editor without blocking it: work runs on a background task,
/// progress goes to the log (the Console), and <see cref="Update"/> (called each editor frame) finishes up on the
/// main thread.
/// </summary>
public sealed partial class BuildController(GameProject project, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("Build");
    private readonly Stopwatch _clock = new();
    private Task<IReadOnlyList<BuildDiagnostic>>? _validation;
    private Task<BuildResult>? _build;
    private bool _runWhenDone;

    /// <summary>Profile used by <see cref="StartBuild"/>; defaults to "release" (or the first profile).</summary>
    public string Profile { get; set; } = project.EffectiveBuildProfiles.FirstOrDefault(p => p.Name == "release")?.Name ?? project.EffectiveBuildProfiles[0].Name;

    /// <summary>
    /// Scripting backend for builds started here ("jit" or "aot"), overriding the profile's without changing the
    /// project file; null uses the profile's.
    /// </summary>
    public string? ScriptingBackend { get; set; }

    /// <summary>The backend the next build uses: <see cref="ScriptingBackend"/>, else the profile's.</summary>
    public string EffectiveScriptingBackend =>
        ScriptingBackend ?? project.EffectiveBuildProfiles.FirstOrDefault(p => p.Name == Profile)?.ScriptingBackend ?? "jit";

    public bool IsBusy => _build is not null || _validation is not null;

    /// <summary>"Building (12 s)", "Validating (2 s)", or null when idle.</summary>
    public string? Status => _build is not null ? $"Building ({_clock.Elapsed.TotalSeconds:0} s)" : _validation is not null ? $"Validating ({_clock.Elapsed.TotalSeconds:0} s)" : null;

    public BuildResult? LastBuild { get; private set; }

    public IReadOnlyList<BuildDiagnostic>? LastValidation { get; private set; }

    /// <summary>Output folder of <see cref="Profile"/>.</summary>
    public string OutputDirectory =>
        project.ResolveProjectFile(project.EffectiveBuildProfiles.FirstOrDefault(p => p.Name == Profile)?.OutputDirectory ?? Path.Combine("Builds", Profile));

    /// <exception cref="InvalidOperationException">A build or validation is already running.</exception>
    public void StartBuild(bool runWhenDone)
    {
        EnsureIdle();
        _runWhenDone = runWhenDone;
        var profile = Profile;
        var backend = ScriptingBackend;
        _clock.Restart();
        _build = Task.Run(() => new GameBuilder(_logger).BuildAsync(project.ProjectFile, new BuildOptions { Profile = profile, ScriptingBackend = backend }));
    }

    /// <exception cref="InvalidOperationException">A build or validation is already running.</exception>
    public void StartValidation()
    {
        EnsureIdle();
        _clock.Restart();
        _validation = Task.Run(() => new GameBuilder(_logger).ValidateProjectAsync(project.ProjectFile));
    }

    /// <summary>Completes finished work: reports diagnostics and the result, and starts the game if asked. Returns true when something finished.</summary>
    public bool Update()
    {
        if (_build is { IsCompleted: true } build)
        {
            _build = null;
            if (build.IsFaulted)
            {
                LogCrashed(_logger, build.Exception!.GetBaseException());
                return true;
            }

            var result = build.Result;
            LastBuild = result;
            Report(result.Diagnostics);
            if (result.Succeeded)
            {
                LogBuilt(_logger, result.Executable!, result.Duration.TotalSeconds);
                if (_runWhenDone)
                {
                    Run(result.Executable!);
                }
            }
            else
            {
                LogBuildFailed(_logger, result.Diagnostics.Count(d => d.Severity == BuildSeverity.Error));
            }

            return true;
        }

        if (_validation is { IsCompleted: true } validation)
        {
            _validation = null;
            if (validation.IsFaulted)
            {
                LogCrashed(_logger, validation.Exception!.GetBaseException());
                return true;
            }

            LastValidation = validation.Result;
            Report(validation.Result);
            var errors = validation.Result.Count(d => d.Severity == BuildSeverity.Error);
            var warnings = validation.Result.Count(d => d.Severity == BuildSeverity.Warning);
            LogValidated(_logger, errors, warnings);
            return true;
        }

        return false;
    }

    /// <summary>Starts the last successful build of <see cref="Profile"/>.</summary>
    /// <returns>False if that profile has not been built yet.</returns>
    public bool RunLastBuild()
    {
        var data = GamePackage.FindDataDirectory(OutputDirectory);
        if (data is null)
        {
            return false;
        }

        Run(Path.Combine(OutputDirectory, GamePackage.Load(data).ExecutableName + ".exe"));
        return true;
    }

    private void Run(string executable)
    {
        try
        {
            Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            LogRunFailed(_logger, ex, executable);
        }
    }

    private void Report(IEnumerable<BuildDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics.Where(d => d.Severity != BuildSeverity.Info))
        {
            var text = diagnostic.ToString();
            LogDiagnostic(_logger, diagnostic.Severity == BuildSeverity.Error ? LogLevel.Error : LogLevel.Warning, text);
        }
    }

    private void EnsureIdle()
    {
        if (IsBusy)
        {
            throw new InvalidOperationException("A build or validation is already running.");
        }
    }

    [LoggerMessage(EventId = 5400, Message = "{Diagnostic}")]
    private static partial void LogDiagnostic(ILogger logger, LogLevel level, string diagnostic);

    [LoggerMessage(EventId = 5401, Level = LogLevel.Information, Message = "Build succeeded in {Seconds:0.0} s: {Executable}")]
    private static partial void LogBuilt(ILogger logger, string executable, double seconds);

    [LoggerMessage(EventId = 5402, Level = LogLevel.Error, Message = "Build failed with {Errors} errors (see above)")]
    private static partial void LogBuildFailed(ILogger logger, int errors);

    [LoggerMessage(EventId = 5403, Level = LogLevel.Information, Message = "Validation finished: {Errors} errors, {Warnings} warnings")]
    private static partial void LogValidated(ILogger logger, int errors, int warnings);

    [LoggerMessage(EventId = 5404, Level = LogLevel.Error, Message = "The build tooling crashed")]
    private static partial void LogCrashed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 5405, Level = LogLevel.Error, Message = "Could not start {Executable}")]
    private static partial void LogRunFailed(ILogger logger, Exception exception, string executable);
}
