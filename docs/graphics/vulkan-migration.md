# Vulkan 1.4 migration

MNVTEngine moves its rendering backend from OpenGL 4.5 to Vulkan 1.4. This document is the Phase 0 audit and the plan.
It records the decisions and is kept up to date as the phases land.

## Audit (Phase 0)

### Versions checked (2026-11-04)

| Item | Found | Notes |
|---|---|---|
| Vulkan loader / instance | 1.4.357 (NVIDIA driver 621.1, RTX 2060 SUPER, device API 1.4.357) | `vulkan-1.dll` comes from the GPU driver |
| Vulkan SDK installed | none | Latest LunarG SDK is **1.4.363.1** (Windows, Linux, macOS). Needed for the validation layer, `glslc`, `spirv-val` |
| Khronos validation layer | not installed | Without the SDK on Windows, only the `nightly` GitHub build exists. Local checks used that build through `VK_ADD_LAYER_PATH` |
| `Silk.NET.Vulkan` (+ `.Extensions.KHR`, `.Extensions.EXT`) | 2.23.0 (latest stable) | Generated from Vulkan headers 1.4.336: has `PhysicalDeviceVulkan14Features`, `CmdPushDescriptorSet`, `MapMemory2`, `CmdPipelineBarrier2`, `CmdBeginRendering`, `QueueSubmit2`. MIT. Same Silk.NET version as the existing windowing and input packages |
| `Silk.NET.Shaderc` + `.Native` | 2.23.0 | shaderc with `shaderc_compile_options_set_vulkan_rules_relaxed`, per-stage binding bases, and a native win-x64 DLL (7 MB). MIT / Apache-2.0 |
| `Vortice.VulkanMemoryAllocator` | 1.8.0 (VMA 3.x, depends on `Vortice.Vulkan` 3.3.0) | MIT. Native `vma.dll` for win-x64 and win-arm64 only. Works with handles created by Silk.NET once `Vortice.Vulkan.Vulkan.vkInitialize()` has loaded the same `vulkan-1.dll` (spike verified) |
| ImGui.NET | 1.91.6.1 (unchanged) | Has no Vulkan backend. Not needed, see below |
| .NET | 11 RC3 | All packages target netstandard2.0, net5.0 or net9/10, and load on net11 |

### OpenGL dependency inventory

| Where | What |
|---|---|
| `src/Engine/MNVTEngine.Graphics.OpenGL` | `GLGraphicsDevice`, `GLResources` (texture, render target, buffers), `GLShader`, `GLGpuTimer`. The only code that calls GL |
| `src/Engine/MNVTEngine.Platform/GameWindow.cs` | Creates a GL 4.5 core context (`ContextAPI.OpenGL`), exposes `IGLContextSource GraphicsContext`, calls `SwapBuffers`, has the `DebugContext` option |
| Device factories `(window, log) => GLGraphicsDevice.Create(window.GraphicsContext, ...)` | `GameHost/Program.cs`, `Player/Program.cs`, `CLI/Cli.cs`, `Editor/EditorApp.cs`, `Editor/ProjectHub.cs`, `samples/HelloWorld`, `tests/Runtime.Tests`, `tests/DebugUI.Tests`, `tests/Graphics.OpenGL.Tests` |
| Project references to `Graphics.OpenGL` | GameHost, Player, CLI, Editor, HelloWorld, Runtime.Tests, DebugUI.Tests, Graphics.OpenGL.Tests |
| Shader sources | `BuiltinShaders.Sprite` and user fragment shaders (`samples/SceneDemo/Assets/Shaders/Pulse.frag`): GLSL `#version 450 core` with loose `uniform`s and `in`/`out` without locations |
| GL conventions in the API | Viewport and scissor origin bottom-left; render targets `IsFlippedVertically`; `ReadPixels` flips GL's bottom-up rows; clip space z in [-1, 1] (`Matrix4x4.Ortho`) |
| Options and docs | `--debug-gl`, `GameWindowOptions.DebugContext`; `README.md`, `docs/graphics/rendering.md`, `docs/architecture/overview.md`, `docs/build-system/building.md`, `docs/getting-started.md`, `docs/editor/editor.md`, `docs/engine/scenes-and-runtime.md` |
| Build validator | Comment about `opengl32.dll`; the frame-run check needs a GL 4.5 GPU |
| Architecture tests | Rules naming `Graphics.OpenGL` |

### Existing architecture (what the migration can keep)

- `IGraphicsDevice` is an immediate-mode API: `SetRenderTarget`, `SetViewport`, `Clear`, `Draw(DrawCommand)`, `ReadPixels`, resource factories.
  `DrawCommand` carries material (shader, blend, named uniforms), texture, vertex and index buffer, range, view-projection, topology and scissor.
- Everything above the device is backend-independent and stays unchanged:
  - `SpriteBatch` and `Renderer2D` (batching, sorting), and `Camera2D`;
  - `ResourceCache`: materials, lighting through render targets and blend modes, and the tilemap and sprite render paths;
  - the ImGui renderer (`DebugUI/ImGuiBackend/ImGuiRenderer.cs`) and `DebugDrawRenderer`.
- **ImGui already renders through `IGraphicsDevice`.** `ImDrawVert` matches `SpriteVertex`, clip rects become `DrawCommand.Scissor`, and textures are registered with ids. A Vulkan `IGraphicsDevice` therefore covers the editor, the in-game overlay and debug drawing without a separate ImGui Vulkan renderer. Multiple independent ImGui contexts already exist (editor and overlay).
- Diagnostics consume `GraphicsStatistics`, `IGpuTimer` (asynchronous timestamps) and `QueryGpuMemory()`. The Vulkan backend implements the same interfaces.

## Decisions

| Topic | Decision | Reason |
|---|---|---|
| API level | Vulkan 1.4 core, required. Instance version and device `apiVersion` below 1.4 fail with `GraphicsInitializationException` naming the device and version | Spec. No silent fallback to 1.3 |
| Bindings | Silk.NET.Vulkan 2.23 | Same family as windowing and input; has the 1.4 core entry points |
| Memory | VMA via `Vortice.VulkanMemoryAllocator`, behind one internal class (`GpuAllocator`) that converts handles. `VK_EXT_memory_budget` is enabled when present | Proven suballocator with statistics and budgets; no hand-written allocator to maintain. Cost: a second binding assembly (`Vortice.Vulkan`, 1.6 MB) and native `vma.dll` (Windows only, the only target platform) |
| Rendering | Dynamic rendering (`vkCmdBeginRendering`), Synchronization2 barriers, `vkQueueSubmit2` | 1.3 and 1.4 core; no render pass objects |
| Descriptors | Push descriptors (`vkCmdPushDescriptorSet`, core in 1.4) for the per-draw set: binding 0 texture, 1 vertex uniforms, 2 fragment uniforms | Per-draw data in a 2D batcher changes every draw; push descriptors need no pools, no set lifetime tracking and no per-frame reset |
| Uniforms | Per-draw uniform blocks are written to a per-frame streaming buffer; the block layout comes from SPIR-V reflection | Keeps `Material.SetFloat("uGlow", ...)` by name and `Shader.HasUniform` |
| Shader language | GLSL compiled to SPIR-V 1.6 with shaderc at `CreateShader`, using Vulkan *relaxed rules*: loose `uniform`s go into `gl_DefaultUniformBlock`, and locations and bindings are assigned automatically. Fragment inputs are re-linked to vertex outputs by name, as GL does. The target environment is Vulkan 1.3, because the bundled shaderc has no 1.4 environment (it silently falls back to 1.0). SPIR-V 1.6 is the newest version Vulkan 1.4 consumes. The SPIR-V optimizer is off: it strips the names reflection needs, and drivers optimize anyway | Existing built-in and user shaders (`#version 450 core`) compile unchanged. Verified with `Pulse.frag` |
| Shader caching | SPIR-V cached in memory and on disk by a SHA-256 of the source, stage and compiler options; `VkPipelineCache` persisted to disk | No recompilation on startup; pipelines are never created per frame |
| Pipelines | Created on first use per (shader, blend mode, topology, vertex layout, color format) and cached; viewport and scissor are dynamic state | Five blend modes and two formats keep the count small |
| Frames in flight | 2. Each frame slot has a command pool, a fence, an acquire semaphore, a streaming buffer, timestamp queries and a deferred-destruction list | No per-frame CPU/GPU wait beyond the fence of the frame two back |
| Present semaphores | One "render finished" semaphore per swapchain image | Required to reuse semaphores safely with `vkQueuePresentKHR` |
| Frame boundary | New `IGraphicsDevice.Present()`, called by hosts at the end of `Render`. The frame begins implicitly with the first command. `Present` then waits for the next frame slot (the frame two back), like a GL buffer swap blocked | GL swapped in `GameWindow`; with Vulkan the device owns presentation. Waiting at the start of the next frame instead would count VSync and GPU time as CPU time in diagnostics |
| Coordinate conventions | Negative-height viewport, so clip space stays y-up like GL. Render targets are no longer flipped (`IsFlippedVertically = false`). Viewport and scissor stay bottom-left in the public API and are converted internally. Clip z of `Matrix4x4.Ortho` at z = 0 is inside Vulkan's [0, 1] | Engine-facing API and math unchanged |
| Uploads | Staging buffers recorded into the frame's upload command buffer, submitted before the frame's draws; staging freed by deferred destruction | Correct ordering without extra queue waits. A transfer queue is not used: uploads are small and rare in a 2D engine |
| Readback | `ReadPixels` submits the work so far, waits for that submission only and continues the frame in a new command buffer | Used by tests and screenshots, never in the normal frame loop |
| Validation | Enabled by default in Debug builds of the engine when `VK_LAYER_KHRONOS_validation` is present; `--gpu-validation` / `MNVT_VULKAN_VALIDATION=1` force it, `=0` disables it. Messages go to `ILogger` with counts exposed for diagnostics and tests | Never required in production |
| Headless device | `VulkanGraphicsDevice.CreateHeadless` renders into an offscreen back buffer | GPU tests without a window or swapchain |
| GL removal | After all hosts, tests and samples run on Vulkan and pass with validation | Spec Phase 6 |

## Synchronization model

- **Frame slots.** One graphics queue; frames alternate between 2 slots. Starting a frame waits for the slot's fence (signalled by the submit two frames ago). It then resets the slot's command pool, streaming buffer and query pool, and runs the slot's deferred destructions.
- **Swapchain.** The swapchain image is acquired lazily when the frame first targets the back buffer, with the slot's acquire semaphore. The first submit that renders to it waits on that semaphore at `COLOR_ATTACHMENT_OUTPUT`. The final submit signals the image's render-finished semaphore, which `vkQueuePresentKHR` waits on.
- **Image layouts.** The device tracks each image's current layout. Before use it records one `vkCmdPipelineBarrier2` transition:
  - color attachment for drawing;
  - shader-read-only for sampling;
  - transfer source or destination for copies;
  - present for the back buffer.
  Rendering into a render target and then sampling it in the same frame (lighting, editor views) is handled by these transitions.
- **Destruction.** `Dispose` of a resource never destroys a Vulkan object directly. It queues the handle tagged with the frame being recorded, or the next one between frames (a staging buffer is queued before its copy is recorded). Frame F starts by waiting for frame F-2, then destroys everything tagged F-2 or older. Device disposal waits for the device to be idle, runs every queue, reports leaks and destroys objects in reverse creation order.
- **Swapchain recreation** happens after `VK_ERROR_OUT_OF_DATE_KHR`, after `VK_SUBOPTIMAL_KHR`, and on resize. It waits for the device to be idle (rare), passes the old swapchain to `oldSwapchain` and destroys it afterwards. A minimized window (size 0) skips the frame.
- **GPU timing.** Timestamps are written into the slot's query pool. Results are read with `vkGetQueryPoolResults` without `WAIT`, after the slot's fence has signalled. A frame whose results are not available is counted as dropped.

## Phases

| Phase | Deliverable | Status |
|---|---|---|
| 0 | This audit and plan | done |
| 1 | Instance, validation, device selection, queues, surface, swapchain, frame slots: a window that clears and presents | done |
| 2 | VMA, buffers, textures, samplers, mipmaps, deferred destruction, readback | done |
| 3 | shaderc and reflection, pipelines and cache, push descriptors, streaming buffers, full `IGraphicsDevice`; GL rendering tests ported | done (`tests/MNVTEngine.Graphics.Vulkan.Tests`, 32 tests, no validation errors) |
| 4 | Hosts on Vulkan: GameHost, Player, CLI, editor, Project Hub, HelloWorld; ImGui and the debug overlay verified | done: Platformer, SceneDemo, HelloWorld, the editor in Play Mode and Debug/Release exports run without validation messages |
| 5 | GPU timestamps, memory budget and allocator statistics, validation counts, pipeline and descriptor statistics in diagnostics | done |
| 6 | OpenGL backend, package and docs removed | done: `Graphics.OpenGL` and its tests deleted, `Silk.NET.OpenGL` removed, `GameWindow` is Vulkan-only (`GraphicsValidation` replaces `DebugContext`), docs updated. 567 tests pass |

## Risks

| Risk | Mitigation |
|---|---|
| No validation layer on machines without the SDK | Validation is optional; the device logs that it is unavailable. Developers install the LunarG SDK |
| VMA through a second binding (Vortice) | Confined to `GpuAllocator`; handles are plain `ulong`/`nint` in both bindings. If it becomes a problem, VMA can be replaced behind the same class |
| Relaxed GLSL rules differ subtly from GL (e.g. uniform arrays, `sampler2D` other than binding 0) | Reflection rejects unsupported uniform types with a `ShaderCompilationException` instead of drawing wrongly |
| Swapchain behaviour on hidden windows (tests) | GPU tests use the headless device; window tests only check presentation |
| Native size of exported games grows (`shaderc_shared.dll` 7 MB, `vma.dll`) | Resolved: shaders are precompiled at export time and games no longer ship shaderc ([rendering](rendering.md)) |
| Driver differences in present modes and formats | Chosen from what the surface reports; FIFO (always available) when VSync is on |

## Diagnostics (Phase 5)

| Data | Source | Status in diagnostics |
|---|---|---|
| GPU frame and scope time | Timestamp queries per frame slot, read after the slot's fence, without WAIT | Measured; dropped frames counted. Unsupported when the graphics queue has no timestamp bits |
| GPU memory used / total | `VK_EXT_memory_budget`: device-local heap sizes, and this process's budget minus its usage | Measured; Unsupported without the extension. No GPU stall (unlike the GL vendor queries) |
| GPU allocated, allocations, failures, buffer memory | VMA statistics, counted by the engine | Measured (exact). Unsupported on backends without an allocator |
| Texture memory | Allocation sizes of textures and render targets | Measured on Vulkan; `Estimated` (shown as `~1.2 MB`) from width x height x 4 when the device has no allocator |
| Pipeline switches, descriptor writes, barriers, submits | Counted by the device per frame (`GraphicsStatistics`) | Measured |
| Backend facts | `IGraphicsDevice.DescribeBackend()`: API and instance version, GPU, driver, swapchain, frames in flight, validation counts and last error, pipelines created and cache, streaming memory, memory budget source, timer | Shown in the Renderer window and the `renderer` console command |
| Validation messages | Debug messenger to `ILogger` (event 1102), counted. Loader messages about third-party layers (overlays, capture tools) are logged as warnings and not counted as errors | Console and log |

`MetricStatus.Estimated` was added: estimated values are budgeted like measured ones, but always displayed with `~`.