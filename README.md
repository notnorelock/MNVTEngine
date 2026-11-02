# MNVTEngine

A modular 2D game engine in C# / .NET 11: OpenGL 4.5 renderer (Silk.NET), ImGui.NET editor, hybrid ECS,
asset pipeline, the `.mnvts` gameplay scripting language with its own bytecode VM, and standalone game export.

**Status: Milestone 0 (Foundation).** Math, timing, logging and JSON infrastructure exist and are tested.
Nothing renders yet.

## Requirements

- .NET SDK 11 (`global.json` pins `11.0.100-rc.3` and rolls forward)
- Windows x64 is the first target platform

## Build and test

```bash
dotnet build MNVTEngine.sln
dotnet test --solution MNVTEngine.sln
```

## Layout

| Path | Contents |
|---|---|
| `src/Engine` | Runtime engine modules (`Core`, `Mathematics`, `Serialization`, ...) |
| `src/Editor` | Editor (Milestone 3) |
| `src/Tooling` | CLI, build and packaging (Milestone 7) |
| `tests` | xUnit v3 tests, including architecture dependency rules |
| `docs` | Architecture and subsystem documentation |

## Documentation

- [Architecture overview, dependency rules, decisions, risks](docs/architecture/overview.md)
- [Mathematics conventions](docs/engine/mathematics.md)
- [JSON serialization](docs/serialization/json.md)

## Roadmap

| Milestone | Scope | Status |
|---|---|---|
| 0 | Foundation: solution, math, serialization, logging, timing | done |
| 1 | Window, OpenGL context, shaders, textures, SpriteBatch, Camera2D, input | next |
| 2 | Hybrid ECS, GameObject API, scenes, standalone runtime | |
| 3 | ImGui editor, Play Mode | |
| 4 | Asset pipeline with stable GUIDs | |
| 5 | C# game scripting via AssemblyLoadContext | |
| 6 | `.mnvts` language, compiler, VM, mission framework | |
| 7 | Build and Windows x64 packaging | |
| 8 | Physics2D, audio, animation, tilemaps, 2D beta | |
