# AGENTS.md

Instructions for AI coding agents working on MNVTEngine. Human-facing docs start at [README.md](README.md).

MNVTEngine is a modular 2D game engine in C# / .NET 11: Vulkan 1.4 renderer (Silk.NET, VMA, shaderc), ImGui.NET editor, hybrid ECS,
Box2D physics, OpenAL audio, asset pipeline with stable GUIDs, C# components with hot reload, the `.mnvts` scripting
language (own bytecode VM, missions, debugger), runtime diagnostics overlay and standalone Windows export.

## Build and test

```bash
dotnet build MNVTEngine.sln
dotnet test --solution MNVTEngine.sln                                    # whole suite
dotnet test --project tests/MNVTEngine.Scenes.Tests                      # one project (positional path is rejected)
dotnet test --project tests/MNVTEngine.Scenes.Tests -- --filter-class "*SceneTests"
dotnet test --project tests/MNVTEngine.Scenes.Tests -- --filter-method "*Prefab*"
```

- SDK is pinned in `global.json` (`11.0.100-rc.3`, rolls forward). Tests use xUnit v3 on Microsoft.Testing.Platform.
- `TreatWarningsAsErrors`, `AnalysisMode=Recommended` and `EnforceCodeStyleInBuild` are on (`Directory.Build.props`).
  Fix analyzer warnings; do not suppress them without a reason in a comment.
- Package versions live in `Directory.Packages.props` (central package management). Add new packages there, not in csproj files.
- Add projects with `dotnet sln add --in-root <csproj>`; adding into solution folders fails on this SDK.
- Versions are generated, never written in project files: `eng/version.json` sets MAJOR, the rest comes from the date,
  Git and the build counter (`Directory.Build.targets`, [versioning](docs/architecture/versioning.md)). `dotnet build`
  gives BUILD 0; `scripts/build.cmd` and `mnvt build` allocate build numbers in `artifacts/build-state`.
- MSBuild treats `*.mnvtproj` as a project file: build the `.csproj` explicitly when one sits next to it.
- GPU tests (`Graphics.Vulkan.Tests`, `DebugUI.Tests`, and `Build.Tests` that run an exported game) need a Vulkan 1.4 GPU and skip without one (as on GitHub's runners). They request the
  validation layer and fail on validation errors. Install the LunarG Vulkan SDK, or point `VK_ADD_LAYER_PATH` at a
  Khronos validation layer build, or validation is silently off. GPU test classes share one `[Collection]` with
  `DisableParallelization = true`.
- The user often has the editor running, which locks `src/Editor/MNVTEngine.Editor/bin` (MSB3027). Never kill it.
  Build the editor or `Editor.Tests` with `-p:OutDir=<temp dir>` and run the other test projects one by one.

Run the engine:

```bash
scripts\mnvt.cmd editor samples/Platformer
scripts\mnvt.cmd run samples/Platformer
scripts\mnvt.cmd build samples/Platformer --configuration release --run
dotnet run --project samples/HelloWorld -- --frames 120 --screenshot shot.png --gpu-validation
```

## Layout

| Path | Contents |
|---|---|
| `src/Engine` | Runtime modules (`Core`, `Mathematics`, `ECS`, `Scenes`, `Graphics`, `Graphics.Vulkan`, `Platform`, `Input`, `Audio`, `Physics2D`, `Assets`, `Scripting*`, `Diagnostics`, `DebugUI`, `CrashReporting`, `Runtime`, ...) and `GameHost`, the executable of exported games |
| `src/Editor` | `Editor.Services` (UI-free, testable) and `Editor` (ImGui application) |
| `src/Tooling` | `CLI` (`mnvt`), `Build` (export pipeline, packaging, validator), `Player` (development runner) |
| `samples`, `templates` | Sample games and `mnvt new` templates |
| `tests` | One xUnit project per module, plus `Architecture.Tests` |
| `docs` | Architecture and subsystem docs; [architecture overview](docs/architecture/overview.md) lists decisions and rules |

## Architecture rules

`tests/MNVTEngine.Architecture.Tests` enforces these from the csproj graph. Keep them green; do not weaken a rule to make a change fit.

- Engine projects never reference Editor or Tooling projects. Tooling never references Editor.
- Graphics and Platform never reference each other; the backend (`Graphics.Vulkan`) is chosen at the host, not in Runtime.
  No Vulkan (Silk.NET/Vortice) types outside `Graphics.Vulkan`; gameplay code and scripts never see them.
- Scenes never references Graphics or Platform; ECS never references Scenes; Input depends only on Mathematics.
- Scripting projects and Physics2D never reference Graphics, Graphics.Vulkan or ImGui. Audio never references OpenAL.
- `DebugUI` (ImGui) is a development tool: GameHost references it only when `MnvtDevTools` is set (`MNVT_DEVTOOLS` define).
  Release exports must not contain it unless the project opts in; the build validator reports MNVB508 otherwise.

## Code conventions

- Match the surrounding code: naming, comment density, file layout. `.editorconfig` is authoritative for style.
- JSON is Newtonsoft.Json only. Never use `TypeNameHandling`; polymorphism goes through explicit registries. Project and scene files are untrusted input: validate them.
- Logging: inject `ILogger` and use `[LoggerMessage]` source-generated methods. Gameplay code uses `GameLog` / `Component.Log`. Never `Console.WriteLine` in engine code. See [logging](docs/engine/logging.md).
- No swallowed exceptions. A `catch` either handles a specific case, logs with context, or rethrows. Catch filters exclude `OutOfMemoryException`.
- GPU and native resources implement `IDisposable` and are disposed by their owner; `GraphicsResourceTracker` reports leaks.
- No fake placeholders or stub implementations presented as working. Unsupported metrics report `Unsupported`/`Unavailable`, never 0.
- Component state persists only through public fields or `[SerializeField]`; add a serializer round-trip test for new components.
- Runtime code reads game content only through `AssetDatabase` / its virtual file system (`MNVTEngine.IO`), never with `File.*`: exported games have no asset files, only signed `.mnvtpack` packages. See [virtual file system](docs/architecture/virtual-filesystem.md).
- Asset references are `"asset:<guid>"` with sidecar `.meta` files. When hand-writing metas, make sure GUIDs are unique: a duplicate is silently reassigned.
- Gameplay code never sees backend types (Silk.NET, OpenAL, Box2D); use engine types such as `Key`.
- Developer console commands are an explicit registry; never execute shell or system commands from it.

## ImGui.NET pitfalls

- `ImGui.TextColored`, `BulletText`, `SetTooltip`, `TextWrapped` and `TextDisabled` treat text as a printf format: a `%` hangs or crashes.
  Use `Gui.TextColored`, `Gui.Bullet`, `Gui.Tooltip`, `Gui.TextWrapped`, `Gui.TextDisabled` from `MNVTEngine.DebugUI.ImGuiBackend`.
- `ImGui.InputDouble` hung natively in large views; use `Gui.InputNumber`.
- The debug overlay owns its own ImGui context and must restore the caller's context.

## Vulkan backend

- Hosts call `device.Present()` at the end of each rendered frame; take screenshots (`ReadPixels`) before it.
- Never destroy Vulkan objects directly: resources go through `DestroyLater`, which waits for frames in flight.
- Shaders stay GL-style GLSL 450 (compiled with relaxed rules, linked by name); see `docs/graphics/rendering.md`.
- Run apps with `--gpu-validation` after rendering changes and check the log for `Vulkan` warnings and errors.

## Hot reload

C# game code loads into a collectible `AssemblyLoadContext`. Anything static that holds game types blocks unloading
(see `ReflectionCacheCleaner`). Game code that registers console commands or static callbacks must unregister them in `OnDestroy`.

## Workflow

- Each change ships with tests and updated docs (`docs/`, README links) for the behavior it changes.
- Run the affected test projects before finishing; run the full suite for cross-cutting changes.
- Verify rendering or UI changes in the real app (Player, editor, or an exported build), not only in tests.
- Commit locally only when asked. Never push. Do not include unrelated user changes in a commit.
