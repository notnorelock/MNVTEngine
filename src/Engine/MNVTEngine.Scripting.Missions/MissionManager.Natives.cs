using MNVTEngine.Scripting.VM;

namespace MNVTEngine.Scripting.Missions;

public sealed partial class MissionManager
{
    /// <summary>Implementations of <see cref="StandardApi.Missions"/>, bound to this manager.</summary>
    public IEnumerable<NativeFunction> CreateNatives() =>
    [
        new("mission.start", c =>
        {
            Start(c.String(0));
            return ScriptValue.Null;
        }),
        new("mission.complete", c =>
        {
            Complete(c.Count > 0 ? c.String(0) : CurrentMission(c, "mission.complete"));
            return ScriptValue.Null;
        }),
        new("mission.fail", c =>
        {
            if (c.Count == 2)
            {
                Fail(c.String(0), c[1].ToString());
            }
            else
            {
                Fail(CurrentMission(c, "mission.fail"), c.Count == 1 ? c[0].ToString() : null);
            }

            return ScriptValue.Null;
        }),
        new("mission.cancel", c =>
        {
            Cancel(c.Count > 0 ? c.String(0) : CurrentMission(c, "mission.cancel"));
            return ScriptValue.Null;
        }),
        new("mission.state", c =>
            ScriptValue.FromString(Get(c.Count > 0 ? c.String(0) : CurrentMission(c, "mission.state")).State.ToString().ToLowerInvariant())),
        new("objective.activate", c =>
        {
            ActivateObjective(CurrentMission(c, "objective.activate"), c.String(0));
            return ScriptValue.Null;
        }),
        new("objective.complete", c =>
        {
            CompleteObjective(CurrentMission(c, "objective.complete"), c.Count > 0 ? c.String(0) : CurrentObjective(c, "objective.complete"), c.Fiber);
            return ScriptValue.Null;
        }),
        new("objective.fail", c =>
        {
            FailObjective(CurrentMission(c, "objective.fail"), c.Count > 0 ? c.String(0) : CurrentObjective(c, "objective.fail"), c.Fiber);
            return ScriptValue.Null;
        }),
        new("objective.state", c =>
            ScriptValue.FromString(GetObjective(Get(CurrentMission(c, "objective.state")), c.String(0)).State.ToString().ToLowerInvariant())),
        new("objective.is_complete", c =>
            ScriptValue.FromBool(GetObjective(Get(CurrentMission(c, "objective.is_complete")), c.String(0)).State == ObjectiveState.Completed)),
    ];

    private static string CurrentMission(NativeCall call, string function) =>
        call.Fiber.Context is MissionContext context && !context.Mission.Contains('#', StringComparison.Ordinal)
            ? context.Mission
            : throw new ScriptRuntimeException($"{function}() without a mission id can only be used inside a mission.");

    private static string CurrentObjective(NativeCall call, string function) =>
        call.Fiber.Context is MissionContext { Objective: { } objective }
            ? objective
            : throw new ScriptRuntimeException($"{function}() without an objective id can only be used inside an objective.");
}
