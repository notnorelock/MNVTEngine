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

## Transform

- 2D: `LocalPosition`, `LocalRotation` (degrees, counter-clockwise), `LocalScale`.
- World values (`Position`, `Rotation`, `LossyScale`) are composed through the parent chain. `LocalToWorldMatrix` is cached and invalidated down the subtree.
- `SetParent(parent, worldPositionStays = true)` rejects cycles and parenting across scenes.
- With a rotated parent that also has non-uniform scale, the matrix is exact, but `Rotation` and `LossyScale` are approximations.

## Scene files (`.mnvt`, schema 2)

```json
{
  "schemaVersion": 1,
  "name": "Main",
  "gameObjects": [
    { "id": "<guid>", "name": "Player", "active": true, "parent": null,
      "transform": { "localPosition": { "x": 0, "y": 0 }, "localRotation": 0, "localScale": { "x": 1, "y": 1 } },
      "components": [ { "type": "MNVTEngine.SpriteRenderer", "id": "<guid>", "enabled": true, "data": { "texture": "asset:<guid>" } } ] }
  ]
}
```

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
├── MyGame.mnvtproj   { "schemaVersion": 1, "name", "startupScene", "window": {...}, "fixedTimestep" }
└── Assets/
    ├── Scenes/Main.mnvt
    └── Textures/player.png
```

- Asset paths are relative to `Assets/`. Absolute paths, and paths that escape `Assets/` (for example `../`), are rejected. This is a trust boundary for project files.
- Every asset has a `.meta` file with its GUID; see [assets](assets.md). A standalone game opens the asset database read-only.
- The project file is strict: unknown keys are errors. It is validated on load: name, timestep, window size, and that the startup scene exists.
- **MSBuild note:** MSBuild treats any `*.*proj` file as a project. If a `.mnvtproj` sits next to the game's `.csproj`, name the `.csproj` explicitly in `dotnet build`. The solution build is unaffected.

## Standalone runtime

A game host is a few lines (see `src/Tooling/MNVTEngine.Player/Program.cs`):

```csharp
return GameApplication.Run(args,
    (window, logger) => GLGraphicsDevice.Create(window.GraphicsContext, window.FramebufferWidth, window.FramebufferHeight, logger),
    loggerFactory,
    (project, registry) => LoadScripts(project, registry)); // false aborts startup (exit code 3)
```

- **Project discovery:** the host uses the project path argument, or else the single `.mnvtproj` next to the executable.
- **Options:** `--frames N` exits after N frames, `--screenshot file.png` saves the last frame, and `--debug-gl` enables GL driver messages.
- **Exit codes:** 0 ok, 1 GPU resource leak, 2 project or startup scene unreadable.
- **Scene switching:** `SceneManager.LoadScene(path)` switches at the end of the frame. A broken target scene is logged and the current scene keeps running.
- **Missing textures:** each missing or corrupt texture is logged once and drawn as a magenta checkerboard.
- **Rendering:** `SceneRenderer` draws every active `SpriteRenderer` through `Scene.MainCamera` (the first active camera). Sprites are sorted by `SortingOrder`, higher on top. Off-screen sprites are culled with a conservative bounding circle.
