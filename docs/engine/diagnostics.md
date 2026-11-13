# Runtime diagnostics and the in-game debug UI

A running game can be inspected from inside its own window: a HUD with live metrics, a profiler, a developer console, performance budgets, and inspection windows. The same interface appears in three places:

- the editor's Game view during Play Mode;
- the Player (`mnvt run`);
- Debug exports.

**Release exports contain none of it.** The debug UI assemblies and Dear ImGui are left out of the build (see [Build modes](#build-modes)).

| Piece | Project | What it does |
|---|---|---|
| Metrics, budgets, `Debug` API, console commands, settings | `MNVTEngine.Diagnostics` | No rendering and no backend. |
| Render statistics, GPU timer queries, GPU memory, allocator statistics, backend facts | `MNVTEngine.Graphics` (+ Vulkan implementation) | Optional device capabilities. |
| `RuntimeDiagnostics`: metric providers, built-in commands, frame lifecycle | `MNVTEngine.Runtime` | Collects data; draws nothing. |
| `DebugOverlay`: HUD, windows, console, debug-draw rendering; the shared ImGui backend | `MNVTEngine.DebugUI` | Development builds only. |

Collection is separate from presentation. Panels only read `RuntimeDiagnostics` (metrics, budgets, profiler, history); they never gather data themselves.

## Shortcuts

| Key | Action |
|---|---|
| F1 | HUD on/off |
| F2 | Profiler window |
| F3 | Developer console |
| F4 | Debug drawing on/off |
| F5 | Budget warnings in the HUD on/off |
| F6 | Windows panel: open any window, HUD appearance, metric providers |

- **Rebinding:** keys can be changed in `diagnostics.keys` (names of `MNVTEngine.Key` values). An unknown name logs a warning and turns that shortcut off.
- **Shortcut keys never reach gameplay.** A pressed shortcut key stays hidden from the game until it is released.
- **Input capture:** while a debug window wants the keyboard or mouse (typing in the console, dragging a window), gameplay sees no keyboard or mouse input (`InputState.KeyboardCaptured` / `MouseCaptured`). The HUD never takes input, so with only the HUD visible, gameplay input is untouched.
- **In the editor,** the Game view must have focus. This is the same rule as for game input.

## HUD

The HUD is a box in one corner of the view. Its sections are configurable (`overlay.sections`):

| Section | Shows |
|---|---|
| `frame` | FPS, frame time, CPU time, GPU time |
| `memory` | Process memory, GPU memory, managed heap, allocations per frame, GCs per minute |
| `renderer` | Draw calls, vertices, triangles, textures, shaders, batches, sprites |
| `runtime` | Entities, components, systems, physics bodies, audio voices, C# scripts, script VMs, uptime, frame number |
| `scripting`, `physics`, `assets` | Counts for those subsystems |
| `particles` | Active particle systems, particles alive, emitted per frame, CPU simulation time, particles drawn, particle draw calls, particle rendering time ([particle metrics](particles.md#diagnostics-and-performance)) |
| `browser` | Embedded browsers, Chromium paints and texture uploads per frame, upload size and time, texture memory, browser host memory; GPU zero-copy shows Unsupported ([browser diagnostics](browser.md#diagnostics)) |
| `warnings` | Budgets at Warning or above, e.g. `WARNING: TEXTURE MEMORY BUDGET AT 91.20%` |

**Values that are not measured show as `n/a`, never as 0:**

- `n/a (unsupported)`: the driver or backend cannot measure it, such as GPU memory without a vendor extension.
- `n/a`: nothing has measured it yet, such as GPU time during the first frames or GCs per minute before 10 seconds have passed.

Corner, opacity, scale, theme (`dark`, `light`, `classic`) and title are set in `diagnostics.overlay`, or for one session in the Windows panel.

## Windows

| Window | Contents |
|---|---|
| **Profiler** | CPU per frame with min, p50, p95, p99 and max over 60 to 600 frames; frame, CPU and GPU graphs; the sample tree with average and maximum ms and calls; GPU scopes; counters. **Snapshot** freezes the numbers; **Capture to file** writes a report. |
| **Console** | Log with severity, logger and text filters, timestamps, stack traces in tooltips and copy via right-click; commands with history (Up/Down) and Tab completion. |
| **Budgets** | Every budget with its value, limit (editable for this session), usage, peak and state; recent warnings. |
| **Renderer** | The last frame for the game and with the debug UI: draw calls, vertices, indices, triangles, lines and switches; GPU resources; estimated texture memory; GPU memory. |
| **ECS** | Entity hierarchy with a filter. The selected object is marked in the game, and its components show their saved fields (read-only). Systems with their order and time. |
| **Memory** | Heap, process and allocation graphs; GPU, texture, audio and asset memory; GC counts and pause time; memory budgets; a button to run a garbage collection. |
| **Scripting** | Loaded game assemblies (reloadable or not); C# script components per type with time; `.mnvts` VMs with coroutines per state and mission and objective states; recent script exceptions with stack traces. |
| **Physics** | Bodies, colliders, contacts, trigger overlaps and step time. **Show colliders** draws outlines: green dynamic, blue kinematic, gray static, yellow trigger. |
| **Assets** | Loaded textures sorted by memory, audio clips, the asset database by type, and import or load failures. |
| **Mixer** | The scene's AudioMixer as a mixing console: faders, mutes, insert effects and live stereo meters with peak hold and clip lights ([audio mixer](audio.md#meters-and-the-mixing-console)). Changes last for the run. |

**Custom windows:** subclass `MNVTEngine.DebugUI.Panels.DebugPanel` and call `overlay.AddPanel(...)`.

**Turning windows off:** `diagnostics.disabledPanels` removes windows a project does not want, and `diagnostics.disabledProviders` switches off metric providers (`Time`, `Memory`, `Renderer`, `Runtime`, `Assets`, `Content`).

**Content metrics** (`content.*`, provider `Content`): mounted sources and packages, package content size, files (unsupported while a folder is mounted), open streams, pending asynchronous reads, bytes read and chunks decoded per frame, chunk cache hits, verification, decryption and decompression time per frame (all threads), total package opening time, failed reads and corrupted chunks. Loading and streaming metrics (`assets.loadTime`, `scene.loadTime`, `textures.*`, `memory.streamedTextures`, `audio.streams`, `audio.underruns`) are listed there too. See [virtual file system](../architecture/virtual-filesystem.md#10-diagnostics).

**Window layout** is kept between runs:

- Player: `Library/Diagnostics/debug-ui.ini`.
- Exports: `Logs/debug-ui.ini`.
- Editor: `Library/Editor/game-debug-ui.ini`.

## Debug drawing and logging

```csharp
using MNVTEngine.Diagnostics;

public sealed class PlayerController : Component
{
    public override void Update()
    {
        Debug.DrawText("Player is moving", new Vector2(20, 40), Color.Green);              // screen pixels, top-left origin
        Debug.DrawLine(Transform.Position, Transform.Position + new Vector2(2, 0), Color.Red); // world units
        Debug.DrawCircle(Transform.Position, 0.5f, Color.Yellow);
        Debug.DrawLabel($"hp {Health}", Transform.Position + new Vector2(0, 1), Color.White);
        Debug.DrawRect(new Rect(10, 10, 100, 50), Color.White, space: DebugSpace.Screen);
    }
}
```

- **Shapes:**
  - `DrawLine`, `DrawRay`, `DrawRect` (outline or filled), `DrawCircle` (outline or filled), `DrawPoint`, `DrawBounds` (`Rect` or the XY of `Bounds`), `DrawGrid`.
  - Text: `DrawText`/`DrawScreenText` (screen), and `DrawWorldText`/`DrawLabel` (anchored to a world position; the size stays in pixels).
  - Text is drawn with the engine's text stack (`DebugTextRenderer` in Runtime: the built-in font through the glyph atlas and the sprite batch), after the game and its UI and below the debug windows, in the game and in the editor's Game view. The other shapes go through the overlay's ImGui draw lists.
  - Text options: font size, alignment, and a background color.
- **Spaces:** geometry is in world space by default. Pass `space: DebugSpace.Screen` for pixels of the view.
- **Lifetime:** `duration: 0` (the default) draws for one frame. Seconds keep it that long. `Debug.Persistent` keeps it until `Debug.Remove(handle)` or `Debug.ClearPersistent()`.
- **Cost:** commands go to a fixed-capacity buffer (`diagnostics.maxDebugDrawCommands`), compacted in place every frame, so drawing allocates nothing beyond your strings.
  - Draws beyond the capacity are dropped and counted. The `debugDrawCommands` budget reports them.
  - Rendering shares the debug UI's vertex buffer and font atlas, so all debug draws together cost a few draw calls.
  - Text is real TrueType text: Segoe UI when available, otherwise ImGui's built-in font.
  - If a frame needs more than about 120 000 vertices, the rest is skipped.
- **Depth:** the engine is 2D without a depth buffer. Debug draws appear over the scene, world space first, and under the debug windows.
- **No-ops:** with drawing off (F4), in Release builds, or without diagnostics, every call returns immediately.

**Logging:** `Debug.Log(message, category)`, `Debug.LogWarning`, `Debug.LogError` and `Debug.LogException(exception)`.

- They use the logger named after the calling class (or `category`) and record class, method, file and line.
- They go through the normal logging pipeline and its levels per logger; see [Logging](logging.md).

## Console commands

`help` lists the commands.

| Command | Does |
|---|---|
| `fps`, `stats`, `memory`, `renderer` | Show metrics (`memory` also queries GPU memory once). |
| `crash managed`, `crash native` | Crash the game on purpose, to test [crash reporting](crash-reporting.md). |
| `scene`, `entities [filter]`, `scripts`, `missions` | Inspect the running game. |
| `browsers`, `browser.reload <id>` | The browser host and every [embedded browser](browser.md); reload one (recovers it after a crash). |
| `budgets`, `budgets set <id> <limit>` | List budgets; change a limit for this session. |
| `profiler`, `profiler capture`, `profiler reset`, `profiler detail frame\|systems\|scripts` | Summary; report file; reset peaks; instrumentation level. |
| `gc` | Run a full garbage collection and print the heap before and after. |
| `draw clear\|on\|off` | Debug drawing. |
| `providers [on\|off <name>]` | Metric providers. |
| `overlay [on\|off]`, `window <name> [on\|off]`, `clear` | The debug UI itself. |

**Game commands** go through the same explicit registry:

```csharp
Debug.Commands?.Register("god", "Toggles invulnerability.", _ => DebugCommandResult.Ok($"god mode {(God = !God ? "on" : "off")}"));
```

Only registered delegates run: there is no shell or process access. A command that throws reports its exception instead of breaking the console.

**Unregister commands in `OnDestroy`.** Commands registered by C# game code keep that code alive, and that blocks unloading after a hot reload.

**Automation:** `--debug-command "<line>"`, which can be repeated, runs commands at startup in the Player and in exports.

## Profiler

- **Instrumenting your code:** `using (Profiler.BeginSample("PlayerController.Update")) { ... }`, or the shorter `Profiler.Sample`. Use constant names.
- **Detail** (`profiler detail` or the Profiler window):
  - `Frame`: frame totals only.
  - `Systems` (default): engine phases and systems.
  - `Scripts`: also every component callback per type (`Player.Update`), at the cost of a dictionary lookup per call.
- **GPU timing** uses Vulkan timestamp queries (`vkCmdWriteTimestamp2`):
  - Each of the 2 frames in flight has a query pool. Results are read when that frame slot is reused, after its fence has signalled, without `WAIT`. Nothing ever waits on the GPU for them, and frames whose results are not available are counted as dropped.
  - Scopes: `Scene` and `Debug Overlay`.
- **Overhead is visible:** the `Diagnostics` sample includes every metric provider, and `Debug Overlay` is the UI.
  - Measured on the Platformer with an RTX 2060 SUPER: diagnostics cost about 0.05 ms and the overlay about 0.02 ms per frame with the HUD hidden.
- **The editor shares one profiler:** its Profiler panel and the Play Mode diagnostics use the same `FrameProfiler`; there are not two systems.

## Performance budgets

**Configuration:** budgets are configured per project in `diagnostics.budgets`. Every limit is optional; `null` turns that budget off.

| Group | Budgets (units) |
|---|---|
| Frame | `frameTimeMs` (30-frame average), `cpuFrameTimeMs`, `gpuFrameTimeMs` |
| Renderer | `maxDrawCalls`, `maxVertices`, `maxTriangles`, `maxTextureSwitches`, `maxShaderSwitches`, `maxRenderTargets`, `maxDebugDrawCommands` |
| Memory | `managedMemoryMb`, `processMemoryMb`, `gpuMemoryMb` (off by default), `textureMemoryMb`, `assetMemoryMb`, `audioMemoryMb`, `allocationKbPerFrame`, `gcPerMinute` |
| Runtime | `maxEntities`, `maxComponents`, `maxPhysicsBodies`, `maxScriptCoroutines` |
| Particles | `maxParticles` (alive, default 100,000), `particleSimulationMs` (CPU simulation per frame, default 4) |

**Thresholds** (`diagnostics.thresholds`) set the severities. Information starts at 70 % of the limit, Warning at 85 %, Error ("exceeded") at 100 % and Critical at 150 %.

**The frame-time budget is special:**

- With VSync the frame time equals the refresh period, so at 60 Hz it is always "at 100 %" of a 16.67 ms limit.
- It therefore uses the 30-frame average with thresholds of its own (95 / 103 / 110 / 150 %), so it reports sustained drops, not jitter.
- CPU and GPU time are per frame.

**Warnings:**

- **Contents:** each warning names the budget, its subsystem, the value, the limit, the percentage used, and where to look ("Use texture atlases and fewer materials so sprites batch.").
- **Where they appear:** Warning and above are logged; the HUD shows the current ones.
- **No flooding:**
  - A budget reports when it reaches a higher severity.
  - While a severity persists, it repeats at most every `warningRepeatSeconds` (10 s).
  - A value flickering around a threshold reports once.
  - The first `warmupFrames` (60) frames, which include loading and JIT warm-up, are not evaluated.
- **Unmeasured metrics never warn.**

**Session overrides** never touch the project file:

- `budgets set` or the Budgets window, for the running game.
- The editor's **Window → Diagnostics Settings**, for the next Play.

### GPU memory

GPU memory comes from `VK_EXT_memory_budget`: total is the size of the device-local heaps, and available is this process's budget minus its usage. Without the extension it is unsupported.

- **It covers the device-local heaps, other processes included** (they shrink the budget).
- Reading it costs no GPU synchronization (the OpenGL vendor queries used before stalled up to a frame). It is still sampled every 5 s, and only while something displays it (the HUD memory section, the Renderer or Memory window) or a `gpuMemoryMb` budget exists.
- **The engine's own allocations** come from the GPU allocator (VMA) and are exact:
  - `GPU allocated`: memory reserved from the driver;
  - `GPU allocations`: live allocations;
  - `GPU allocation failures`;
  - `Buffer memory`.
  Backends without an allocator report them as unsupported.
- **Vulkan specifics** are in the Renderer window's *Backend* section and the `renderer` command:
  - API and instance version, GPU and driver;
  - swapchain size, format, present mode and image count;
  - frames in flight;
  - validation state, error and warning counts, and the last error;
  - pipelines created and the pipeline cache;
  - streaming memory, memory budget source and GPU timer.
- **Per-frame counters** cover draw calls, geometry, texture, shader and target switches, plus pipeline switches, push-descriptor writes, barriers and queue submits.

**Texture memory** is the exact allocation size of textures and render targets, from the allocator. On a device without an allocator it is estimated from the sizes (width × height × 4 bytes), and shown as an estimate (`~12.0 MB`, status `Estimated`). Estimated values are budgeted like measured ones.

## Configuration

The `diagnostics` section of the `.mnvtproj`. Every value is optional; the defaults are shown:

```json
"diagnostics": {
  "enabled": true,
  "showOverlayByDefault": false,
  "enableProfiler": true,
  "enableGpuTiming": true,
  "enableDebugDrawing": true,
  "enablePerformanceWarnings": true,
  "shortcutsEnabled": true,
  "maxDebugDrawCommands": 20000,
  "warmupFrames": 60,
  "warningRepeatSeconds": 10,
  "disabledPanels": [],
  "disabledProviders": [],
  "keys": { "toggleOverlay": "F1", "toggleProfiler": "F2", "toggleConsole": "F3", "toggleDebugDrawing": "F4", "toggleWarnings": "F5", "toggleWindows": "F6" },
  "overlay": { "sections": ["frame", "memory", "renderer", "runtime", "warnings"], "corner": "topLeft", "opacity": 0.55, "scale": 1, "title": "DEVELOPMENT BUILD", "theme": "dark" },
  "budgets": { "frameTimeMs": 16.67, "maxDrawCalls": 1000, "maxEntities": 10000, "managedMemoryMb": 1024, "textureMemoryMb": 512 },
  "thresholds": { "information": 0.7, "warning": 0.85, "error": 1.0, "critical": 1.5 }
}
```

- **Invalid values** (negative limits, thresholds that do not increase, out-of-range counts) are replaced by their defaults with a warning when the project loads.
- **Editing in the editor:** **Window → Diagnostics Settings** edits these settings for the session and saves them into the project. Saving rewrites only this section and the `devtools` module switch ("Include in Release builds").
- **`includeInRelease`** from older projects is read as the module switch `"modules": { "devtools": true }` and saved as that switch.
- **Command-line switches** (Player and exports): `--overlay` shows the HUD at start, and `--no-diagnostics` runs without diagnostics.

## Build modes

| Build | Debug UI and ImGui | Diagnostics | Debug draws, budgets |
|---|---|---|---|
| Editor Play Mode, Player | yes | yes | yes |
| Debug export | yes (`cimgui.dll` in `Dependencies/Native`) | yes | yes |
| Release export | **not in the build** | off | no-ops; no monitoring |
| Release with `"modules": { "devtools": true }` | yes (opt-in) | yes | yes |
| Debug with `"modules": { "devtools": false }` | no | off | no-ops |

- **Mechanism:** dev tools are the `devtools` [module](modules.md): the project's switch decides, and without one Debug builds have it and Release builds do not. The build resolves it with the other modules and passes the result to the game host as `MnvtDevTools`, which references `MNVTEngine.DebugUI` and defines `MNVT_DEVTOOLS`.
- **The manifest** records `devTools`.
- **The build validator** rejects DebugUI and ImGui files in a game without dev tools (MNVB508), and requires `cimgui.dll` in one that has them.
- **The architecture tests** check that only the editor, the Player and (conditionally) the game host reference the debug UI.

## Tests

- **Metric, budget and command logic** run without a GPU: budget thresholds, deduplication, rate limiting, flicker, unmeasured metrics, session overrides, the command registry, the draw buffer's lifetime and overflow, settings validation and serialization, project save and load, the `RuntimeDiagnostics` frame lifecycle and commands, input capture and suppression, profiler percentiles and detail levels.
- **GPU tests** run on a real Vulkan device (headless) with the validation layer requested, and fail on any validation error:
  - frame statistics including pipeline and descriptor counts, timestamp queries delivered without waiting, and allocator and budget memory;
  - the overlay's own ImGui context (the caller's context is restored);
  - F1 toggling the HUD and never reaching the game;
  - the console capturing and releasing the keyboard;
  - screen- and world-space debug draws checked by pixel, and drawing switched off;
  - game rendering correct after the overlay;
  - every window drawn with `%` signs in its text, at 1280×720.

## Limitations

- **Text formatting:** ImGui.NET's `TextColored`, `BulletText`, `SetTooltip` and `TextWrapped` treat their text as a printf format. Engine and editor code uses the `Gui.TextColored`/`Bullet`/`Tooltip`/`TextWrapped` helpers, which draw text verbatim. Custom panels should do the same.
- **Number fields:** `ImGui.InputDouble` hung inside Dear ImGui in a large view. The UI uses `Gui.InputNumber`, a text field parsed as a number.
- **High-DPI:** fonts are rasterized for the display scale at startup. Moving the window to a monitor with another scale does not re-rasterize them.
- **Per-script timing** times callbacks per component type, not per instance. `.mnvts` execution time is the `Scripts.Missions` sample (all VMs together).
- **Asset memory** counts GPU textures and decoded audio, not managed copies of scene data.
- **Editor GPU time** in Play Mode spans the editor's whole frame from the game's update on, including the Scene view.
