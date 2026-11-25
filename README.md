<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/images/mnvtengine-logo.svg">
    <img src="docs/images/mnvtengine-logo-dark.svg" alt="MNVTEngine" width="380">
  </picture>
</p>

<p align="center">
  <strong>A modular 2D and 3D game engine in C#, built on Vulkan 1.4 and .NET 11.</strong><br>
  Write gameplay in C# or in its own scripting language, edit it live, and ship games whose content is sealed in signed packages.
</p>

<p align="center">
  <a href="https://github.com/notnorelock/MNVTEngine/actions/workflows/build.yml"><img src="https://github.com/notnorelock/MNVTEngine/actions/workflows/build.yml/badge.svg" alt="Build"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="MIT License"></a>
  <img src="https://img.shields.io/badge/.NET-11-512BD4.svg" alt=".NET 11">
  <img src="https://img.shields.io/badge/Vulkan-1.4-AC162C.svg" alt="Vulkan 1.4">
  <img src="https://img.shields.io/badge/platform-Windows%20x64-0078D4.svg" alt="Windows x64">
</p>

---

## Why MNVTEngine

- **One engine, two worlds.** A project picks a 2D or 3D profile. The engine loads only the modules the game actually
  uses, and the export contains only those modules.
- **C# all the way down.** Engine, editor, tooling and gameplay share one language. Game code hot-reloads into a
  collectible `AssemblyLoadContext` while the editor keeps running.
- **A scripting language of its own.** `.mnvts` compiles to bytecode for the engine's VM. It comes with missions and
  objectives, persistence and a debugger, for designers who should not need a C# toolchain.
- **Exports that don't ship your project folder.** Games read content through a virtual file system. The content
  lives in compressed, encrypted and signed `.mnvtpack` packages. C# runs JIT-compiled, or ahead of time as one native
  executable with NativeAOT.
- **Rules enforced by tests.** Module boundaries (Vulkan stays inside the backend, gameplay never sees backend types)
  are checked from the project graph by the test suite.
- **Every build is traceable.** Engine, editor, CLI, packages and exported games all carry the same
  `MAJOR.DDMM.PATCH.BUILD` version, commit and build id.

## Features

| Area | What you get |
|---|---|
| Rendering | Vulkan 1.4 backend (Silk.NET, VMA, shaderc): sprites, meshes, instancing, skinning and blend shapes, PBR materials, directional, point and spot shadows, tone mapping, texture streaming, 2D lighting |
| World | Hybrid ECS with a GameObject API, scenes and prefabs, tilemaps, sprite animation, particle systems in 2D and 3D, terrain with roads, rivers and foliage |
| Physics | Box2D for 2D, BepuPhysics v2 for 3D: rigidbodies, colliders, callbacks, ray casts, character collision |
| Audio | OpenAL output with spatial audio, a mixer with buses and effects, streamed decoding |
| Assets | Stable GUIDs and `.meta` sidecars; importers for images, fonts, audio, FBX, glTF and GLB models with LODs |
| UI | Game UI, text and fonts, splash and loading screens, and an embedded Chromium browser that renders pages onto sprites and meshes |
| Editor | ImGui editor with Play Mode, gizmos, inspectors, undo and redo, hot reload, script debugging |
| Diagnostics | In-game HUD, profiler with GPU timings, console, budgets, debug drawing, crash handler with dumps and reports |
| Shipping | `mnvt` CLI, build profiles, build validator, signed packages, DLC packages (including DLC code), build reports |

## Quick start

You need the .NET 11 SDK (pinned in `global.json`), Windows x64, and a GPU and driver with Vulkan 1.4. The
[LunarG Vulkan SDK](https://vulkan.lunarg.com/) is optional: it adds the validation layer that development builds use.

```bash
git clone https://github.com/notnorelock/MNVTEngine.git
cd MNVTEngine
scripts\mnvt.cmd new MyGame --profile 2d           # or --profile 3d, or --template basic-2d
scripts\mnvt.cmd editor MyGame                     # edit; change Scripts/*.cs and watch it reload
scripts\mnvt.cmd build MyGame --configuration release --run
```

The exported game lands in `MyGame\Builds\release\`. Next steps: [getting started](docs/getting-started.md) and
[building and exporting games](docs/build-system/building.md).

## Samples

```bash
scripts\mnvt.cmd run samples/Platformer
scripts\mnvt.cmd editor samples/Showcase3D
```

| Sample | Shows |
|---|---|
| `Platformer` | A complete 2D level: collect five coins, then reach the flag |
| `SceneDemo` | A tour of 2D features: physics, lighting, scene switching |
| `Showcase3D` | 3D models, materials, lights and shadows |
| `TerrainDemo` | Terrain, roads, rivers and foliage |
| `Particles2D`, `Particles3D` | Particle systems in both worlds |
| `UIShowcase` | Game UI and text |
| `Browser2D`, `Browser3D` | Web pages on sprites and meshes |
| `HelloWorld` | The renderer alone, with automation flags (`--frames 120 --screenshot shot.png --gpu-validation`) |

## Building the engine

```bash
dotnet build MNVTEngine.sln            # development build
dotnet test --solution MNVTEngine.sln  # the whole test suite
scripts\build.cmd Release              # pipeline build with a build number
scripts\Publish-Editor.ps1             # editor in a clean layout: the exe on top, libraries in Dependencies/Managed
```

GPU tests need a Vulkan 1.4 device and skip without one. Every push builds and tests on GitHub Actions. Releases are
published from a manual run of the same workflow ([versioning](docs/architecture/versioning.md)).

## Repository

| Path | Contents |
|---|---|
| `src/Engine` | Runtime modules and `GameHost`, the executable of exported games |
| `src/Editor` | UI-free editor services and the ImGui editor |
| `src/Tooling` | `mnvt` CLI, build and packaging pipeline, development player, version generator |
| `samples`, `templates` | Sample games and `mnvt new` templates |
| `tests` | One xUnit v3 project per module, plus architecture rules |
| `docs` | Architecture and subsystem documentation |

## Documentation

**Start here:** [Getting started](docs/getting-started.md) · [Architecture overview](docs/architecture/overview.md) ·
[Building and exporting](docs/build-system/building.md) · [Editor](docs/editor/editor.md)

<details>
<summary><strong>Engine</strong></summary>

- [Scenes, ECS, lifecycle, scene format, standalone runtime](docs/engine/scenes-and-runtime.md)
- [Project profiles](docs/engine/project-profiles.md) and [engine modules](docs/engine/modules.md)
- [Assets: metas, GUIDs, importers, prefabs](docs/engine/assets.md) and [model import](docs/engine/models.md)
- [C# scripting and hot reload](docs/engine/csharp-scripting.md)
- [Physics 2D](docs/engine/physics2d.md) and [Physics 3D](docs/engine/physics3d.md)
- [Audio](docs/engine/audio.md), [sprite animation](docs/engine/animation.md), [tilemaps](docs/engine/tilemaps.md)
- [Particles](docs/engine/particles.md) and [terrain](docs/engine/terrain.md)
- [Embedded browser](docs/engine/browser.md) ([architecture](docs/architecture/embedded-browser.md))
- [Mathematics conventions](docs/engine/mathematics.md) and [JSON serialization](docs/serialization/json.md)

</details>

<details>
<summary><strong>Graphics and UI</strong></summary>

- [Rendering, frame loop, resource ownership](docs/graphics/rendering.md)
- [Vulkan 1.4 backend: decisions and synchronization](docs/graphics/vulkan-migration.md)
- [Meshes, 3D cameras and 3D rendering](docs/graphics/meshes.md) ([3D plan](docs/graphics/3d-plan.md), [acceptance](docs/graphics/3d-acceptance.md))
- [Materials, lighting and shadows](docs/graphics/materials-and-lighting.md)
- [Game UI, text, fonts, splash and loading screens](docs/ui/ui-system.md)

</details>

<details>
<summary><strong>Scripting</strong></summary>

- [`.mnvts` language reference](docs/scripting/language.md)
- [Missions: lifecycle, script API, persistence](docs/scripting/missions.md)

</details>

<details>
<summary><strong>Shipping and operations</strong></summary>

- [Virtual file system and `.mnvtpack` packages](docs/architecture/virtual-filesystem.md)
- [Code obfuscation (`mnvt build --obfuscate`)](docs/architecture/obfuscation.md)
- [Ahead-of-time compilation (NativeAOT backend)](docs/architecture/il2cpp-aot-pipeline.md)
- [Engine composition audit](docs/architecture/engine-composition-audit.md)
- [Engine versioning and CI](docs/architecture/versioning.md)
- [Logging](docs/engine/logging.md), [runtime diagnostics](docs/engine/diagnostics.md), [profiling](docs/engine/profiling.md)
- [Crash reporting](docs/engine/crash-reporting.md)

</details>

## License

MNVTEngine is released under the [MIT License](LICENSE). Copyright (c) 2026 Norbert Podbiegły (Norelock).

Games made with the engine may use any license. A game that ships the engine's binaries must include this copyright
and permission notice, for example in its credits or a license file.
