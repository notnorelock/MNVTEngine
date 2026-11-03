# MNVTEngine Architecture Overview

Status: **Milestone 1 (Graphics Foundation)** complete. This document is the source of truth for module boundaries.
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
| MNVTEngine.ECS | M2 | Entity storage, component pools, queries, systems | Core |
| MNVTEngine.Scene | M2 | GameObject/Component API, Transform hierarchy, scene lifecycle and serialization | ECS, Serialization |
| MNVTEngine.Runtime | M2 | Game host loop wiring platform + graphics + scene | everything engine-side except Editor/Tooling |
| MNVTEngine.Assets | M4 | GUID database, importers, metadata | Serialization |
| MNVTEngine.Scripting | M6 | Language (lexer, parser, AST), compiler, bytecode, VM | Core only |
| MNVTEngine.Scripting.Missions | M6 | Mission framework, persistence | Scripting, Serialization |
| MNVTEngine.Editor | M3 | ImGui workspace, panels, gizmos | anything |
| MNVTEngine.Build / CLI | M7 | Project build, packaging, `mnvt` command | engine + Assets, never Editor |

Deviations from the original proposal, and why:

- **Windowing + Input merged into `Platform`.** Both come from the same Silk.NET window object and lifetime; splitting them creates an interface layer with one implementation.
- **Scripting.Language/Compiler/VM start as one project** with folders. The editor reuses the compiler through its public API; the VM's isolation from graphics is enforced by the architecture tests, not by assembly count. Split later if build times or ownership require it.
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

## Technical risks

| Risk | Impact | Mitigation |
|---|---|---|
| .NET 11 is a release candidate | API or tooling changes before GA | `global.json` rolls forward to GA; CI should pin once GA ships |
| ImGui.NET (1.91.6.1) is updated infrequently | Docking API or native binary gaps | Evaluate at M3; Hexa.NET.ImGui is the fallback. The editor isolates ImGui behind its own UI layer |
| Silk.NET 2.x vs 3.0 transition | API churn | Pinned 2.23.0; Silk types do not appear in Graphics or gameplay-facing input APIs |
| Newtonsoft.Json caches contracts per `Type` | Blocks unloading collectible game-script `AssemblyLoadContext`s (M5) | Use a per-load-context `JsonSerializer`/contract resolver and drop it on reload; verify unload with a `WeakReference` test |
| Native deps in self-contained publish | GLFW/OpenAL natives not always copied | Explicit packaging step and a build test that launches the exported game (M7) |
| Physics scope | Custom solver is a large effort | Decide at M8: simple custom impulse solver for AABB/circle vs. integrating a permissively licensed Box2D port |
| OpenGL integration tests | CI agents may lack a GL context | Separate test category requiring a GPU; unit tests stay GL-free |
