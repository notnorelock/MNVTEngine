# MNVTEngine

A modular 2D game engine in C# / .NET 11: OpenGL 4.5 renderer (Silk.NET), ImGui.NET editor, hybrid ECS,
asset pipeline, the `.mnvts` gameplay scripting language with its own bytecode VM, and standalone game export.

**Status: Milestone 7 (build and Windows x64 packaging).** The `mnvt` command creates, validates, runs and exports
projects. `mnvt build` produces a self-contained Windows x64 game folder that needs no .NET SDK, no installed runtime
and no editor. Gameplay and missions can be written in `.mnvts` (Milestone 6) and C# components (Milestone 5).

## Requirements

- .NET SDK 11 (`global.json` pins `11.0.100-rc.3` and rolls forward)
- Windows x64 is the first target platform

## Build and test

```bash
dotnet build MNVTEngine.sln
dotnet test --solution MNVTEngine.sln
```

## The mnvt command

```bash
scripts\mnvt.cmd new MyGame --template basic-2d   # empty-2d, basic-2d
scripts\mnvt.cmd run MyGame
scripts\mnvt.cmd build MyGame --configuration release --run
MyGame\Builds\release\MyGame.exe
```

`scripts\mnvt.cmd` runs `src/Tooling/MNVTEngine.CLI` from source. See [building and exporting games](docs/build-system/building.md).

## Run the samples

```bash
# A game project: scenes in Assets/Scenes, C# components in Scripts/ (compiled on first run)
dotnet run --project src/Tooling/MNVTEngine.Player -- samples/SceneDemo

dotnet run --project samples/HelloWorld
# Automated: render 120 frames, save a screenshot, exit
dotnet run --project samples/HelloWorld -- --frames 120 --screenshot shot.png --debug-gl
```

SceneDemo: WASD/arrows move, Space spawns orbs, Tab switches scenes.
HelloWorld: WASD/arrows move, mouse wheel zooms, Q/E rotate the camera, Escape quits. Requires an OpenGL 4.5 GPU.

## Run the editor

```bash
dotnet run --project src/Editor/MNVTEngine.Editor -- samples/SceneDemo
```

See [the editor guide](docs/editor/editor.md) for controls. Edit `samples/SceneDemo/Scripts/*.cs` while the editor runs to see hot reload.

## Layout

| Path | Contents |
|---|---|
| `src/Engine` | Runtime engine modules (`Core`, `Mathematics`, `Serialization`, ...) and `GameHost`, the executable of exported games |
| `src/Editor` | Editor: UI-free services and the ImGui application |
| `src/Tooling` | `mnvt` CLI, build pipeline and packaging (`Build`), development runner (`Player`) |
| `templates` | Project templates for `mnvt new` |
| `scripts` | `mnvt.cmd` wrapper |
| `samples` | Runnable sample games |
| `tests` | xUnit v3 tests, including architecture dependency rules and GPU integration tests |
| `docs` | Architecture and subsystem documentation |

## Documentation

- [Architecture overview, dependency rules, decisions, risks](docs/architecture/overview.md)
- [Mathematics conventions](docs/engine/mathematics.md)
- [JSON serialization](docs/serialization/json.md)
- [2D rendering, frame loop, resource ownership](docs/graphics/rendering.md)
- [Scenes, ECS, lifecycle, scene format, standalone runtime](docs/engine/scenes-and-runtime.md)
- [Editor: panels, Play Mode, ImGui integration](docs/editor/editor.md)
- [Assets: metas, GUIDs, importers, prefabs](docs/engine/assets.md)
- [C# scripting: game assemblies, hot reload, unloading](docs/engine/csharp-scripting.md)
- [.mnvts language reference: grammar, semantics, VM limits, errors](docs/scripting/language.md)
- [Missions: lifecycle, script API, persistence](docs/scripting/missions.md)
- [Building and exporting games: CLI, profiles, packaging, validation](docs/build-system/building.md)

## Roadmap

| Milestone | Scope | Status |
|---|---|---|
| 0 | Foundation: solution, math, serialization, logging, timing | done |
| 1 | Window, OpenGL context, shaders, textures, SpriteBatch, Camera2D, input | done |
| 2 | Hybrid ECS, GameObject API, scenes, standalone runtime | done |
| 3 | ImGui editor, Play Mode | done |
| 4 | Asset pipeline with stable GUIDs | done |
| 5 | C# game scripting via AssemblyLoadContext | done |
| 6 | `.mnvts` language, compiler, VM, mission framework | done |
| 7 | Build and Windows x64 packaging, `mnvt` CLI | done |
| 8 | Physics2D, audio, animation, tilemaps, 2D beta | next |
