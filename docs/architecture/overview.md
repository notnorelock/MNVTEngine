# MNVTEngine Architecture Overview

Status: **Milestone 8 (2D beta)** complete. This document is the source of truth for module boundaries.
`tests/MNVTEngine.Architecture.Tests` enforces the rules below by reading the `.csproj` graph.

## Toolchain

| Item | Choice | Notes |
|---|---|---|
| SDK | .NET 11 (`11.0.100-rc.3`, pinned in `global.json`, rolls forward to GA) | `net11.0` for every project |
| Packages | Central package management (`Directory.Packages.props`) | |
| Tests | xUnit v3 on Microsoft.Testing.Platform | Opted in via `global.json` `test.runner`; run `dotnet test` |
| Logging | `Microsoft.Extensions.Logging.Abstractions` | Inbox in the .NET 11 shared framework: no package reference needed |
| Serialization | Newtonsoft.Json 13.0.4 | |
| Build quality | Nullable, `TreatWarningsAsErrors`, `AnalysisMode=Recommended`, code style enforced in build | |

## Module map

Projects that exist today are **bold**. The rest are planned and only get a project when their milestone starts.

```text
                         ┌────────────────────┐
                         │   Editor (ImGui)   │  M3+
                         └─────────┬──────────┘
                                   │ may reference anything below
             ┌─────────────────────┼─────────────────────┐
             ▼                     ▼                     ▼
      Tooling (CLI, Build,   Runtime (game host,    Graphics.Vulkan / Windowing /
      Packaging, AssetComp.)  scene loop) M1-M2     Input / Audio backends (Silk.NET)
             │                     │                     │
             └──────────┬──────────┴──────────┬──────────┘
                        ▼                     ▼
          Scene / ECS / Assets / Physics2D   Graphics (backend-agnostic API)
          Scripting.* (Language, Compiler, VM, Missions, Bindings)
                        │
                        ▼
           **Serialization** (Newtonsoft.Json, converters, registries)
                        │
                        ▼
              **Core** (timing, logging)     **Mathematics** (no dependencies)
```

### Planned project boundaries

Only boundaries with real ownership or dependency value are planned. Sub-areas live as folders.

| Project | Milestone | Owns | May depend on |
|---|---|---|---|
| **MNVTEngine.Core** | M0 | Game clock, fixed timestep, logging sinks, frame profiler (M8, see [profiling](../engine/profiling.md)), low-level utilities | nothing |
| **MNVTEngine.Mathematics** | M0 | Vectors, quaternion, matrix, color, shapes | nothing |
| **MNVTEngine.Modules** | Profiles | Project profiles (2D / 3D), their inference for older projects, the engine module catalog and the module resolver. See [project profiles](../engine/project-profiles.md) and [modules](../engine/modules.md) | nothing |
| **MNVTEngine.Serialization** | M0 | JSON settings, math converters, later: type registry, schema versions, migrations | Mathematics |
| **MNVTEngine.Graphics** | M1 | Renderer2D, SpriteBatch (per-sprite materials), Camera2D, materials, resources, image decoding (no backend types). Material assets and 2D lighting: see [materials and lighting](../graphics/materials-and-lighting.md) | Core, Mathematics, StbImageSharp |
| **MNVTEngine.Graphics.Vulkan** | M9 (replaced Graphics.OpenGL) | Vulkan 1.4 implementation of Graphics: device selection, swapchain, frames in flight, VMA memory, shaderc + SPIR-V reflection, pipelines and caches, GPU timing. See [Vulkan migration](../graphics/vulkan-migration.md) | Graphics, Silk.NET.Vulkan, Silk.NET.Shaderc, Vortice.VulkanMemoryAllocator |
| **MNVTEngine.Platform** | M1 | Window and its Vulkan surface source, input state, frame loop (Silk.NET GLFW) | Core, Mathematics |
| **MNVTEngine.ECS** | M2 | Entity storage, component pools, queries, systems | Core |
| **MNVTEngine.Input** | M2 | Backend-free input state, keys, axes | Mathematics |
| **MNVTEngine.Scenes** | M2 | GameObject/Component API, Transform hierarchy, lifecycle, Time/Input facades, scene serialization, sprite animation and tilemaps (M8, see [animation](../engine/animation.md), [tilemaps](../engine/tilemaps.md)) | ECS, Input, Serialization |
| **MNVTEngine.Runtime** | M2 | Project file, `EngineComponents` registry, GUID-based resource cache, prefab/text asset provider, scene renderer, standalone game loop; backend injected by the host | Assets, Graphics, Platform, Scenes, Scripting.Bindings |
| **MNVTEngine.Assets** | M4 | GUID database, sidecar metas, importers (scripts are compiled on import for diagnostics), import cache, dependencies, safe asset paths, encrypted content archive (M7) | Serialization, Graphics (image decoding), Scripting.Missions (API declarations) |
| **MNVTEngine.Assets.Models** | 3D P5 | Model importer: FBX via Assimp (Silk.NET.Assimp, BSD-3), glTF/GLB via SharpGLTF (MIT), into model artifacts. Edit-time only: editor, Player and build; never in exported games (enforced). See [models](../engine/models.md) | Assets, Silk.NET.Assimp, SharpGLTF.Core |
| **MNVTEngine.Scripting** | M6 | `.mnvts` lexer, parser, AST, semantic checks, compiler, bytecode format and verifier, coroutine VM, debugger hooks | nothing (enforced) |
| **MNVTEngine.Scripting.Missions** | M6 | Standard API declarations, mission/objective lifecycle, save data with schema version | Scripting, Serialization; never Scenes/Graphics (enforced) |
| **MNVTEngine.Scripting.Bindings** | M6 | `MissionRunner` component, scene API implementation (player, entity, marker, scene, dialogue, input) | Scenes, Scripting.Missions |
| **MNVTEngine.Physics3D** | 3D P8 | Rigidbody, Box/Sphere/Capsule colliders, collision and trigger callbacks, ray casts, per-scene BepuPhysics v2 simulation (Apache-2.0, pure C#). See [physics 3D](../engine/physics3d.md) | Scenes, BepuPhysics; never Graphics, Platform, Assets or Physics2D (enforced) |
| **MNVTEngine.Physics2D** | M8 | Rigidbody2D, Box/Circle/Tilemap colliders, collision and trigger callbacks, ray casts, per-scene Box2D world (Box2D.NET, MIT). See [physics](../engine/physics2d.md) | Scenes, Box2D.NET; never Graphics, Platform or Assets (enforced) |
| **MNVTEngine.Terrain** | Terrain | TerrainHeaven port: roads, intersections, rivers, terrain cuts, stairs, fences, adaptive terrain mesher, foliage and prop stores, project JSON. Pure geometry. See [terrain](../engine/terrain.md) | Mathematics, Clipper2 (BSL), Newtonsoft.Json |
| **MNVTEngine.Terrain.Components** | Terrain | `WorldTerrain`: a `.mnvtterrain` world on a GameObject, its collision triangles (`IMeshDataSource`). Drawn by Runtime (splat shader, foliage, props). See [terrain](../engine/terrain.md) | Scenes, Physics3D, Terrain |
| **MNVTEngine.Audio** | M8 | Audio device abstraction, WAV/Ogg decoding, AudioSource/AudioListener, per-scene AudioSystem, silent device. See [audio](../engine/audio.md) | Scenes, NVorbis; never OpenAL (enforced) |
| **MNVTEngine.Audio.OpenAL** | M8 | OpenAL Soft backend, chosen by hosts | Audio, Silk.NET.OpenAL |
| **MNVTEngine.GameAssemblies** | M5 | Collectible load contexts for game code, component discovery and registration, unload support (reflection cache release) | Scenes |
| **MNVTEngine.GameHost** | M7 | Executable of exported games: resolves `Dependencies/`, opens the encrypted content archive, loads game code from it; never compiles | Runtime, GameAssemblies, Graphics.Vulkan |
| **MNVTEngine.Build** (Tooling) | M5/M7 | C# script compilation, build profiles, content compilation, host publishing, package validation, templates. See [building](../build-system/building.md) | GameAssemblies, Runtime |
| **MNVTEngine.CLI** (Tooling, `mnvt`) | M7 | Commands `new`, `editor`, `run`, `build`, `validate`; argument parsing and reporting only | Build, Graphics.Vulkan |
| **MNVTEngine.Player** (Tooling) | M5 | Development runner: compile scripts if stale, load, run any project | Build, Graphics.Vulkan |
| **MNVTEngine.Editor.Services** | M3 | Session, selection, Play Mode, inspection, picking, gizmo math (no ImGui) | Runtime |
| **MNVTEngine.Editor** | M3 | ImGui backend, docked workspace, panels, dialogs (exe) | Editor.Services, Graphics.Vulkan, ImGui.NET |

Deviations from the original proposal, and why:

- **Windowing lives in `Platform`; device-level input state is its own small `Input` project.** The GLFW bridge stays in Platform, while `InputState`/`Key` are backend-free so the gameplay `Input` facade in Scenes needs no window dependency (moved in M2).
- **`Scene` project is named `MNVTEngine.Scenes`.** A project named `MNVTEngine.Scene` makes tooling generate the namespace `MNVTEngine.Scene`, which collides with the `MNVTEngine.Scene` type for every consumer.
- **Scripting.Language/Compiler/VM start as one project** with folders. The editor reuses the compiler through its public API; the VM's isolation from graphics is enforced by the architecture tests, not by assembly count. Split later if build times or ownership require it.
- **Editor is two projects, not nine.** `Editor.Services` (UI-free, tested) and the `Editor` exe with panels as folders; the proposed per-panel projects would all share one ImGui context and the same services.
- **Packaging, asset compilation and templates live in `MNVTEngine.Build`, not in separate `Packaging`/`AssetCompiler`/`ProjectTemplates` projects.** They are separate classes (`GamePackager`, `ContentCompiler`, `BuildValidator`, `ProjectTemplates`, with `GameBuilder` orchestrating), but they share the same dependencies and are only used by tooling.
- **Exported games use `Dependencies/Managed`, `Dependencies/Native` and `Dependencies/Runtime`, plus content archives in `<Game>_Data/` (globals, engine-generated default resources, one per level, shared assets, resources).** The runtime folder keeps the layout hostfxr requires (`host/fxr`, `shared/Microsoft.NETCore.App`), so it cannot be merged into Managed/Native. See [building](../build-system/building.md#output-layout).
- **No separate `Resources` or `Diagnostics` projects yet.** Resource lifetime belongs with Graphics/Assets; diagnostics types live in Core until there is more than a handful.
- The architecture tests already contain rules for the original names (`Windowing`, `Input`, `Scripting.VM`, ...) so the boundary is protected whichever split is chosen.

## Dependency rules (enforced)

1. No project reference cycles.
2. Core and Mathematics reference no projects and no packages.
3. Engine projects never reference Editor or Tooling projects (transitively).
4. Tooling never references Editor.
5. Only Editor projects reference ImGui packages (transitively).
6. Only platform backends reference Silk.NET (or Vortice) packages directly.
7. Graphics never references Graphics.Vulkan; Runtime never references Graphics.Vulkan directly (backend chosen at the host).
   Graphics and Platform never reference each other, and Graphics.Vulkan never references Platform; the host wires them.
   Scenes never references Graphics or Platform; ECS never references Scenes; Input depends only on Mathematics.
8. Scripting projects, Physics2D and Physics3D never reference Graphics, Graphics.Vulkan or ImGui.

## Key decisions

| Decision | Choice | Reason |
|---|---|---|
| Coordinate system | Right-handed, +X right, +Y up, +Z towards viewer | 2D world is Y-up; the Vulkan device flips its viewport so clip space stays y-up |
| Matrix convention | Column vectors (`v' = M * v`), column-major storage | Same memory layout as a GLSL `mat4` in a std140 uniform block: copied as is |
| Angles | Degrees in gameplay APIs, radians in `Mathf` trig | Designer-friendly, matches common engine conventions |
| Math struct equality | Exact `==`; `Approximately` for tolerance | Keeps `Equals`/`GetHashCode` consistent |
| JSON polymorphism | Never `TypeNameHandling`; explicit registries | Project files are untrusted input |
| Logging | MEL abstractions + engine `InMemoryLoggerProvider` | Standard structured logging; ring buffer feeds the editor console |
| Time | `GameClock` takes measured elapsed time; `FixedTimestep` with backlog cap | Deterministic and testable; no hidden system-timer reads |
| GPU API | Vulkan 1.4 core (dynamic rendering, synchronization2, push descriptors), single graphics thread, 2 frames in flight. Replaced OpenGL 4.5 | Explicit memory, synchronization and validation; modern drivers and tools. See [rendering](../graphics/rendering.md) and [Vulkan migration](../graphics/vulkan-migration.md) |
| Resource lifetime | Explicit `Dispose`, tracker per device, no finalizers | GPU objects are destroyed on the graphics thread after the GPU is done with them (deferred destruction); leaks are reported, not hidden |
| Input | Engine-owned `Key` enum mapped by name from Silk.NET | Gameplay code never sees backend types |
| Hybrid ECS | GameObjects are entities; object components live in world pools by concrete type; one component per type per object | Systems query scripts and data alike; see [scenes](../engine/scenes-and-runtime.md) |
| Gameplay facades | `Time`/`Input`/`SceneManager` statics backed by a thread-static current scene | Unity-like ergonomics without cross-scene global state |
| Script exceptions | Logged with context, frame continues | One broken script does not take down the game |
| Editor rendering | ImGui drawn through `IGraphicsDevice` (sprite shader + scissor + dynamic indices) | Editor and debug UI moved to Vulkan without a dedicated ImGui renderer; no second backend code path |
| Play Mode | Copy made by serialize/deserialize; edit scene never runs | Identical to what a build loads; Stop needs no restore logic. See [editor](../editor/editor.md) |
| Asset identity | Sidecar `.meta` with GUID; references are `"asset:<guid>"` | Renames/moves never break references; metas are diffable and merge-friendly. See [assets](../engine/assets.md) |
| Scenes vs assets | Scenes depend only on `IAssetLookup` (Serialization); `Assets` does not know about scenes | Asset database stays usable by tooling without the gameplay model |
| Prefabs | Unlinked copies with id remapping | Correct and simple; nested/override prefabs deferred |
| C# game code | Separate class library compiled with `dotnet build`, loaded from bytes into a collectible ALC; host assemblies always shared | Real C# tooling and debugging; no file locks; game code cannot shadow the engine. See [C# scripting](../engine/csharp-scripting.md) |
| Hot reload | Serialize scene, swap types, deserialize, unload, verify collection | Same path as save/load, so anything that persists survives reload |
| Game export | Publish the engine's `GameHost` renamed to the game, with a private runtime found app-relative; dependencies resolved by the host from `Dependencies/`; content, bytecode and C# game code in one AES-GCM archive with an asset index; staged, validated, then swapped in | One tested host for every game; no installed .NET; asset GUIDs unchanged; tampering detected; a failed build never destroys the last good one |

## Technical risks

| Risk | Impact | Mitigation |
|---|---|---|
| .NET 11 is a release candidate | API or tooling changes before GA | `global.json` rolls forward to GA; CI should pin once GA ships |
| ImGui.NET (1.91.6.1) is updated infrequently | Docking API or native binary gaps | Verified at M3: docking works; DockBuilder (not wrapped) is bound via P/Invoke to cimgui. Hexa.NET.ImGui remains the fallback; all ImGui use is in the Editor exe |
| Silk.NET 2.x vs 3.0 transition | API churn | Pinned 2.23.0; Silk types do not appear in Graphics or gameplay-facing input APIs |
| Newtonsoft.Json static reflection caches | Confirmed at M5 to pin unloaded game code | Per-operation contract resolvers plus `ReflectionCacheCleaner` clearing Newtonsoft's internal stores on unload; tests verify collection and fail if Newtonsoft internals change |
| Native deps in self-contained publish | GLFW/OpenAL natives not always copied | Done in M7: the validator checks every deps.json asset plus required natives (`glfw3.dll`, `soft_oal.dll`, `shaderc_shared.dll`, `vma.dll`), and a build test launches the exported game |
| Physics scope | Custom solver is a large effort | Decided at M8: Box2D.NET (MIT, pure C# Box2D v3) behind engine components; its process-wide world table is guarded and worlds are freed on scene unload |
| GPU integration tests | CI agents may lack a Vulkan 1.4 GPU or the validation layer | Tests use a headless device and skip without a GPU; selection, reflection and linking are unit-tested without one. Validation runs whenever the layer is installed |
| Vulkan validation layer availability | Not installed with drivers; only with the LunarG SDK | Optional at run time; development machines install the SDK (1.4.363.1 at migration time) |
