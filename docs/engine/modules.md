# Engine modules

The engine is a set of **modules**. A project has a module or does not. Which ones it has follows from its [profile](project-profiles.md) (2D or 3D), each module's activation, and the project's own switches. The module catalog, the resolver and the lifecycle are explicit: modules are registered in code, resolved deterministically and started in dependency order. Nothing is discovered by scanning assemblies.

> Status: catalog, resolver, lifecycle and `mnvt modules` are in place (phase 2). The game host registers and starts only the project's modules, and validation rejects content of modules a project does not have. Exports still contain every module's assemblies; stripping them is phase 3. See [limitations](#limitations).

## The catalog

`MNVTEngine.Modules` has no engine dependencies, so tooling, the runtime and the editor read the same definitions. A `ModuleDefinition` declares:

| Field | Meaning |
|---|---|
| `Id` | Stable id: lowercase, dot-separated (`physics.2d`) |
| `Activation` | `Always` (every project; cannot be disabled), `Auto` (in a build: included when the project uses it; in the editor and development runs: available), `OptIn` (off unless enabled) |
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
| `assets` | 2D, 3D | always | core, graphics | | Assets, IO (database, virtual file system, packages, core importers) |
| `runtime` | 2D, 3D | always | core, platform, graphics, assets | | Runtime, GameAssemblies, CrashReporting, logging |
| `audio` | 2D, 3D | auto | core | | Audio, Audio.OpenAL, NVorbis; `soft_oal.dll` |
| `scripting` | 2D, 3D | auto | core | | Scripting, Missions, Bindings |
| `ui` | 2D, 3D | auto | core, graphics, runtime | | UI (screen-space, the same in both profiles) |
| `devtools` | 2D, 3D | opt-in | runtime | | DebugUI, ImGui.NET; `cimgui.dll` |
| `world.2d` | 2D | auto | core, graphics, runtime | | World2D (sprites, sprite animation, tilemaps, 2D lights), World2D.Rendering (the sprite pass, 2D lighting) |
| `world.3d` | 3D | auto | core, graphics, assets, runtime | | World3D (meshes, models, model animation, 3D lights, environment), World3D.Rendering (3D renderer, meshes, skinning, lighting and shadows, the mesh, shadow and sky shaders, model artifacts and cache, the 3D pass) |
| `physics.2d` | 2D | auto | world.2d | | Physics2D, Box2D.NET |
| `physics.3d` | 3D | auto | world.3d | | Physics3D, BepuPhysics, BepuUtilities |
| `terrain` | 3D | auto | world.3d, physics.3d | | Terrain, Terrain.Components, Clipper2; the terrain shader |
| `particles` | 2D, 3D | auto | core | | Particles: particle systems (simulation, emission, shapes, lifetime modules, curves, gradients, `.mnvtparticle`); see [Particle systems](particles.md) |
| `particles.2d` | 2D | with `particles` | particles, world.2d | | Particles2D: draws particle systems in the sprite batch |
| `particles.3d` | 3D | with `particles` | particles, world.3d | | Particles3D: billboards and oriented quads, depth-tested and sorted; the particle shader |
| `browser.audio` | 2D, 3D | with `browser` | browser, audio | | Browser.Audio: page audio into the mixer (left out when the project has no audio) |
| `browser` | 2D, 3D | auto | core, runtime | | Browser, Browser.Protocol: Chromium pages in textures, C# API, JavaScript bridge; the build adds `MNVTBrowserHost.exe`, CefSharp and CEF (`Dependencies/Native/Chromium`, about 360 MB). See [Embedded browser](browser.md) |
| `browser.2d` | 2D | auto | browser, world.2d | | Browser.World2D: `BrowserSurface2D` |
| `browser.3d` | 3D | auto | browser, world.3d | | Browser.World3D: `BrowserSurface3D` |

Hosts are not modules. The game executable (GameHost) and the shared editor (Editor, Editor.Services) host every module. Profile-specific editor assemblies will belong to their module.

## Project switches

```json
{ "profile": "3d", "modules": { "terrain": false, "devtools": true } }
```

Each optional module has one of three states in `modules`:

| Value | Meaning |
|---|---|
| `true` | Always included, used or not (content the analysis cannot see, such as a module only reached through reflection) |
| `false` | Never included. Using it anyway is an error (`MNVB608`) |
| missing | Auto: a build includes it when the project uses it |

`ModuleResolver.Resolve(catalog, profile, switches, usage)` (or `ProjectModules.Resolve` for a `GameProject`) returns the result. `usage` is what a build's [usage analysis](#usage-analysis) found, as evidence per module. Without it (the editor, `mnvt run`, the Player) every Auto module of the profile is available, so editing never loses a feature.

- **Enabled modules in startup order.** Each module comes after everything it depends on, and ties are broken by id, so the same input always gives the same order. Shutdown is the reverse. Each enabled module carries its reason: `always`, `enabled in the project`, `used: <evidence>`, `required by x`, or `available in 2D` without a usage analysis.
- **Required dependencies are added automatically**, with an info diagnostic (`MNVB610`).
- **Errors are never resolved by guessing:**

| Code | Problem |
|---|---|
| `MNVB605` | A module of the other profile is enabled, or required (`"physics.3d": true` in a 2D project) |
| `MNVB606` | The project names a module that does not exist |
| `MNVB607` | An `Always` module is disabled, or a module an enabled module requires is disabled. The fix is to disable the dependent too |
| `MNVB608` | A scene or prefab uses a component of a module the project does not have (a `Tilemap` in a 3D project, an `AudioSource` with `audio` disabled) |

- **Excluded modules**, each with its reason: `not available in 2D`, `disabled in the project`, `opt-in, not enabled`, `not used: no components, assets or API of it in the project`.

## Usage analysis

A build includes only the modules and assets the game uses (`UsageAnalyzer`, run by `ContentCompiler` after every asset is imported).

**Assets.** The asset graph starts at:

- the startup scene;
- assets named by project settings (window icon, splash and loading screen images and fonts);
- assets marked `"alwaysInclude": true` in their `.meta` (see [Assets](assets.md#metadata));
- assets whose path (scenes and prefabs also without extension) or `asset:` reference appears as a string literal in the C# scripts (read from the compiled assembly's metadata, without loading it) or in a reachable `.mnvts` script.

From there it follows every reference the importers recorded: scene to prefab (nested), to material, to shader and texture, to model and its textures. Unreached assets are not shipped and are counted in the build log (`Modules: ...; N unused assets not shipped`). Unreached scenes, prefabs and audio clips are also reported as warning `MNVB223`, so content loaded by a computed path is never dropped silently: mark it `alwaysInclude`.

**Modules.** Evidence comes from the shipped content only:

| Evidence | Example |
|---|---|
| A module's component in a shipped scene or prefab | `Rigidbody2D in Scenes/Main.mnvt` enables `physics.2d` |
| A shipped asset of a type the module owns (`AssetTypes` in the catalog) | `Audio asset Sounds/jump.wav` enables `audio`; models and animator controllers `world.3d`; terrains `terrain`; UI layouts `ui`; sprite animations `world.2d`; `.mnvts` scripts `scripting` |
| `audio.*` calls in a shipped mission script | enables `audio` |
| A type the C# scripts reference from a module's assembly | `C# scripts use AudioSource (MNVTEngine.Audio)` |

Required modules follow as usual (`physics.2d` brings `world.2d`). A module that `Accompanies` another (`particles.2d` accompanies `particles`) is included whenever that module is, in the profiles it supports, unless the project switches it, or something it requires, off. It is never included on its own. Evidence for a module of the other profile, or for a module the project disables, is an error (`MNVB608`) naming the evidence.

The packaged project file lists every optional module of the profile as `true` or `false`, exactly as the build resolved them, so the game starts the modules it was built with and no others.

## Sizes

Release exports (KB), from before Phase B to now:

| Export | Before (every module) | Modules only (B3) | + framework pruning (B4) |
|---|---:|---:|---:|
| Platformer (2D) | 105,720 | 97,202 | 82,758 |
| UIShowcase (2D) | | 95,906 | 81,458 |
| Showcase3D (3D) | | 99,722 | 85,278 |
| TerrainDemo (3D) | | 96,943 | 82,499 |

The private runtime is now 70.8 MB of each export (103 files, 13.9 MB, pruned). Debug Platformer with dev tools: 85,502 KB.

## Generated bootstrap

The game executable is built with the game's modules only. `ModuleBootstrap` (MNVTEngine.Build) writes two files into `Library/Build/<profile>/Bootstrap`:

- `ModuleBootstrap.g.cs`: `HostModules.Create`, one explicit constructor call per resolved module in startup order. No reflection and no assembly scanning, so it is safe for trimming and AOT.
- `ModuleBootstrap.g.props`: references to those modules' projects, and it swaps the host's development list (`HostModules.cs`) for the generated file.

`dotnet publish` of GameHost imports the props through `-p:MnvtBootstrap`. Assemblies and natives of modules the game does not use are never built into it. The manifest records the game's modules (`package.json` `modules`). The validator then requires only those modules' native libraries (MNVB506), and fails on any file of a module the game does not have (MNVB517). Without a bootstrap (solution builds, tests) GameHost references every module; `ModuleBootstrapTests` keeps that list equal to the generator's.

| Export (release) | Modules beyond core, platform, graphics, assets and runtime | Engine and third-party assemblies | Natives |
|---|---|---|---|
| Platformer | audio, scripting, ui, world.2d, physics.2d | + Audio(.OpenAL), NVorbis, Scripting(.Missions, .Bindings), UI, World2D(.Rendering), Physics2D, Box2D.NET | glfw3, soft_oal, vma |
| UIShowcase | scripting, ui, world.2d | + Scripting(.Missions, .Bindings), UI, World2D(.Rendering) | glfw3, vma |
| Showcase3D | world.3d, physics.3d | + World3D(.Rendering), Physics3D, Bepu | glfw3, vma |
| TerrainDemo | world.3d, physics.3d, terrain | + World3D(.Rendering), Physics3D, Bepu, Terrain(.Components), Clipper2 | glfw3, vma |

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

These commands resolve without a usage analysis, so Auto modules show as `available in 2D`: what the editor and development runs get. The modules a build includes, with the evidence for each, are in the build log (`Modules: ...`).

```text
> mnvt modules graph samples/Platformer
Platformer (2D): 10 modules, in startup order (shutdown is the reverse)

  #   Module      Why              Depends on
  1   core        always           -
  2   audio       available in 2D  core
  3   graphics    always           core
  4   assets      always           core, graphics
  5   platform    always           core
  6   runtime     always           assets, core, graphics, platform
  7   scripting   available in 2D  core
  8   ui          available in 2D  core, graphics, runtime
  9   world.2d    available in 2D  core, graphics, runtime
  10  physics.2d  available in 2D  world.2d
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

- **Usage analysis is static.** A path built at run time (`"Levels/" + n`) is not seen; mark such assets `alwaysInclude` or name them literally. A module used only through reflection needs `"<module>": true`.
- **The in-game debug UI (devtools) reads Physics2D, Audio, mission and UI state directly**, so a devtools build ships those assemblies. They are the remaining known couplings.
- **The editor registers the components of the project's profile**, and draws with that profile's passes; its 2D and 3D tools are not yet in separate assemblies.
- **Vortice.Vulkan (1.6 MB) ships in every game** only because Vortice.VulkanMemoryAllocator depends on it; the engine calls ten VMA functions (`vmaCreateAllocator`, `vmaCreateBuffer`, `vmaCreateImage`, `vmaDestroy*`, `vmaFlushAllocation`, `vmaInvalidateAllocation`, `vmaGetAllocationInfo`, `vmaGetHeapBudgets`) and uses Silk.NET for everything else. Own `[LibraryImport]` declarations against `vma.dll` would remove it; not done yet.
- **The framework keeps what Newtonsoft.Json references** (System.Private.Xml, System.Data.Common, System.Linq.Expressions, and through Xml System.Net.Http). Removing those needs member-level trimming, which builds do not do.
