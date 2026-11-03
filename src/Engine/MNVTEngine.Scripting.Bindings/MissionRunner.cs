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

        var missions = new MissionManager(script);
        ScriptVM? vm = null;
        vm = new ScriptVM(script, [
            .. CoreNatives.Create(() => vm!, message => LogScript(logger, message), (name, payload) => EventEmitted?.Invoke(name, payload.ToObject())),
            .. missions.CreateNatives(),
            .. SceneNatives.Create(GameObject.Scene, () => PlayerObject),
        ]);
        vm.FiberFaulted += fiber => LogFault(logger, fiber.Name, fiber.Error!.ToString());
        missions.Attach(vm);
        Vm = vm;
        Missions = missions;
        vm.Initialize();
        if (StartMission.Length > 0)
        {
            missions.Start(StartMission);
        }
    }

    public override void Update() => Vm?.Tick(Time.DeltaTime);

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
