# Missions

The mission framework (`MNVTEngine.Scripting.Missions`) is generic: it supports quests, contracts, tutorials, races
and similar. It has no RPG concepts and no dependency on scenes or rendering.

## Declaring a mission

```mnvts
mission "escort" {
    name = "Escort"
    description = "Bring the guide home."
    category = "story"

    var arrived = false                 // mission variable (saved)

    on start { objective.activate("meet") }

    objective "meet" {
        description = "Meet the guide"
        wait for event "met_guide"
        objective.complete()
    }
    objective "protect" {               // starts when "meet" completes
        requires "meet"
        wait until arrived
        objective.complete()
    }
    objective "finish" {
        requires "protect"
        mission.complete()
    }

    on event "arrive" { arrived = true } // only while the mission is active
    on complete { log("done") }
    on fail { log("failed") }
}
```

## Rules

- Missions start with `mission.start(id)`, the `MissionRunner.StartMission` field, or `MissionManager.Start`.
  Starting an active mission does nothing.
- Objectives start **inactive**. `objective.activate(id)` activates one. Several active objectives run in **parallel**.
  An objective with `requires` activates automatically once **all** of the objectives it requires are completed,
  which covers **sequences** and dependency graphs. `requires` cycles and unknown ids are compile errors.
- An active objective runs its body as a coroutine. `objective.complete()` / `objective.fail()` without an id refer
  to the calling objective. Ending an objective from elsewhere stops its body. An objective that completes itself
  keeps running to the end of its body.
- `mission.complete()`, `mission.fail(reason?)` and `mission.cancel()` stop all of the mission's code, including the
  caller, and then run `on complete`, `on fail` or `on cancel`. A mission ends only when told to.
  Unfinished objectives keep their state.
- Coroutines started by mission code (`spawn`, `after`, `every`) belong to the mission and stop with it.

## Script API

Declared in `StandardApi` (the editor and importer check calls against it). It is implemented by `CoreNatives`,
`MissionManager.CreateNatives()` and `SceneNatives`:

| Module | Functions |
|---|---|
| core | `log(x)`, `time.now()`, `events.emit(name, payload?)` |
| mission | `start(id)`, `complete(id?)`, `fail(reason?)` / `fail(id, reason)`, `cancel(id?)`, `state(id?)` |
| objective | `activate(id)`, `complete(id?)`, `fail(id?)`, `state(id)`, `is_complete(id)` |
| player | `position()`, `distance_to(name)` |
| entity | `exists(name)`, `position(name)`, `distance(a, b)`, `set_active(name, bool)`, `destroy(name)` |
| marker | `show(name)`, `hide(name)` |
| scene | `load(path)` |
| audio | `play(name)`, `stop(name)`: the AudioSource on the named object; `set_volume(group, volume)`, `mute(group, bool)`: the scene's AudioMixer |
| dialogue | `start(id, data?)`: raises `dialogue_started` with `{id, data}` for the game's dialogue system |
| input | `key_down(key)`, `key_pressed(key)` |

Scene functions address objects by name and return plain values (`{x, y}` maps, numbers), never engine objects.

## Using missions in a scene

Add a `MissionRunner` component and set `Script` (a `.mnvts` asset). `StartMission` and `PlayerObject` are optional.
C# game code can interact with the script:

```csharp
var runner = scene.FindObjectsOfType<MissionRunner>().First();
runner.RaiseEvent("met_guide");                               // wakes 'wait for event' and runs 'on event' handlers
runner.EventEmitted += (name, payload) => { /* events.emit from scripts */ };
var state = runner.Missions!.Missions["escort"].State;
```

## Persistence

```csharp
string json = manager.Save().ToJson();                        // globals, mission variables, mission/objective states
var data = MissionSaveData.FromJson(json);                    // migrates to the current schema; rejects newer saves
vm.Initialize(); var problems = manager.Restore(data);        // into a fresh VM, before the first tick
```

- The save data has a `schemaVersion` (currently 1). Migrations are registered on `MissionSaveData.Migrations`.
  Malformed data, or a save from a newer version, throws `SerializationException`.
- Saved variables, missions or objectives that the current script no longer has are reported as warnings and
  skipped. Their values are never silently applied elsewhere.
- **Running coroutines are not saved.** After a restore, the body of each active objective starts again from its
  beginning, and `on start` hooks do not run again. Write objective bodies as conditions that can be checked again
  (`wait until ...`, `wait for event ...`) and keep progress in mission variables. Values that cannot be saved
  (internal iterators) produce a warning.
