using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MNVTEngine.Build;
using MNVTEngine.GameAssemblies;
using MNVTEngine.Serialization;

namespace MNVTEngine.Editor.Services;

public enum ScriptState
{
    /// <summary>The project has no C# scripts.</summary>
    None,
    Compiling,
    Ready,
    Failed,
}

/// <summary>
/// Keeps the editor's game code in sync with the scripts project: watches sources, compiles in the background, and
/// reloads. A reload saves the edit scene to JSON, swaps the component types in the registry, loads the scene back
/// (data, ids and selection survive; components whose class was removed become MissingComponent), and unloads the
/// previous generation. Static fields in game code reset on reload. In Play Mode the running game is saved and continued
/// the same way (see <see cref="PlayModeController.Restore"/>). Compile errors keep the last good code loaded and block
/// entering Play Mode.
/// </summary>
public sealed partial class ScriptReloadController : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CollectionCheckDelay = TimeSpan.FromSeconds(2);

    private readonly EditorSession _session;
    private readonly PlayModeController _playMode;
    private readonly ILogger _logger;
    private readonly ScriptProject? _project;
    private readonly GameAssemblyHost _host = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task<ScriptBuildResult>? _build;
    private string? _sourceStampOfLastBuild;
    private string? _readyAssembly;
    private DateTime _nextPoll;
    private WeakReference? _unloading;
    private DateTime _collectionCheckAt;

    public ScriptReloadController(EditorSession session, PlayModeController playMode, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _playMode = playMode;
        _logger = logger;
        try
        {
            _project = ScriptProject.For(session.Project);
        }
        catch (FileNotFoundException ex)
        {
            Fail([new ScriptDiagnostic(ScriptDiagnosticSeverity.Error, "SCRIPTS", ex.Message)]);
            return;
        }

        if (_project is null)
        {
            State = ScriptState.None;
        }
        else if (_project.IsBuildUpToDate())
        {
            _sourceStampOfLastBuild = _project.ComputeSourceStamp();
            _readyAssembly = _project.AssemblyPath; // loaded on the first Update, like any other build
        }
        else
        {
            StartBuild();
        }
    }

    public ScriptState State { get; private set; } = ScriptState.Compiling;

    /// <summary>Messages from the last build or load.</summary>
    public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; private set; } = [];

    public GameCode? Code => _host.Current;

    /// <summary>Result of the last unload verification: true once the previous generation was collected; null until checked.</summary>
    public bool? LastUnloadCollected { get; private set; }

    /// <summary>True while a build runs or a finished build waits to be loaded.</summary>
    public bool IsBusy => State == ScriptState.Compiling || _readyAssembly is not null;

    /// <summary>Play Mode needs compiled, loaded game code (or a project without scripts).</summary>
    public bool CanEnterPlayMode => State is ScriptState.None or ScriptState.Ready && _readyAssembly is null;

    public string StatusText => State switch
    {
        ScriptState.None => "No C# scripts",
        ScriptState.Compiling => "Compiling scripts...",
        ScriptState.Failed => $"Scripts: {Diagnostics.Count(d => d.Severity == ScriptDiagnosticSeverity.Error)} error(s)",
        _ => _readyAssembly is not null ? "Scripts compiled; reloading" : $"Scripts: {Code?.ComponentTypes.Count ?? 0} components",
    };

    /// <summary>Raised after game code was (re)loaded and the edit scene rebuilt with the new types.</summary>
    public event Action? Reloaded;

    /// <summary>Drives polling, build completion, deferred reloads and unload verification. Call once per editor frame.</summary>
    public void Update(DateTime utcNow)
    {
        if (_project is null)
        {
            return;
        }

        if (_build is { IsCompleted: true } build)
        {
            _build = null;
            Complete(build);
        }

        if (_readyAssembly is { } assembly)
        {
            _readyAssembly = null;
            Reload(assembly);
        }

        if (_build is null && utcNow >= _nextPoll)
        {
            _nextPoll = utcNow + PollInterval;
            if (_project.ComputeSourceStamp() != _sourceStampOfLastBuild)
            {
                StartBuild();
            }
        }

        if (_unloading is { } handle && utcNow >= _collectionCheckAt)
        {
            _unloading = null;
            LastUnloadCollected = GameAssemblyHost.IsCollected(handle);
            if (LastUnloadCollected == false)
            {
                LogNotCollected(_logger);
            }
        }
    }

    /// <summary>Forces a build even if sources look unchanged.</summary>
    public void Rebuild()
    {
        if (_project is not null && _build is null)
        {
            StartBuild();
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try
        {
            _build?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The build was cancelled; nothing to report while shutting down.
        }

        _shutdown.Dispose();
    }

    private void StartBuild()
    {
        _sourceStampOfLastBuild = _project!.ComputeSourceStamp();
        State = ScriptState.Compiling;
        LogCompiling(_logger, _project.ProjectFile);
        var project = _project;
        var token = _shutdown.Token;
        _build = Task.Run(() => ScriptCompiler.BuildAsync(project, token), token);
    }

    private void Complete(Task<ScriptBuildResult> build)
    {
        if (build.IsCanceled)
        {
            return;
        }

        if (build.IsFaulted)
        {
            Fail([new ScriptDiagnostic(ScriptDiagnosticSeverity.Error, "BUILD", build.Exception!.GetBaseException().Message)]);
            return;
        }

        var result = build.Result;
        Diagnostics = result.Diagnostics;
        foreach (var warning in result.Diagnostics.Where(d => d.Severity == ScriptDiagnosticSeverity.Warning))
        {
            LogDiagnostic(_logger, LogLevel.Warning, warning);
        }

        if (!result.Succeeded)
        {
            Fail(result.Diagnostics);
            return;
        }

        LogCompiled(_logger, result.Duration.TotalSeconds);
        _readyAssembly = result.AssemblyPath;
        State = ScriptState.Ready;
    }

    private void Reload(string assemblyPath)
    {
        var watch = Stopwatch.StartNew();
        var previous = _host.Current;

        // Save with the old types still registered, so every component can be written.
        var json = _session.Serializer.Serialize(_session.Scene);
        var play = _playMode.Capture();

        var load = _host.Load(assemblyPath);
        if (load.Code is null || load.Code == previous)
        {
            Fail(load.Diagnostics);
            return;
        }

        var diagnostics = load.Diagnostics.Concat(GameAssemblyHost.Register(_session.Registry, previous, load.Code)).ToList();
        foreach (var diagnostic in diagnostics)
        {
            LogDiagnostic(_logger, diagnostic.Severity == ScriptDiagnosticSeverity.Error ? LogLevel.Error : LogLevel.Warning, diagnostic);
        }

        var restored = _session.Serializer.Deserialize(json, _session.ScenePath ?? _session.Scene.Name);
        foreach (var diagnostic in restored.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info))
        {
            LogDiagnostic(_logger, LogLevel.Warning, diagnostic);
        }

        _session.ReplaceAfterScriptReload(restored.Scene);
        if (play is not null)
        {
            _playMode.Restore(play);
        }
        if (previous is not null)
        {
            _unloading = GameAssemblyHost.Unload(previous);
            _collectionCheckAt = DateTime.UtcNow + CollectionCheckDelay;
        }

        Diagnostics = diagnostics;
        State = diagnostics.Any(d => d.Severity == ScriptDiagnosticSeverity.Error) ? ScriptState.Failed : ScriptState.Ready;
        LogReloaded(_logger, load.Code.ComponentTypes.Count, watch.ElapsedMilliseconds);
        Reloaded?.Invoke();
    }

    private void Fail(IReadOnlyList<ScriptDiagnostic> diagnostics)
    {
        Diagnostics = diagnostics;
        State = ScriptState.Failed;
        foreach (var error in diagnostics.Where(d => d.Severity == ScriptDiagnosticSeverity.Error))
        {
            LogDiagnostic(_logger, LogLevel.Error, error);
        }
    }

    [LoggerMessage(EventId = 4100, Level = LogLevel.Information, Message = "Compiling {Project}...")]
    private static partial void LogCompiling(ILogger logger, string project);

    [LoggerMessage(EventId = 4101, Level = LogLevel.Information, Message = "Scripts compiled in {Seconds:0.0} s")]
    private static partial void LogCompiled(ILogger logger, double seconds);

    [LoggerMessage(EventId = 4102, Level = LogLevel.Information, Message = "Scripts reloaded: {Count} components ({Milliseconds} ms)")]
    private static partial void LogReloaded(ILogger logger, int count, long milliseconds);

    [LoggerMessage(EventId = 4103, Message = "{Diagnostic}")]
    private static partial void LogDiagnostic(ILogger logger, LogLevel level, object diagnostic);

    [LoggerMessage(EventId = 4104, Level = LogLevel.Warning, Message = "The previous script generation was not unloaded; something still references its types (memory is not reclaimed until restart)")]
    private static partial void LogNotCollected(ILogger logger);
}
