using Microsoft.Extensions.Logging;
using MNVTEngine.Scenes;
using MNVTEngine.Scripting.Bytecode;
using MNVTEngine.Scripting.Compiler;
using MNVTEngine.Scripting.Missions;
using MNVTEngine.Scripting.VM;
using MNVTEngine.Serialization;

namespace MNVTEngine;

/// <summary>
/// Runs a <c>.mnvts</c> script in the scene: compiles it when the scene starts (the asset importer already reported its
/// errors when it was saved), then advances the VM once per frame. Game code talks to scripts through
/// <see cref="RaiseEvent"/> and <see cref="EventEmitted"/>.
/// </summary>
[ComponentId("MNVTEngine.MissionRunner")]
public sealed partial class MissionRunner : Component
{
    [SerializeField]
    [AssetType(AssetTypes.Script)]
    public AssetRef Script { get; set; }

    /// <summary>Mission id started after the script's 'on start' handlers are scheduled; empty to let the script decide.</summary>
    [SerializeField]
    public string StartMission { get; set; } = "";

    /// <summary>Name of the object <c>player.*</c> functions refer to.</summary>
    [SerializeField]
    public string PlayerObject { get; set; } = "Player";

    public ScriptVM? Vm { get; private set; }

    /// <summary>Debugger for the script VM (the editor's script debugger); applied as soon as the VM exists. Not saved.</summary>
    public IScriptDebugger? Debugger
    {
        get => _debugger;
        set
        {
            _debugger = value;
            if (Vm is { } vm)
            {
                vm.Debugger = value;
            }
        }
    }

    private IScriptDebugger? _debugger;
    private MissionSaveData? _resume;

    public MissionManager? Missions { get; private set; }

    /// <summary>Raised for every <c>events.emit</c> made by the script.</summary>
    public event Action<string, object?>? EventEmitted;

    /// <summary>Delivers a game event to waiting script code and handlers. Payload: null, bool, number, string, list or dictionary.</summary>
    public void RaiseEvent(string name, object? payload = null) =>
        (Vm ?? throw new InvalidOperationException("The mission script is not running.")).RaiseEvent(name, ScriptValue.FromObject(payload));

    public override void Start()
    {
        var logger = GameObject.Scene.Logger;
        if (Script.IsNone)
        {
            LogNoScript(logger, GameObject.Name);
            Enabled = false;
            return;
        }

        var reader = GameObject.Scene.TextAssets ?? throw new InvalidOperationException("The scene has no text asset reader; the host must set Scene.TextAssets.");
        if (LoadScript(reader, logger) is not { } script)
        {
            Enabled = false;
            return;
        }

        var resume = _resume;
        _resume = null;
        Run(script, resume);
    }

    /// <summary>
    /// Mission state to continue from instead of starting fresh, applied when this runner starts (the editor uses it
    /// to keep missions going when Play Mode reloads C# code).
    /// </summary>
    public void ResumeOnStart(MissionSaveData state) => _resume = state ?? throw new ArgumentNullException(nameof(state));

    /// <summary>
    /// Hot reload: recompiles the script from source and swaps it in, keeping variables and mission/objective states
    /// (active objective bodies restart; 'on start' does not run again). On compile errors the running script is kept.
    /// Returns false when nothing was reloaded.
    /// </summary>
    public bool Reload()
    {
        var logger = GameObject.Scene.Logger;
        if (Missions is not { } missions || GameObject.Scene.TextAssets is not { } reader || reader.ReadCompiled(Script, AssetTypes.Script) is not null)
        {
            return false; // not running, or exported bytecode (never changes)
        }

        var compiled = ScriptCompiler.Compile(reader.ReadText(Script, AssetTypes.Script), Script.ToString(), new CompileOptions { Api = StandardApi.All });
        foreach (var diagnostic in compiled.Diagnostics)
        {
            LogDiagnostic(logger, diagnostic.ToString());
        }

        if (!compiled.Succeeded)
        {
            LogReloadFailed(logger, GameObject.Name);
            return false;
        }

        Run(compiled.Script!, missions.Save());
        LogReloaded(logger, GameObject.Name);
        return true;
    }

    private void Run(CompiledScript script, MissionSaveData? resume)
    {
        var logger = GameObject.Scene.Logger;
        var missions = new MissionManager(script);
        ScriptVM? vm = null;
        vm = new ScriptVM(script, [
            .. CoreNatives.Create(() => vm!, message => LogScript(logger, message), (name, payload) => EventEmitted?.Invoke(name, payload.ToObject())),
            .. missions.CreateNatives(),
            .. SceneNatives.Create(GameObject.Scene, () => PlayerObject),
        ]);
        vm.FiberFaulted += fiber => LogFault(logger, fiber.Name, fiber.Error!.ToString());
        vm.Debugger = _debugger; // before the first statement runs, so breakpoints in 'on start' hit
        missions.Attach(vm);
        Vm = vm;
        Missions = missions;
        vm.Initialize(runStartHandlers: resume is null);
        if (resume is not null)
        {
            foreach (var diagnostic in missions.Restore(resume))
            {
                LogDiagnostic(logger, diagnostic.ToString());
            }
        }
        else if (StartMission.Length > 0)
        {
            missions.Start(StartMission);
        }
    }

    public override void Update()
    {
        using var sample = Core.Diagnostics.Profiler.Sample("Scripts.Missions");
        Vm?.Tick(Time.DeltaTime);
    }

    /// <summary>Exported games run the bytecode compiled at build time; in development the source is compiled here.</summary>
    private CompiledScript? LoadScript(ITextAssetReader reader, ILogger logger)
    {
        if (reader.ReadCompiled(Script, AssetTypes.Script) is { } bytecode)
        {
            try
            {
                return BytecodeSerializer.Read(bytecode);
            }
            catch (InvalidDataException ex)
            {
                LogBadBytecode(logger, ex, GameObject.Name);
                return null;
            }
        }

        var source = reader.ReadText(Script, AssetTypes.Script);
        var compiled = ScriptCompiler.Compile(source, Script.ToString(), new CompileOptions { Api = StandardApi.All });
        foreach (var diagnostic in compiled.Diagnostics)
        {
            LogDiagnostic(logger, diagnostic.ToString());
        }

        if (!compiled.Succeeded)
        {
            LogCompileFailed(logger, GameObject.Name);
            return null;
        }

        return compiled.Script;
    }

    [LoggerMessage(EventId = 6000, Level = LogLevel.Warning, Message = "MissionRunner on '{Owner}' has no script assigned; disabled")]
    private static partial void LogNoScript(ILogger logger, string owner);

    [LoggerMessage(EventId = 6001, Level = LogLevel.Warning, Message = "{Diagnostic}")]
    private static partial void LogDiagnostic(ILogger logger, string diagnostic);

    [LoggerMessage(EventId = 6002, Level = LogLevel.Error, Message = "Mission script on '{Owner}' has errors; disabled")]
    private static partial void LogCompileFailed(ILogger logger, string owner);

    [LoggerMessage(EventId = 6003, Level = LogLevel.Information, Message = "[script] {Message}")]
    private static partial void LogScript(ILogger logger, string message);

    [LoggerMessage(EventId = 6004, Level = LogLevel.Error, Message = "Script coroutine '{Fiber}' failed: {Error}")]
    private static partial void LogFault(ILogger logger, string fiber, string error);

    [LoggerMessage(EventId = 6006, Level = LogLevel.Information, Message = "Mission script on '{Owner}' reloaded; state kept")]
    private static partial void LogReloaded(ILogger logger, string owner);

    [LoggerMessage(EventId = 6007, Level = LogLevel.Error, Message = "Mission script on '{Owner}' has errors; the running version is kept")]
    private static partial void LogReloadFailed(ILogger logger, string owner);

    [LoggerMessage(EventId = 6005, Level = LogLevel.Error, Message = "Mission bytecode on '{Owner}' is invalid; disabled (rebuild the game)")]
    private static partial void LogBadBytecode(ILogger logger, Exception exception, string owner);
}

public static class ScriptingComponents
{
    public static ComponentRegistry Register(ComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Register<MissionRunner>();
    }
}

/// <summary>The "scripting" engine module: registers the mission runner, and reports mission VMs and their states.</summary>
public sealed class ScriptingModule : Scenes.IEngineModule, Scenes.IDiagnosticsIntegration
{
    public string Id => "scripting";

    public void Register(Scenes.ComponentRegistry components) => ScriptingComponents.Register(components);

    public IEnumerable<Diagnostics.IMetricsProvider> CreateMetricsProviders(Func<Scene?> scene) => [new ScriptingMetrics(scene)];

    public void RegisterCommands(Diagnostics.DebugCommandRegistry commands, Func<Scene?> scene)
    {
        ArgumentNullException.ThrowIfNull(commands);
        commands.Register("missions", "Mission VMs, and the mission and objective states of every MissionRunner.", _ => Missions(scene()));
    }

    private static Diagnostics.DebugCommandResult Missions(Scene? scene)
    {
        var runners = scene?.FindObjectsOfType<MissionRunner>().ToList() ?? [];
        var text = new System.Text.StringBuilder(System.FormattableString.Invariant($"Mission VMs: {runners.Count(r => r.Vm is not null)} running of {runners.Count}"));
        foreach (var runner in runners)
        {
            text.Append(System.FormattableString.Invariant($"\n  {runner.GameObject.Name}: {(runner.Vm is { } vm ? $"{vm.Fibers.Count} coroutines" : "not running")}"));
            foreach (var mission in runner.Missions?.Missions.Values ?? [])
            {
                text.Append(System.FormattableString.Invariant($"\n    {mission.Info.Id}: {mission.State}"));
                foreach (var objective in mission.Objectives.Values)
                {
                    text.Append(System.FormattableString.Invariant($"\n      {objective.Info.Id}: {objective.State}"));
                }
            }
        }

        return new Diagnostics.DebugCommandResult(true, text.ToString());
    }

    /// <summary>Mission VMs and their coroutines in the running scene.</summary>
    private sealed class ScriptingMetrics(Func<Scene?> scene) : Diagnostics.IMetricsProvider
    {
        private int _frame;
        private (int Vms, int Fibers) _counts;

        public string Name => "Scripting";

        public void Collect(Diagnostics.MetricsSnapshot metrics)
        {
            if (scene() is not { } current)
            {
                return;
            }

            if (_frame++ % 10 == 0)
            {
                var vms = current.FindObjectsOfType<MissionRunner>().Where(r => r.Vm is not null).ToList();
                _counts = (vms.Count, vms.Sum(r => r.Vm!.Fibers.Count));
            }

            metrics.Set(Diagnostics.Metrics.ScriptVms, _counts.Vms);
            metrics.Set(Diagnostics.Metrics.ScriptFibers, _counts.Fibers);
        }
    }
}
