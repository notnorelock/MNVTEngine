# MNVTEngine

A modular 2D game engine in C# / .NET 11: OpenGL 4.5 renderer (Silk.NET), ImGui.NET editor, hybrid ECS,
asset pipeline, the `.mnvts` gameplay scripting language with its own bytecode VM, and standalone game export.

**Status: Milestone 4 (Asset Pipeline).** GUID-based assets with `.meta` files, importers with import
settings, dependency tracking and prefabs, on top of the editor (Milestone 3) and standalone runtime
(Milestone 2). C# game scripting with reload comes next.

## Requirements

- .NET SDK 11 (`global.json` pins `11.0.100-rc.3` and rolls forward)
- Windows x64 is the first target platform

## Build and test

```bash
dotnet build MNVTEngine.sln
dotnet test --solution MNVTEngine.sln
```

## Run the samples

```bash
# A data-driven game project: scenes in Assets/Scenes, components in Scripts/
dotnet run --project samples/SceneDemo/SceneDemo.csproj

dotnet run --project samples/HelloWorld
# Automated: render 120 frames, save a screenshot, exit
dotnet run --project samples/HelloWorld -- --frames 120 --screenshot shot.png --debug-gl
```

SceneDemo: WASD/arrows move, Space spawns orbs, Tab switches scenes.
HelloWorld: WASD/arrows move, mouse wheel zooms, Q/E rotate the camera, Escape quits. Requires an OpenGL 4.5 GPU.

## Run the editor

```bash
dotnet build samples/SceneDemo/SceneDemo.csproj
dotnet run --project src/Editor/MNVTEngine.Editor -- samples/SceneDemo --scripts samples/SceneDemo/bin/Debug/net11.0/SceneDemo.dll
```

See [the editor guide](docs/editor/editor.md) for controls.

## Layout

| Path | Contents |
|---|---|
| `src/Engine` | Runtime engine modules (`Core`, `Mathematics`, `Serialization`, ...) |
| `src/Editor` | Editor: UI-free services and the ImGui application |
| `src/Tooling` | CLI, build and packaging (Milestone 7) |
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

## Roadmap

| Milestone | Scope | Status |
|---|---|---|
| 0 | Foundation: solution, math, serialization, logging, timing | done |
| 1 | Window, OpenGL context, shaders, textures, SpriteBatch, Camera2D, input | done |
| 2 | Hybrid ECS, GameObject API, scenes, standalone runtime | done |
| 3 | ImGui editor, Play Mode | done |
| 4 | Asset pipeline with stable GUIDs | done |
| 5 | C# game scripting via AssemblyLoadContext | next |
| 6 | `.mnvts` language, compiler, VM, mission framework | |
| 7 | Build and Windows x64 packaging | |
| 8 | Physics2D, audio, animation, tilemaps, 2D beta | |
