# Getting Started

This walkthrough creates a game, runs it, edits it in the editor, and exports a standalone Windows build. You need the .NET 11 SDK and a GPU with a Vulkan 1.4 driver. Install the [LunarG Vulkan SDK](https://vulkan.lunarg.com/) as well if you want validation-layer checks in development builds.

## 1. Create a project

Start the editor without a project (`scripts\mnvt.cmd editor`, or run `MNVTEngine.Editor.exe`). The **Project Hub** opens. It lists recent projects, creates a new one from a template (name, location, template), or opens an existing folder. The command line does the same:

```bash
scripts\mnvt.cmd new MyGame --template basic-2d --path C:\Games
```

`basic-2d` is a player moved by a C# component, a goal, and a `.mnvts` mission. `empty-2d` is a camera in an empty scene.

```text
MyGame/
├── MyGame.mnvtproj          name, startup scene, window, build profiles
├── Assets/                  scenes, textures, clips, materials, scripts, sounds (each with a .meta GUID)
├── Scripts/                 C# game components (a class library)
├── Settings/content.key     encrypts exported content; commit it
├── Library/                 caches; never commit
└── Builds/                  exported games; never commit
```

## 2. Run it

```bash
scripts\mnvt.cmd run C:\Games\MyGame
```

The first run compiles `Scripts/`. Later runs reuse the build until a `.cs` file changes.

## 3. Edit it

```bash
scripts\mnvt.cmd editor C:\Games\MyGame
```

- **Hierarchy, Scene and Inspector:** select an object, move it with the gizmos (W/E/R), and edit components. **GameObject → Add Component** adds the built-ins:
  - Sprite, SpriteAnimator and Camera;
  - Rigidbody2D and the colliders, Tilemap and TilemapCollider2D;
  - AudioSource and AudioListener, Light2D and GlobalLight2D;
  - MissionRunner.
- **Project panel:** drop a texture into the Scene view to create a sprite, and select an asset to see its import settings.
- **Play (Ctrl+P):** plays a copy of the scene. Edits made while playing are discarded on Stop.
- **Undo (Ctrl+Z) and redo (Ctrl+Y):** cover every scene edit.
- **C# hot reload:** save a `.cs` file in `Scripts/` and the editor recompiles and swaps the components while keeping scene data.
- **Script debugging:** select a `.mnvts` asset, click a line number to set a breakpoint, then play. See the [script debugger](scripting/language.md#debugging).
- **Profiling:** the **Profiler** panel shows where frame time goes. See [profiling](engine/profiling.md).

## 4. Write gameplay

**C# components** (in `Scripts/`):

```csharp
using MNVTEngine;
using MNVTEngine.Mathematics;

public sealed class Spinner : Component
{
    public float DegreesPerSecond = 90f;   // public fields and [SerializeField] properties are saved and shown in the Inspector

    public override void Update() => Transform.Rotation += DegreesPerSecond * Time.DeltaTime;
}
```

No registration or ids are needed: a component is saved under its `Namespace.Class` name. After renaming a class, add `[ComponentAlias("Old.Namespace.OldName")]` so existing scenes still load.

`.meta` files hold each asset's GUID. The editor creates them for new assets (so does `mnvt build`, with a reminder to commit them). Never write them by hand; keep them in version control next to their assets. C# files in `Scripts/` have none.

Lifecycle: `Awake`, `OnEnable`, `Start`, `FixedUpdate`, `Update`, `LateUpdate`, `OnDisable`, `OnDestroy`. Physics callbacks come from `ICollisionHandler2D` and `ITriggerHandler2D`. Facades: `Time`, `Input`, `SceneManager`, `Physics2D`, `Prefab`.

**Missions and gameplay scripts** (`.mnvts`, sandboxed, coroutine-based):

```text
mission "tutorial" {
    on start { objective.activate("move") }
    objective "move" {
        description = "Walk to the door"
        wait until player.distance_to("Door") < 1
        audio.play("Chime")
        mission.complete()
    }
}
```

Run a script with a `MissionRunner` component (`Script`, `StartMission`).

## 5. Export

In the editor, use the **Build** menu:

- **Build Settings** chooses the profile (debug or release) and shows the output folder.
- **Build** (Ctrl+B) and **Build and Run** (Ctrl+Shift+B) export the game. **Validate Project** checks it without exporting.
- Builds run in the background; progress and problems appear in the Console. Unsaved scene changes are offered for saving first, because builds use the files on disk.

Or from the command line:

```bash
scripts\mnvt.cmd build C:\Games\MyGame --configuration release --run
C:\Games\MyGame\Builds\release\MyGame.exe
```

The output folder contains `MyGame.exe`, the dependency folders (`Dependencies/Managed`, `Dependencies/Native`, `Dependencies/Runtime`) and the encrypted content archives in `MyGame_Data/` (`globals`, `defaultresources`, one `levelN` per scene, and `sharedassets`/`resources` when needed). It needs no .NET installation and no editor, and the build validates it before replacing the previous one. See [building](build-system/building.md).

## Samples

| Sample | Shows |
|---|---|
| `samples/Platformer` | A complete small level. A physics player with run/jump animations and sounds, a tilemap level with collision, trigger coins, a mission (collect the coins, then reach the flag), lights and a follow camera. A test plays it with simulated input. |
| `samples/SceneDemo` | Scenes and prefabs, C# hot reload, a multi-objective mission with a chime, animated coins, a physics crate pile on a tilemap, lights and a custom-shader material (Tab switches scenes) |
| `samples/HelloWorld` | The renderer used directly, without scenes |

```bash
scripts\mnvt.cmd run samples\Platformer          # A/D run, Space jump
scripts\mnvt.cmd editor samples\Platformer
```
