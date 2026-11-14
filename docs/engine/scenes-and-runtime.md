# Scenes, ECS and the standalone runtime

## Layers

| Project | Role |
|---|---|
| `MNVTEngine.ECS` | `World`: entities with generations, sparse-set component pools (structs and classes), `Query<...>`, `GameSystem` + `SystemScheduler`. |
| `MNVTEngine.Input` | Backend-free `InputState`, `Key`, `MouseButton`, axes. Fed by `Platform`. |
| `MNVTEngine.Scenes` | Gameplay API in namespace `MNVTEngine`: `Scene`, `GameObject`, `Component`, `Transform`, `SpriteRenderer`, `Camera`, `Time`, `Input`, `SceneManager`. Infrastructure in `MNVTEngine.Scenes`: registry, serializer, `MissingComponent`. No graphics or window dependency. |
| `MNVTEngine.Runtime` | `GameProject` (`.mnvtproj`), `ResourceCache`, `SceneRenderer`, `GameRuntime`, `GameApplication.Run`. The graphics backend is injected by the host. |

## The hybrid ECS

- A `GameObject` is one ECS entity. Every component attached to it, including object components such as `Transform` and user scripts, is also stored in the world pool for its concrete type.
- Systems can therefore query object components directly (`world.Query<SpriteRenderer>()`), and the renderer works this way.
- Plain struct components can be attached to `gameObject.Entity` through `scene.World` for data-oriented code driven by a `GameSystem`.
- **Rule:** a GameObject holds at most one component of each concrete type. Subclasses count as different types.
- Pools are sparse sets: O(1) add/remove/has, with dense iteration.
- Queries walk the first type's pool backwards. While iterating you may destroy the current entity or create new ones, but you must not remove components from *other* entities. Gameplay code is unaffected because scene-level destruction is always deferred.
- Entity handles carry a generation, so a handle to a destroyed entity is detectably stale even after its index is reused.

## Lifecycle

Callbacks run only while the scene is running (`scene.Start()`). In edit mode the scene is plain data: no callbacks run and destruction is immediate.

| Callback | When |
|---|---|
| `Awake` | Once, when the object is first active while the scene runs. `Start()` awakes every object before enabling any. |
| `OnEnable` / `OnDisable` | Whenever the component becomes, or stops being, active-and-enabled. This follows `Enabled`, `SetActive`, parenting and destruction. |
| `Start` | Once, before the component's first update after its first enable. Components added during a frame start next frame. |
| `FixedUpdate` | Every fixed step (`Time.DeltaTime` is the fixed step here). |
| `Update`, then systems, then `LateUpdate`, then late systems | Every frame. |
| `OnDestroy` | At the end of the frame in which `Destroy` was called (children first), if `Awake` ran. |

- Exceptions from callbacks are logged (event 2000) with the component and object name, and the frame continues. Set `Scene.RethrowScriptExceptions` to make them propagate instead.
- Only overridden update methods are called; the engine detects overrides once per type by reflection.
- `Time` and `Input` are static facades over the scene that is currently executing on this thread. Scenes on different threads (editor, tests) don't interfere. Using them outside a callback throws.
- `Time.TimeScale` (per scene, default 1, reset by a new scene) scales `Time.DeltaTime` and the fixed steps from the next frame:
  - At 0 the game is paused: `Update` still runs with a zero delta; `FixedUpdate` and physics do not.
  - `Time.UnscaledDeltaTime` and the game UI keep running, so pause menus work.
  - The runtime and editor Play Mode both apply it to their clock.

## Transform

- 3D (Unity-style API):
  - local values: `LocalPosition` (`Vector3`), `LocalRotation` (`Quaternion`), `LocalEulerAngles` (degrees), `LocalScale` (`Vector3`);
  - world values: `Position`, `Rotation`, `EulerAngles`, `LossyScale`, composed through the parent chain;
  - directions: `Forward` (local -Z), `Right`, `Up`;
  - operations: `TransformPoint`/`InverseTransformPoint`, `TransformDirection`/`InverseTransformDirection`, `TransformVector`, `LookAt`, `Rotate`.
- 2D helpers:
  - `Rotation2D` and `LocalRotation2D` are the angle about Z in degrees, counter-clockwise; setting one replaces any X/Y rotation.
  - Positions accept `Vector2` (z = 0).
  - Scale: `Vector2` converts to `Vector3` with **z = 0**, which makes the transform non-invertible. Set scale with `SetLocalScale2D(v)` or a `Vector3`.
- `LocalToWorldMatrix` and `WorldToLocalMatrix` are cached and invalidated down the subtree when a transform or an ancestor changes.
- `LocalEulerAngles` returns the angles last set while the rotation is unchanged, so inspectors can edit one angle without the others jumping (and `LocalRotation2D += x` keeps accumulating).
- `SetParent(parent, worldPositionStays = true)` rejects cycles and parenting across scenes.
- With a rotated parent that also has non-uniform scale, the matrix is exact, but `LossyScale` is an approximation.

## Scene files (`.mnvt`, schema 3)

```json
{
  "schemaVersion": 1,
  "name": "Main",
  "gameObjects": [
    { "id": "<guid>", "name": "Player", "active": true, "parent": null,
      "transform": { "localPosition": { "x": 0, "y": 0, "z": 0 }, "localRotation": { "x": 0, "y": 0, "z": 0, "w": 1 }, "localScale": { "x": 1, "y": 1, "z": 1 } },
      "components": [ { "type": "MNVTEngine.SpriteRenderer", "id": "<guid>", "enabled": true, "data": { "texture": "asset:<guid>" } } ] }
  ]
}
```

Schema 2 files (2D transforms: `{x, y}` positions and scales, rotation in degrees about Z) are migrated on load: z = 0 for positions, z = 1 for scales, the angle becomes a quaternion. An Info diagnostic notes the migration; saving writes schema 3.

- **Identity:** every GameObject and component has a stable GUID. A Transform shares its GameObject's id.
- **Ordering:** objects are written parents-first in sibling order. On load, sibling order follows file order.
- **Types:** `type` is a stable id from the explicit `ComponentRegistry`. It is the full type name, or the value of `[ComponentId("...")]` when one is set. `[ComponentAlias("Old.Name")]` keeps old files loading after a rename. Data can never name a CLR type to instantiate.
- **Data:** public fields, plus fields or properties marked `[SerializeField]`, minus `[DontSerialize]`. Names are camelCase with a leading `_` removed. Members typed `GameObject` or `Component`, or collections of them, are stored as id strings and resolved after every object exists.
- **Robustness:** problems inside the file become diagnostics and the rest of the scene still loads:

| Problem | Result |
|---|---|
| Unknown component type | Kept as `MissingComponent`; its data is preserved on save |
| Unknown data member | Warning; value ignored |
| Bad value | Error for that component; its default values are kept |
| Dangling reference | Warning; set to null |
| Duplicate or missing id | A new id is assigned |
| Parent cycle or missing parent | Error; the object stays at root |

- **Fatal errors:** only an unreadable document throws `SerializationException`. That means malformed JSON, duplicate keys, a missing or newer `schemaVersion`, or no `gameObjects` array.
- **Migrations:** register upgrade steps in `SceneSerializer.Migrations` (a `JsonMigrationPipeline`) when the schema changes. Each step upgrades the document by one version.
- **Schema 1 → 2:** sprite textures change from paths to `AssetRef`s. Paths are resolved through the asset database; unknown paths are cleared with a warning.
- **Prefabs** (`.mnvtprefab`) use the same format; see [assets](assets.md#prefabs).

## Game projects

```text
MyGame/
├── MyGame.mnvtproj   { "schemaVersion": 2, "name", "profile": "2d" | "3d", "startupScene", "window": {...}, "fixedTimestep", "streaming": { "textures", "textureBudgetMB", "unusedSeconds" }, "content": { "dlc", "mods", "dlcCode": { "<DLC name>": "<csproj>" } } }
└── Assets/
    ├── Scenes/Main.mnvt
    └── Textures/player.png
```

- Asset paths are relative to `Assets/`. Absolute paths, and paths that escape `Assets/` (for example `../`), are rejected. This is a trust boundary for project files.
- Every asset has a `.meta` file with its GUID; see [assets](assets.md). A standalone game opens the asset database read-only.
- The project file is strict: unknown keys are errors. It is validated on load: name, [profile](project-profiles.md), timestep, window size, and that the startup scene exists.
- **MSBuild note:** MSBuild treats any `*.*proj` file as a project. If a `.mnvtproj` sits next to the game's `.csproj`, name the `.csproj` explicitly in `dotnet build`. The solution build is unaffected.

## Standalone runtime

A game host is a few lines (see `src/Tooling/MNVTEngine.Player/Program.cs`):

```csharp
return GameApplication.Run(args,
    (window, logger) => VulkanGraphicsDevice.Create(window.VulkanSurface, window.FramebufferWidth, window.FramebufferHeight, logger,
        VulkanDeviceOptions.ForApplication("MyGame", window.Options.VSync, window.Options.GraphicsValidation)),
    loggerFactory,
    (project, registry) => LoadScripts(project, registry)); // false aborts startup (exit code 3)
```

- **Project discovery:** the host uses the project path argument, or else the single `.mnvtproj` next to the executable.
- **Options:** `--frames N` exits after N frames, `--screenshot file.png` saves the last frame, and `--gpu-validation` enables the Vulkan validation layer. Exit code 4 means no Vulkan 1.4 GPU or driver was found.
- **Exit codes:** 0 ok, 1 GPU resource leak, 2 project or startup scene unreadable.
- **Scene switching:** `SceneManager.LoadScene(path)` switches at the end of the frame. A broken target scene is logged and the current scene keeps running.
- **DontDestroyOnLoad:** `gameObject.DontDestroyOnLoad()` (in a component: `DontDestroyOnLoad(GameObject)` or
  `DontDestroyOnLoad(this)`) keeps a root object and its children when a scene loads. The host moves them into the new
  scene before unloading the old one (`Scene.MovePersistentObjectsTo`).
  - They are the same objects and components, with their state, and they get no second Awake or OnEnable. A pending
    Start and a destroy queued that frame still happen. They keep persisting through later loads.
  - Engine services move with them, through `Component.OnMovedToScene(previous)`:
    - physics bodies, keeping their velocity, now colliding with the new scene;
    - a playing `AudioSource` keeps playing (its voice and clip move);
    - a `UIDocument` keeps its context and the fonts it uses.
  - Unlike Unity there is no separate "DontDestroyOnLoad" scene: the objects are part of the new scene (`Find`,
    `FindObjectsOfType`, the editor's Hierarchy in Play Mode).
  - Only roots persist: for a child it logs a warning and does nothing.
  - Loading a scene that contains the same object again gives the carried one a new id. Avoid duplicates as in Unity:
    a singleton that destroys the newcomer.
  - The flag is runtime state: it is not saved in scene files. Play Mode keeps it across a C# code reload.
- **Missing textures:** each missing or corrupt texture is logged once and drawn as a magenta checkerboard.
- **Rendering:** `SceneRenderer` draws every active `SpriteRenderer` through `Scene.MainCamera` (the first active camera). Sprites are sorted by `SortingOrder`, higher on top. Off-screen sprites are culled with a conservative bounding circle.
