using MNVTEngine.Scripting.Bytecode;
using MNVTEngine.Scripting.VM;

namespace MNVTEngine.Scripting.Missions;

public enum MissionState
{
    Inactive,
    Active,
    Completed,
    Failed,
    Cancelled,
}

public enum ObjectiveState
{
    Inactive,
    Active,
    Completed,
    Failed,
}

/// <summary>Fiber context: which mission (and objective) the running code belongs to. Spawned coroutines inherit it.</summary>
public sealed record MissionContext(string Mission, string? Objective);

public sealed class ObjectiveRuntime(ObjectiveInfo info)
{
    public ObjectiveInfo Info { get; } = info;

    public ObjectiveState State { get; internal set; }
}

public sealed class MissionRuntime(MissionInfo info)
{
    public MissionInfo Info { get; } = info;

    public MissionState State { get; internal set; }

    public string? FailReason { get; internal set; }

    public IReadOnlyDictionary<string, ObjectiveRuntime> Objectives { get; } =
        info.Objectives.ToDictionary(o => o.Id, o => new ObjectiveRuntime(o), StringComparer.Ordinal);
}

/// <summary>
/// Mission lifecycle over a <see cref="ScriptVM"/>. Rules:
/// <list type="bullet">
/// <item>Objectives start inactive. <c>objective.activate(id)</c> activates one (parallel objectives are simply several
/// active at once); an objective with <c>requires</c> activates automatically once every required objective is complete
/// (sequences and dependency graphs). Activating twice is a no-op.</item>
/// <item>An active objective runs its body as a coroutine. Completing or failing an objective from elsewhere stops that
/// body; an objective completing itself keeps running to the end of its body.</item>
/// <item>Ending a mission (complete, fail, cancel) stops all of its code, including the caller, then runs the matching
/// <c>on complete</c> / <c>on fail</c> / <c>on cancel</c> hook. Missions end only when told to.</item>
/// <item>Mission event handlers run only while their mission is active.</item>
/// </list>
/// </summary>
public sealed partial class MissionManager
{
    private readonly Dictionary<string, MissionRuntime> _missions;
    private ScriptVM? _vm;

    public MissionManager(CompiledScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        Script = script;
        _missions = script.Missions.ToDictionary(m => m.Id, m => new MissionRuntime(m), StringComparer.Ordinal);
    }

    public CompiledScript Script { get; }

    public IReadOnlyDictionary<string, MissionRuntime> Missions => _missions;

    public event Action<MissionRuntime>? MissionChanged;

    public event Action<MissionRuntime, ObjectiveRuntime>? ObjectiveChanged;

    private ScriptVM Vm => _vm ?? throw new InvalidOperationException("Attach a VM before using the mission manager.");

    /// <summary>Connects to the VM that was created with <see cref="CreateNatives"/>; call before <see cref="ScriptVM.Initialize"/>.</summary>
    public void Attach(ScriptVM vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        if (vm.Script != Script)
        {
            throw new ArgumentException("The VM runs a different script.", nameof(vm));
        }

        _vm = vm;
        vm.EventRaised += OnEvent;
    }

    /// <exception cref="KeyNotFoundException">Unknown mission.</exception>
    public void Start(string missionId)
    {
        var mission = Get(missionId);
        if (mission.State == MissionState.Active)
        {
            return;
        }

        mission.State = MissionState.Active;
        mission.FailReason = null;
        foreach (var objective in mission.Objectives.Values)
        {
            objective.State = ObjectiveState.Inactive;
        }

        MissionChanged?.Invoke(mission);
        RunHook(mission, "start");
    }

    public void Complete(string missionId) => End(Get(missionId), MissionState.Completed, "complete", null);

    public void Fail(string missionId, string? reason = null) => End(Get(missionId), MissionState.Failed, "fail", reason);

    public void Cancel(string missionId) => End(Get(missionId), MissionState.Cancelled, "cancel", null);

    public void ActivateObjective(string missionId, string objectiveId)
    {
        var mission = Get(missionId);
        var objective = GetObjective(mission, objectiveId);
        if (mission.State != MissionState.Active || objective.State != ObjectiveState.Inactive)
        {
            return;
        }

        objective.State = ObjectiveState.Active;
        ObjectiveChanged?.Invoke(mission, objective);
        Vm.Spawn(objective.Info.Body, [], new MissionContext(missionId, objectiveId));
    }

    public void CompleteObjective(string missionId, string objectiveId, ScriptFiber? caller = null) =>
        EndObjective(missionId, objectiveId, ObjectiveState.Completed, caller);

    public void FailObjective(string missionId, string objectiveId, ScriptFiber? caller = null) =>
        EndObjective(missionId, objectiveId, ObjectiveState.Failed, caller);

    private void EndObjective(string missionId, string objectiveId, ObjectiveState state, ScriptFiber? caller)
    {
        var mission = Get(missionId);
        var objective = GetObjective(mission, objectiveId);
        if (mission.State != MissionState.Active || objective.State is ObjectiveState.Completed or ObjectiveState.Failed)
        {
            return;
        }

        objective.State = state;
        Vm.Cancel(f => f != caller && f.Context is MissionContext c && c.Mission == missionId && c.Objective == objectiveId);
        ObjectiveChanged?.Invoke(mission, objective);

        if (state == ObjectiveState.Completed)
        {
            foreach (var dependent in mission.Objectives.Values.Where(o => o.State == ObjectiveState.Inactive && o.Info.Requires.Length > 0))
            {
                if (dependent.Info.Requires.All(r => mission.Objectives[r].State == ObjectiveState.Completed))
                {
                    ActivateObjective(missionId, dependent.Info.Id);
                }
            }
        }
    }

    private void End(MissionRuntime mission, MissionState state, string hook, string? reason)
    {
        if (mission.State != MissionState.Active)
        {
            return;
        }

        mission.State = state;
        mission.FailReason = reason;
        Vm.Cancel(f => f.Context is MissionContext c && c.Mission == mission.Info.Id);
        MissionChanged?.Invoke(mission);
        RunHook(mission, hook);
    }

    private void RunHook(MissionRuntime mission, string hook)
    {
        if (mission.Info.Hooks.TryGetValue(hook, out var function))
        {
            // End hooks run outside the mission context, so ending code cannot be cancelled by the mission it ends.
            Vm.Spawn(function, [], hook == "start" ? new MissionContext(mission.Info.Id, null) : new MissionContext(mission.Info.Id + "#" + hook, null));
        }
    }

    private void OnEvent(string name, ScriptValue payload)
    {
        foreach (var handler in Script.EventHandlers.Where(h => h.Mission is not null && h.EventName == name))
        {
            if (_missions[handler.Mission!].State == MissionState.Active)
            {
                Vm.StartHandler(handler.Function, payload, new MissionContext(handler.Mission!, null));
            }
        }
    }

    private MissionRuntime Get(string missionId) =>
        _missions.TryGetValue(missionId, out var mission) ? mission : throw new ScriptRuntimeException($"Unknown mission '{missionId}'.");

    private static ObjectiveRuntime GetObjective(MissionRuntime mission, string objectiveId) =>
        mission.Objectives.TryGetValue(objectiveId, out var objective)
            ? objective
            : throw new ScriptRuntimeException($"Mission '{mission.Info.Id}' has no objective '{objectiveId}'.");
}
