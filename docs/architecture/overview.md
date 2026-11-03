# MNVTEngine Architecture Overview

Status: **Milestone 7 (build and Windows x64 packaging)** complete. This document is the source of truth for module boundaries.
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
      Tooling (CLI, Build,   Runtime (game host,    Graphics.OpenGL / Windowing /
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
| **MNVTEngine.Core** | M0 | Game clock, fixed timestep, logging sinks, low-level utilities | nothing |
| **MNVTEngine.Mathematics** | M0 | Vectors, quaternion, matrix, color, shapes | nothing |
| **MNVTEngine.Serialization** | M0 | JSON settings, math converters, later: type registry, schema versions, migrations | Mathematics |
| **MNVTEngine.Graphics** | M1 | Renderer2D, SpriteBatch, Camera2D, materials, resources, image decoding (no GL types) | Core, Mathematics, StbImageSharp |
| **MNVTEngine.Graphics.OpenGL** | M1 | GL 4.5 implementation of Graphics | Graphics, Silk.NET.OpenGL |
| **MNVTEngine.Platform** | M1 | Window, GL context, input state, frame loop (Silk.NET GLFW) | Core, Mathematics |
| **MNVTEngine.ECS** | M2 | Entity storage, component pools, queries, systems | Core |
| **MNVTEngine.Input** | M2 | Backend-free input state, keys, axes | Mathematics |
| **MNVTEngine.Scenes** | M2 | GameObject/Component API, Transform hierarchy, lifecycle, Time/Input facades, scene serialization | ECS, Input, Serialization |
| **MNVTEngine.Runtime** | M2 | Project file, GUID-based resource cache, prefab/text asset provider, scene renderer, standalone game loop; backend injected by the host | Assets, Graphics, Platform, Scenes, Scripting.Bindings |
| **MNVTEngine.Assets** | M4 | GUID database, sidecar metas, importers (scripts are compiled on import for diagnostics), import cache, dependencies, safe asset paths | Serialization, Graphics (image decoding), Scripting.Missions (API declarations) |
| **MNVTEngine.Scripting** | M6 | `.mnvts` lexer, parser, AST, semantic checks, compiler, bytecode format and verifier, coroutine VM, debugger hooks | nothing (enforced) |
| **MNVTEngine.Scripting.Missions** | M6 | Standard API declarations, mission/objective lifecycle, save data with schema version | Scripting, Serialization; never Scenes/Graphics (enforced) |
| **MNVTEngine.Scripting.Bindings** | M6 | `MissionRunner` component, scene API implementation (player, entity, marker, scene, dialogue, input) | Scenes, Scripting.Missions |
| **MNVTEngine.GameAssemblies** | M5 | Collectible load contexts for game code, component discovery and registration, unload support (reflection cache release) | Scenes |
| **MNVTEngine.GameHost** | M7 | Executable of exported games: loads the package manifest, game code and content; never compiles | Runtime, GameAssemblies, Graphics.OpenGL |
| **MNVTEngine.Build** (Tooling) | M5/M7 | C# script compilation, build profiles, content compilation, host publishing, package validation, templates. See [building](../build-system/building.md) | GameAssemblies, Runtime |
| **MNVTEngine.CLI** (Tooling, `mnvt`) | M7 | Commands `new`, `editor`, `run`, `build`, `validate`; argument parsing and reporting only | Build, Graphics.OpenGL |
| **MNVTEngine.Player** (Tooling) | M5 | Development runner: compile scripts if stale, load, run any project | Build, Graphics.OpenGL |
| **MNVTEngine.Editor.Services** | M3 | Session, selection, Play Mode, inspection, picking, gizmo math (no ImGui) | Runtime |
| **MNVTEngine.Editor** | M3 | ImGui backend, docked workspace, panels, dialogs (exe) | Editor.Services, Graphics.OpenGL, ImGui.NET |

Deviations from the original proposal, and why:

- **Windowing lives in `Platform`; device-level input state is its own small `Input` project.** The GLFW bridge stays in Platform, while `InputState`/`Key` are backend-free so the gameplay `Input` facade in Scenes needs no window dependency (moved in M2).
- **`Scene` project is named `MNVTEngine.Scenes`.** A project named `MNVTEngine.Scene` makes tooling generate the namespace `MNVTEngine.Scene`, which collides with the `MNVTEngine.Scene` type for every consumer.
- **Scripting.Language/Compiler/VM start as one project** with folders. The editor reuses the compiler through its public API; the VM's isolation from graphics is enforced by the architecture tests, not by assembly count. Split later if build times or ownership require it.
- **Editor is two projects, not nine.** `Editor.Services` (UI-free, tested) and the `Editor` exe with panels as folders; the proposed per-panel projects would all share one ImGui context and the same services.
- **Packaging, asset compilation and templates live in `MNVTEngine.Build`, not in separate `Packaging`/`AssetCompiler`/`ProjectTemplates` projects.** They are separate classes (`GamePackager`, `ContentCompiler`, `BuildValidator`, `ProjectTemplates`, with `GameBuilder` orchestrating), but they share the same dependencies and are only used by tooling.
- **Exported games are flat, not `Dependencies/Managed|Native`.** Self-contained .NET apps resolve assemblies and native libraries from the app folder via `deps.json`; see [building](../build-system/building.md#output-layout).
- **No separate `Resources` or `Diagnostics` projects yet.** Resource lifetime belongs with Graphics/Assets; diagnostics types live in Core until there is more than a handful.
- The architecture tests already contain rules for the original names (`Windowing`, `Input`, `Scripting.VM`, ...) so the boundary is protected whichever split is chosen.

## Dependency rules (enforced)

1. No project reference cycles.
2. Core and Mathematics reference no projects and no packages.
3. Engine projects never reference Editor or Tooling projects (transitively).
4. Tooling never references Editor.
5. Only Editor projects reference ImGui packages (transitively).
6. Only platform backends reference Silk.NET packages directly.
7. Graphics never references Graphics.OpenGL; Runtime never references Graphics.OpenGL directly (backend chosen at the host).
   Graphics and Platform never reference each other, and Graphics.OpenGL never references Platform; the host wires them.
   Scenes never references Graphics or Platform; ECS never references Scenes; Input depends only on Mathematics.
8. Scripting projects and Physics2D never reference Graphics, OpenGL or ImGui.

## Key decisions

| Decision | Choice | Reason |
|---|---|---|
| Coordinate system | Right-handed, +X right, +Y up, +Z towards viewer | Matches OpenGL; 2D world is Y-up |
| Matrix convention | Column vectors (`v' = M * v`), column-major storage | Direct `glUniformMatrix4fv(..., transpose: false)` |
| Angles | Degrees in gameplay APIs, radians in `Mathf` trig | Designer-friendly, matches common engine conventions |
| Math struct equality | Exact `==`; `Approximately` for tolerance | Keeps `Equals`/`GetHashCode` consistent |
| JSON polymorphism | Never `TypeNameHandling`; explicit registries | Project files are untrusted input |
| Logging | MEL abstractions + engine `InMemoryLoggerProvider` | Standard structured logging; ring buffer feeds the editor console |
| Time | `GameClock` takes measured elapsed time; `FixedTimestep` with backlog cap | Deterministic and testable; no hidden system-timer reads |
| GPU API | OpenGL 4.5 DSA only, single graphics thread | No bind-to-edit state bugs; simple ownership. See [rendering](../graphics/rendering.md) |
| Resource lifetime | Explicit `Dispose`, tracker per device, no finalizers | GL objects cannot be freed off-thread; leaks are reported, not hidden |
| Input | Engine-owned `Key` enum mapped by name from Silk.NET | Gameplay code never sees backend types |
| Hybrid ECS | GameObjects are entities; object components live in world pools by concrete type; one component per type per object | Systems query scripts and data alike; see [scenes](../engine/scenes-and-runtime.md) |
| Gameplay facades | `Time`/`Input`/`SceneManager` statics backed by a thread-static current scene | Unity-like ergonomics without cross-scene global state |
| Script exceptions | Logged with context, frame continues | One broken script does not take down the game |
| Editor rendering | ImGui drawn through `IGraphicsDevice` (sprite shader + scissor + dynamic indices) | Editor works on any future backend; no second GL code path |
| Play Mode | Copy made by serialize/deserialize; edit scene never runs | Identical to what a build loads; Stop needs no restore logic. See [editor](../editor/editor.md) |
| Asset identity | Sidecar `.meta` with GUID; references are `"asset:<guid>"` | Renames/moves never break references; metas are diffable and merge-friendly. See [assets](../engine/assets.md) |
| Scenes vs assets | Scenes depend only on `IAssetLookup` (Serialization); `Assets` does not know about scenes | Asset database stays usable by tooling without the gameplay model |
| Prefabs | Unlinked copies with id remapping | Correct and simple; nested/override prefabs deferred |
| C# game code | Separate class library compiled with `dotnet build`, loaded from bytes into a collectible ALC; host assemblies always shared | Real C# tooling and debugging; no file locks; game code cannot shadow the engine. See [C# scripting](../engine/csharp-scripting.md) |
| Hot reload | Serialize scene, swap types, deserialize, unload, verify collection | Same path as save/load, so anything that persists survives reload |
| Game export | Publish the engine's `GameHost` self-contained and renamed to the game; content as assets+metas plus bytecode; staged, validated, then swapped in | One tested host for every game; asset GUIDs unchanged; a failed build never destroys the last good one |

## Technical risks

| Risk | Impact | Mitigation |
|---|---|---|
| .NET 11 is a release candidate | API or tooling changes before GA | `global.json` rolls forward to GA; CI should pin once GA ships |
| ImGui.NET (1.91.6.1) is updated infrequently | Docking API or native binary gaps | Verified at M3: docking works; DockBuilder (not wrapped) is bound via P/Invoke to cimgui. Hexa.NET.ImGui remains the fallback; all ImGui use is in the Editor exe |
| Silk.NET 2.x vs 3.0 transition | API churn | Pinned 2.23.0; Silk types do not appear in Graphics or gameplay-facing input APIs |
| Newtonsoft.Json static reflection caches | Confirmed at M5 to pin unloaded game code | Per-operation contract resolvers plus `ReflectionCacheCleaner` clearing Newtonsoft's internal stores on unload; tests verify collection and fail if Newtonsoft internals change |
| Native deps in self-contained publish | GLFW/OpenAL natives not always copied | Done in M7: the validator checks every deps.json asset plus required natives (`glfw3.dll`), and a build test launches the exported game |
| Physics scope | Custom solver is a large effort | Decide at M8: simple custom impulse solver for AABB/circle vs. integrating a permissively licensed Box2D port |
| OpenGL integration tests | CI agents may lack a GL context | Separate test category requiring a GPU; unit tests stay GL-free |
