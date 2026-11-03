using MNVTEngine.Scripting.Compiler;
using MNVTEngine.Scripting.VM;

namespace MNVTEngine.Scripting.Missions;

/// <summary>
/// Declarations of the engine's script API. Declarations are pure data, so tools (asset import, editor) can check
/// scripts without the engine; implementations are supplied by hosts: <see cref="MissionManager"/> (mission, objective),
/// <see cref="CoreNatives"/> (log, time, events) and the scene bindings (player, entity, marker, scene, dialogue, input).
/// </summary>
public static class StandardApi
{
    public static ScriptApi Core { get; } = new ScriptApi()
        .Add("log", 1, 1, "Writes a message to the game log.")
        .Add("time.now", 0, 0, "Script time in seconds since the script started.")
        .Add("events.emit", 1, 2, "events.emit(name, payload?): raises an event for scripts and game code.");

    public static ScriptApi Missions { get; } = new ScriptApi()
        .Add("mission.start", 1, 1, "Starts a mission by id.")
        .Add("mission.complete", 0, 1, "Completes the current (or given) mission and runs its 'on complete'.")
        .Add("mission.fail", 0, 2, "mission.fail(reason?) or mission.fail(id, reason): fails a mission and runs 'on fail'.")
        .Add("mission.cancel", 0, 1, "Cancels the current (or given) mission and runs 'on cancel'.")
        .Add("mission.state", 0, 1, "inactive, active, completed, failed or cancelled.")
        .Add("objective.activate", 1, 1, "Activates an objective of the current mission and runs its body.")
        .Add("objective.complete", 0, 1, "Completes the current (or given) objective; objectives that require it may activate.")
        .Add("objective.fail", 0, 1, "Fails the current (or given) objective.")
        .Add("objective.state", 1, 1, "inactive, active, completed or failed.")
        .Add("objective.is_complete", 1, 1, "True if the objective is completed.");

    public static ScriptApi Scene { get; } = new ScriptApi()
        .Add("player.position", 0, 0, "Player position as {x, y}.")
        .Add("player.distance_to", 1, 1, "Distance from the player to the named object.")
        .Add("entity.exists", 1, 1, "True if an active object with this name exists.")
        .Add("entity.position", 1, 1, "Position of the named object as {x, y}.")
        .Add("entity.distance", 2, 2, "Distance between two named objects.")
        .Add("entity.set_active", 2, 2, "Shows or hides the named object.")
        .Add("entity.destroy", 1, 1, "Destroys the named object.")
        .Add("marker.show", 1, 1, "Activates the named marker object.")
        .Add("marker.hide", 1, 1, "Deactivates the named marker object.")
        .Add("scene.load", 1, 1, "Loads a scene by path relative to Assets.")
        .Add("audio.play", 1, 1, "Plays the AudioSource on the named object.")
        .Add("audio.stop", 1, 1, "Stops the AudioSource on the named object.")
        .Add("audio.set_volume", 2, 2, "Sets a mixer group's volume (0 to 1), e.g. audio.set_volume(\"Music\", 0.5).")
        .Add("audio.mute", 2, 2, "Mutes or unmutes a mixer group: audio.mute(\"Music\", true).")
        .Add("dialogue.start", 1, 2, "Raises 'dialogue_started' with {id, data} for the game's dialogue system.")
        .Add("input.key_down", 1, 1, "True while the named key (e.g. \"E\", \"Space\") is held.")
        .Add("input.key_pressed", 1, 1, "True on the frame the named key was pressed.");

    /// <summary>Everything the engine declares; game code may add more modules.</summary>
    public static ScriptApi All { get; } = new ScriptApi().Merge(Core).Merge(Missions).Merge(Scene);
}

/// <summary>Host-independent implementations of <see cref="StandardApi.Core"/>.</summary>
public static class CoreNatives
{
    /// <param name="vm">Resolves the VM the functions belong to (created after the natives).</param>
    /// <param name="log">Receives log messages.</param>
    /// <param name="emitted">Called for every scripted <c>events.emit</c>, so game code can react.</param>
    public static IEnumerable<NativeFunction> Create(Func<ScriptVM> vm, Action<string> log, Action<string, ScriptValue>? emitted = null)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(log);
        return
        [
            new("log", c =>
            {
                log(c[0].ToString());
                return ScriptValue.Null;
            }),
            new("time.now", _ => ScriptValue.FromNumber(vm().Time)),
            new("events.emit", c =>
            {
                var name = c.String(0);
                vm().RaiseEvent(name, c[1]);
                emitted?.Invoke(name, c[1]);
                return ScriptValue.Null;
            }),
        ];
    }
}
