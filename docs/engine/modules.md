# Engine modules

The engine is a set of **modules**. A project has a module or does not. Which ones it has follows from its [profile](project-profiles.md) (2D or 3D), each module's activation, and the project's own switches. The module catalog, the resolver and the lifecycle are explicit: modules are registered in code, resolved deterministically and started in dependency order. Nothing is discovered by scanning assemblies.

> Status: catalog, resolver, lifecycle and `mnvt modules` are in place (phase 2). The game host registers and starts only the project's modules, and validation rejects content of modules a project does not have. Exports still contain every module's assemblies; stripping them is phase 3. See [limitations](#limitations).

## The catalog

`MNVTEngine.Modules` has no engine dependencies, so tooling, the runtime and the editor read the same definitions. A `ModuleDefinition` declares:

| Field | Meaning |
|---|---|
| `Id` | Stable id: lowercase, dot-separated (`physics.2d`) |
| `Activation` | `Always` (every project; cannot be disabled), `Default` (on unless the project disables it), `OptIn` (off unless enabled) |
| `Profiles` | Profiles it works in; a 2D-only module never enters a 3D project |
| `Requires` | Modules it cannot work without. Enabling it enables them, transitively |
| `Optional` | Modules it integrates with when present. They are never enabled because of it; when both are on, they start first |
| `RuntimeAssemblies`, `NativeLibraries`, `BuildAssets` | What it ships in a game |
| `EditorAssemblies` | Editor assemblies that serve only this module |
| `Components` | Serialized ids of the components it provides |

`ModuleCatalog.Create` validates a set of definitions as a whole and creates nothing if any is invalid (`ModuleCatalogException` lists every problem):

- Ids are unique (`MNVB601`) and well formed. Profiles are listed, once each. An `Always` module supports every profile. No module depends on itself or lists a dependency twice (`MNVB602`).
- Every assembly, native library, build asset and component has one owner (`MNVB602`).
- Every dependency exists (`MNVB603`), and there are no cycles, through optional dependencies either (`MNVB604`).
- A required dependency exists in every profile of the module that requires it (`MNVB609`). Optional integrations may be profile-specific.

### Built-in modules

| Module | Profiles | Activation | Requires | Optional | Ships |
|---|---|---|---|---|---|
| `core` | 2D, 3D | always | | | Core, Mathematics, ECS, Serialization, Modules, Scenes, Input, Diagnostics, Newtonsoft.Json, Silk.NET.Core/Maths |
| `platform` | 2D, 3D | always | core | | Platform, Silk.NET GLFW/windowing/input; `glfw3.dll` |
| `graphics` | 2D, 3D | always | core | | Graphics, Graphics.Vulkan, Silk.NET Vulkan, Vortice VMA, Stb image/font; `vma.dll`; the sprite shader. Not the shader compiler: games run on precompiled SPIR-V |
| `assets` | 2D, 3D | always | core, graphics | | Assets (database, archives, core importers) |
| `runtime` | 2D, 3D | always | core, platform, graphics, assets | | Runtime, GameAssemblies, CrashReporting, logging |
| `audio` | 2D, 3D | default | core | | Audio, Audio.OpenAL, NVorbis; `soft_oal.dll` |
| `scripting` | 2D, 3D | default | core | | Scripting, Missions, Bindings |
| `ui` | 2D, 3D | default | core, graphics, runtime | | UI (screen-space, the same in both profiles) |
| `devtools` | 2D, 3D | opt-in | runtime | | DebugUI, ImGui.NET; `cimgui.dll` |
| `world.2d` | 2D | default | core, graphics, runtime | | World2D (sprites, sprite animation, tilemaps, 2D lights), World2D.Rendering (the sprite pass, 2D lighting) |
| `world.3d` | 3D | default | core, graphics, assets, runtime | | World3D (meshes, models, model animation, 3D lights, environment), World3D.Rendering (3D renderer, meshes, skinning, lighting and shadows, the mesh, shadow and sky shaders, model artifacts and cache, the 3D pass) |
| `physics.2d` | 2D | default | world.2d | | Physics2D, Box2D.NET |
| `physics.3d` | 3D | default | world.3d | | Physics3D, BepuPhysics, BepuUtilities |
| `terrain` | 3D | default | world.3d, physics.3d | | Terrain, Terrain.Components, Clipper2; the terrain shader |

Hosts are not modules. The game executable (GameHost) and the shared editor (Editor, Editor.Services) host every module. Profile-specific editor assemblies will belong to their module.

## Project switches

```json
{ "profile": "3d", "modules": { "terrain": false, "devtools": true } }
```

`modules` switches modules on or off on top of the profile's defaults; leave it out to get the defaults. `ModuleResolver.Resolve` (or `ProjectModules.Resolve` for a `GameProject`) returns the result:

- **Enabled modules in startup order.** Each module comes after everything it depends on, and ties are broken by id, so the same input always gives the same order. Shutdown is the reverse. Each enabled module carries its reason: `always`, `default for 2D`, `enabled in the project`, `required by x`.
- **Required dependencies are added automatically**, with an info diagnostic (`MNVB610`).
- **Errors are never resolved by guessing:**

| Code | Problem |
|---|---|
| `MNVB605` | A module of the other profile is enabled, or required (`"physics.3d": true` in a 2D project) |
| `MNVB606` | The project names a module that does not exist |
| `MNVB607` | An `Always` module is disabled, or a module an enabled module requires is disabled. The fix is to disable the dependent too |
| `MNVB608` | A scene or prefab uses a component of a module the project does not have (a `Tilemap` in a 3D project, an `AudioSource` with `audio` disabled) |

- **Excluded modules**, each with its reason: `not available in 2D`, `disabled in the project`, `opt-in, not enabled`.

`mnvt build` and `mnvt validate` stop on any of these errors. The game host does not start (exit code 2). This includes `MNVB608` when a project runs from its sources (`mnvt run`, the Player): left to the scene loader, such a component would load as a `MissingComponent` and silently do nothing. Exports were checked when they were built.

## Lifecycle

`IEngineModule` (in Scenes, next to `ComponentRegistry`) is a module's runtime side:

```csharp
public interface IEngineModule
{
    string Id { get; }                              // the catalog id
    void Register(ComponentRegistry components);    // add the module's components, nothing else
    void Initialize() { }                           // process-wide services, if any
    void Shutdown() { }                             // stop what Initialize started
}
```

`ModuleHost` runs a resolved set, given in startup order:

- `Start(registry)` registers every module, then initializes every module, both in startup order. It runs once; a second call throws.
- If a module fails to register or initialize, the modules initialized before it are shut down in reverse. Then `ModuleStartupException` is thrown, naming the module and the phase (`register` / `initialize`). Shutdown failures during that cleanup are listed in `CleanupFailures`.
- `Shutdown()` stops the initialized modules in reverse order, once; later calls do nothing. Every module is asked to stop even if another fails, and failures are reported together (`ModuleShutdownException`, one `ModuleFailureException` per module).
- **Ownership.** The host owns the modules it is given. Modules keep no static state. The registry passed to `Start` stays the caller's; discard it after a failed start. Most modules register components only: their systems (physics worlds, audio, UI) attach to each scene on first use, so `Initialize` and `Shutdown` default to doing nothing.

Implementations are created by id in `EngineModules.Create` (Runtime), a plain switch: `audio`, `scripting`, `ui`, `physics.2d`, `physics.3d`, `terrain`. `GameApplication` resolves the project's modules, starts a `ModuleHost` with the enabled ones, runs the game, and shuts the modules down. Scenes load against a registry with only those modules' components.

## Integration points

Runtime references no optional module. Modules plug into the host through explicit interfaces that the host checks on the module instances it was given (no scanning, no service locator):

| Interface | Assembly | Used for | Implemented by |
|---|---|---|---|
| `ISceneIntegration` | Scenes | Prepare every activated scene (attach a scene system, set a scene service) | audio, world.3d |
| `IDiagnosticsIntegration` | Scenes | Metric providers and console commands; without the module its metrics read as unavailable | audio, ui, scripting, physics.2d |
| `IResourceIntegration` | Runtime | Built-in shaders and caches (`IResourceExtension`) in the `ResourceCache` | world.3d, terrain |
| `IRenderIntegration` | Runtime | A scene pass (`IScenePass`) per renderer: 3D pass (100), sprite pass (200) | world.3d, world.2d |
| `IMeshItemIntegration` | World3D.Rendering | Meshes drawn inside the 3D pass, with its lights and shadows | terrain |
| `IScreenIntegration` | Runtime | A screen-space layer over the scene (input before gameplay, drawing after the scene) | ui |

Scene host services that modules define are typed, per scene, and set by the host or a module: `Scene.AssetReader` (asset bytes), `Scene.GetService<T>()` / `SetService<T>()` with module extension members such as world.3d's `scene.ModelAnimations` and `scene.MeshData`, and the audio module's `ISceneAudio`, which mission scripts' `audio.*` natives use (a script error without audio). The splash and loading screens are drawn by Runtime with the sprite batch and text, without the UI module. Importers of module asset types (`.mnvts`, `.mnvtterrain`, models) are build tooling (`EngineImporters` in MNVTEngine.Build); a game reads imported content and never imports.

## `mnvt modules`

```text
mnvt modules list [project]     the catalog; with a project, each module's status in it
mnvt modules validate <project> resolution problems plus content of missing modules (exit 1 on errors)
mnvt modules graph <project>    startup order, reasons, dependencies, dependency tree, excluded modules
```

```text
> mnvt modules graph samples/Platformer
Platformer (2D): 10 modules, in startup order (shutdown is the reverse)

  #   Module      Why             Depends on
  1   core        always          -
  2   audio       default for 2D  core
  3   graphics    always          core
  4   platform    always          core
  5   scripting   default for 2D  audio, core
  6   assets      always          core, graphics, scripting (optional)
  7   runtime     always          assets, core, graphics, platform
  8   ui          default for 2D  core, graphics
  9   world.2d    default for 2D  core, graphics
  10  physics.2d  default for 2D  world.2d
...
Not in this project
  devtools    opt-in, not enabled
  physics.3d  not available in 2D
  terrain     not available in 2D
  world.3d    not available in 2D
```

## Architecture tests

`ModuleBoundaryTests` check the catalog against the real `.csproj` graph:

- Every engine project belongs to exactly one module, except GameHost (the host) and Assets.Models (build tooling).
- An assembly references only assemblies of its own module and of the modules it requires, transitively.
- Assemblies of 2D-only and 3D-only modules never reach each other, and no shared module requires a profile module.

References that still cross module boundaries are listed as `KnownCouplings`. The list may only shrink: a new crossing fails, and an entry that was fixed but not removed fails too.

## Limitations

- **Exports still ship every module.** GameHost references every module's assemblies until the build generates its bootstrap and references from the resolved modules (phase B3).
- **The in-game debug UI (devtools) reads Physics2D, Audio, mission and UI state directly**, so a devtools build ships those assemblies. They are the remaining known couplings.
- **The editor registers the components of the project's profile**, and draws with that profile's passes; its 2D and 3D tools are not yet in separate assemblies.
- **`devtools` is still added by build configuration** (`MnvtDevTools`), not by the project's switches.
