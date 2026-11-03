# MNVTEngine

A modular 2D game engine in C# / .NET 11: OpenGL 4.5 renderer (Silk.NET), ImGui.NET editor, hybrid ECS,
asset pipeline, the `.mnvts` gameplay scripting language with its own bytecode VM, and standalone game export.

**Status: Milestone 5 (C# Game Scripting).** Game components live in a C# class library that the editor
compiles in the background and hot-reloads (collectible load contexts, data preserved, unload verified).
The `.mnvts` gameplay language and VM come next.

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
| `src/Engine` | Runtime engine modules (`Core`, `Mathematics`, `Serialization`, ...) |
| `src/Editor` | Editor: UI-free services and the ImGui application |
| `src/Tooling` | Script compilation (`Build`), development runner (`Player`); CLI and packaging in Milestone 7 |
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

## Roadmap

| Milestone | Scope | Status |
|---|---|---|
| 0 | Foundation: solution, math, serialization, logging, timing | done |
| 1 | Window, OpenGL context, shaders, textures, SpriteBatch, Camera2D, input | done |
| 2 | Hybrid ECS, GameObject API, scenes, standalone runtime | done |
| 3 | ImGui editor, Play Mode | done |
| 4 | Asset pipeline with stable GUIDs | done |
| 5 | C# game scripting via AssemblyLoadContext | done |
| 6 | `.mnvts` language, compiler, VM, mission framework | next |
| 7 | Build and Windows x64 packaging | |
| 8 | Physics2D, audio, animation, tilemaps, 2D beta | |
