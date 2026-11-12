namespace MNVTEngine.Scenes;

/// <summary>
/// The runtime side of an engine module (its catalog entry is <c>MNVTEngine.Modules.ModuleDefinition</c> with the same
/// <see cref="Id"/>). A <see cref="ModuleHost"/> calls <see cref="Register"/> on every module, then
/// <see cref="Initialize"/> on every module, both in startup order, and <see cref="Shutdown"/> in reverse. Each call
/// happens at most once. A module keeps no static state: what it starts in <see cref="Initialize"/> it stops in
/// <see cref="Shutdown"/>.
/// </summary>
public interface IEngineModule
{
    /// <summary>The catalog id, e.g. "physics.2d".</summary>
    string Id { get; }

    /// <summary>Adds the module's components (and nothing else) to the registry scenes are loaded with.</summary>
    void Register(ComponentRegistry components);

    /// <summary>Starts process-wide services. Most modules have none: their systems attach to each scene on first use.</summary>
    void Initialize()
    {
    }

    /// <summary>Stops what <see cref="Initialize"/> started. Called only after a successful <see cref="Initialize"/>.</summary>
    void Shutdown()
    {
    }
}

public enum ModuleHostState
{
    Created,
    Running,
    Failed,
    Stopped,
}

/// <summary>A module failed to register or initialize. Modules initialized before it were shut down again.</summary>
public sealed class ModuleStartupException(string module, string phase, Exception inner, IReadOnlyList<Exception> cleanupFailures)
    : InvalidOperationException($"Module '{module}' failed to {phase}: {inner.Message}" + (cleanupFailures.Count > 0 ? $" ({cleanupFailures.Count} module(s) also failed to shut down.)" : ""), inner)
{
    public string Module { get; } = module;

    /// <summary>"register" or "initialize".</summary>
    public string Phase { get; } = phase;

    /// <summary>Shutdown errors of the modules that were stopped again, by module.</summary>
    public IReadOnlyList<Exception> CleanupFailures { get; } = cleanupFailures;
}

/// <summary>One or more modules failed to shut down; every module was still asked to.</summary>
public sealed class ModuleShutdownException(IReadOnlyList<Exception> failures)
    : AggregateException($"{failures.Count} module(s) failed to shut down.", failures);

/// <summary>A module's shutdown failure, naming the module.</summary>
public sealed class ModuleFailureException(string module, Exception inner) : Exception($"Module '{module}': {inner.Message}", inner)
{
    public string Module { get; } = module;
}

/// <summary>
/// Runs the lifecycle of a set of modules, given in startup order (dependencies first, as the resolver orders them).
/// The host owns the modules it is given: it starts them once, and stops the initialized ones in reverse order once.
/// The registry passed to <see cref="Start"/> stays the caller's; after a failed start, discard it.
/// </summary>
public sealed class ModuleHost
{
    private readonly IReadOnlyList<IEngineModule> _modules;
    private readonly List<IEngineModule> _initialized = [];

    /// <exception cref="ArgumentException">Two modules share an id.</exception>
    public ModuleHost(IEnumerable<IEngineModule> modulesInStartupOrder)
    {
        ArgumentNullException.ThrowIfNull(modulesInStartupOrder);
        _modules = [.. modulesInStartupOrder];
        if (_modules.GroupBy(m => m.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            throw new ArgumentException($"Module '{duplicate.Key}' is given more than once.", nameof(modulesInStartupOrder));
        }
    }

    public ModuleHostState State { get; private set; }

    /// <summary>Ids of the modules, in startup order.</summary>
    public IReadOnlyList<string> Modules => [.. _modules.Select(m => m.Id)];

    /// <summary>Ids of the modules that are initialized now, in startup order.</summary>
    public IReadOnlyList<string> Initialized => [.. _initialized.Select(m => m.Id)];

    /// <summary>Registers every module's components, then initializes every module, both in startup order.</summary>
    /// <exception cref="InvalidOperationException">The host was already started.</exception>
    /// <exception cref="ModuleStartupException">A module failed; the ones initialized before it are shut down again.</exception>
    public void Start(ComponentRegistry components)
    {
        ArgumentNullException.ThrowIfNull(components);
        if (State != ModuleHostState.Created)
        {
            throw new InvalidOperationException($"The modules were already started (state {State}).");
        }

        State = ModuleHostState.Running;
        foreach (var (phase, step) in new (string, Action<IEngineModule>)[] { ("register", m => m.Register(components)), ("initialize", Initialize) })
        {
            foreach (var module in _modules)
            {
                try
                {
                    step(module);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Rethrown with the module's identity after undoing what already started.
                    State = ModuleHostState.Failed;
                    throw new ModuleStartupException(module.Id, phase, ex, StopInitialized());
                }
            }
        }
    }

    /// <summary>Shuts the initialized modules down in reverse startup order. Does nothing the second time.</summary>
    /// <exception cref="ModuleShutdownException">Modules failed to shut down; the others were still shut down.</exception>
    public void Shutdown()
    {
        if (State is not ModuleHostState.Running)
        {
            return;
        }

        State = ModuleHostState.Stopped;
        if (StopInitialized() is { Count: > 0 } failures)
        {
            throw new ModuleShutdownException(failures);
        }
    }

    private void Initialize(IEngineModule module)
    {
        module.Initialize();
        _initialized.Add(module);
    }

    private List<Exception> StopInitialized()
    {
        var failures = new List<Exception>();
        for (var i = _initialized.Count - 1; i >= 0; i--)
        {
            try
            {
                _initialized[i].Shutdown();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Every module is asked to stop; failures are reported together by the caller.
                failures.Add(new ModuleFailureException(_initialized[i].Id, ex));
            }
        }

        _initialized.Clear();
        return failures;
    }
}
