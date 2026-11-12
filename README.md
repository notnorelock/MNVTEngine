# MNVTEngine

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/mnvtengine-logo.svg">
  <img src="docs/images/mnvtengine-logo-dark.svg" alt="MNVTEngine" width="360">
</picture>

A modular 2D game engine in C# / .NET 11:

- **Core:** a Vulkan 1.4 renderer (Silk.NET, VMA, shaderc), an ImGui.NET editor and a hybrid ECS.
- **Gameplay:** Box2D physics, OpenAL audio, sprite animation, tilemaps, materials and 2D lighting.
- **Assets and scripting:** an asset pipeline with stable GUIDs, C# components with hot reload, and the `.mnvts` scripting language with its own bytecode VM, missions and debugger.
- **Export:** standalone Windows games.

**Status: Milestone 8 (2D beta).** The engine can be used to build actual 2D games. Start with [getting started](docs/getting-started.md) or play `samples/Platformer`.

## Requirements

- .NET SDK 11 (`global.json` pins `11.0.100-rc.3` and rolls forward)
- Windows x64 is the first target platform
- A GPU and driver with Vulkan 1.4. The [LunarG Vulkan SDK](https://vulkan.lunarg.com/) is optional; it adds the validation layer that development builds use

## Build and test

```bash
dotnet build MNVTEngine.sln
dotnet test --solution MNVTEngine.sln
```

## The mnvt command

```bash
scripts\mnvt.cmd new MyGame --profile 2d          # or --profile 3d, or --template basic-2d (see docs/engine/project-profiles.md)
scripts\mnvt.cmd run MyGame
scripts\mnvt.cmd build MyGame --configuration release --run
MyGame\Builds\release\MyGame.exe
```

`scripts\mnvt.cmd` runs `src/Tooling/MNVTEngine.CLI` from source. See [building and exporting games](docs/build-system/building.md).

## Run the samples

```bash
scripts\mnvt.cmd run samples/Platformer     # A/D or arrows run, Space jumps, Esc pauses: collect 5 coins, then reach the flag
scripts\mnvt.cmd run samples/SceneDemo      # WASD flies; Tab switches to the physics/lighting arena

dotnet run --project samples/HelloWorld
# Automated: render 120 frames, save a screenshot, exit
dotnet run --project samples/HelloWorld -- --frames 120 --screenshot shot.png --gpu-validation
```

SceneDemo: WASD/arrows move, Space spawns orbs, Tab switches scenes. Both samples export with `mnvt build`.
HelloWorld: WASD/arrows move, mouse wheel zooms, Q/E rotate the camera, Escape quits. Requires a Vulkan 1.4 GPU and driver.

## Run the editor

```bash
scripts\mnvt.cmd editor samples/Platformer
```

See [the editor guide](docs/editor/editor.md) for controls. Edit `samples/*/Scripts/*.cs` while the editor runs to see hot reload.

## Layout

| Path | Contents |
|---|---|
| `src/Engine` | Runtime engine modules (`Core`, `Mathematics`, `Serialization`, ...) and `GameHost`, the executable of exported games |
| `src/Editor` | Editor: UI-free services and the ImGui application |
| `src/Tooling` | `mnvt` CLI, build pipeline and packaging (`Build`), development runner (`Player`) |
| `templates` | Project templates for `mnvt new` |
| `scripts` | `mnvt.cmd` wrapper |
| `samples` | `Platformer` (complete level), `SceneDemo` (feature tour), `UIShowcase` (game UI), `Showcase3D` (3D meshes), `HelloWorld` (renderer only) |
| `tests` | xUnit v3 tests, including architecture dependency rules and GPU integration tests |
| `docs` | Architecture and subsystem documentation |

## Documentation

- [Getting started: create, edit, script and export a game](docs/getting-started.md)
- [Architecture overview, dependency rules, decisions, risks](docs/architecture/overview.md)
- [Mathematics conventions](docs/engine/mathematics.md)
- [JSON serialization](docs/serialization/json.md)
- [2D rendering, frame loop, resource ownership](docs/graphics/rendering.md)
- [Vulkan 1.4 backend: audit, decisions, synchronization model](docs/graphics/vulkan-migration.md)
- [3D engine and model pipeline: audit, library choices, plan](docs/graphics/3d-plan.md); [acceptance evidence](docs/graphics/3d-acceptance.md); [meshes, 3D cameras and 3D rendering](docs/graphics/meshes.md)
- [Scenes, ECS, lifecycle, scene format, standalone runtime](docs/engine/scenes-and-runtime.md)
- [Project profiles: 2D or 3D projects, legacy migration](docs/engine/project-profiles.md); [engine modules: catalog, resolution, lifecycle, `mnvt modules`](docs/engine/modules.md)
- [Editor: panels, Play Mode, ImGui integration](docs/editor/editor.md)
- [Assets: metas, GUIDs, importers, prefabs](docs/engine/assets.md); [models: FBX, glTF and GLB import](docs/engine/models.md)
- [C# scripting: game assemblies, hot reload, unloading](docs/engine/csharp-scripting.md)
- [.mnvts language reference: grammar, semantics, VM limits, errors](docs/scripting/language.md)
- [Missions: lifecycle, script API, persistence](docs/scripting/missions.md)
- [Building and exporting games: CLI, profiles, packaging, validation](docs/build-system/building.md)
- [Physics2D: rigidbodies, colliders, callbacks, ray casts](docs/engine/physics2d.md); [Physics 3D (BepuPhysics v2)](docs/engine/physics3d.md)
- [Audio: sources, listener, spatial audio, decoding](docs/engine/audio.md)
- [Sprite animation](docs/engine/animation.md) and [tilemaps](docs/engine/tilemaps.md)
- [Terrain: roads, rivers, terrain cuts, foliage, TerrainHeaven projects](docs/engine/terrain.md)
- [Materials, 3D lighting and shadows, 2D lighting](docs/graphics/materials-and-lighting.md)
- [Logging: named loggers, levels per namespace/class, call sites](docs/engine/logging.md)
- [Runtime diagnostics: in-game debug UI, HUD, profiler, console, budgets, debug drawing](docs/engine/diagnostics.md)
- [Crash reporting: crash handler process, dumps, reports, crash window](docs/engine/crash-reporting.md)
- [Game UI, text and fonts, window, splash and loading screens](docs/ui/ui-system.md)
- [Profiling](docs/engine/profiling.md)

## Roadmap

| Milestone | Scope | Status |
|---|---|---|
| 0 | Foundation: solution, math, serialization, logging, timing | done |
| 1 | Window, graphics device, shaders, textures, SpriteBatch, Camera2D, input (OpenGL 4.5 at the time; Vulkan 1.4 since M9) | done |
| 2 | Hybrid ECS, GameObject API, scenes, standalone runtime | done |
| 3 | ImGui editor, Play Mode | done |
| 4 | Asset pipeline with stable GUIDs | done |
| 5 | C# game scripting via AssemblyLoadContext | done |
| 6 | `.mnvts` language, compiler, VM, mission framework | done |
| 7 | Build and Windows x64 packaging, `mnvt` CLI | done |
| 8 | 2D beta: Physics2D, audio, sprite animation, tilemaps, materials, 2D lighting, undo/redo, profiling, script debugger, Platformer sample | done |
| 9 | Vulkan 1.4 backend replaces OpenGL; runtime diagnostics with GPU timing and memory. See [Vulkan migration](docs/graphics/vulkan-migration.md) | done |
