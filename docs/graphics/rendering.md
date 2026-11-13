# 2D rendering

## Layers

| Project | Role |
|---|---|
| `MNVTEngine.Graphics` | Backend-independent API: `IGraphicsDevice`, resources, `Material`, `SpriteBatch`, `Renderer2D`, `Camera2D`, `Image` decoding (StbImageSharp). No Vulkan types. |
| `MNVTEngine.Graphics.Vulkan` | `VulkanGraphicsDevice`: Vulkan 1.4 (dynamic rendering, synchronization2, push descriptors) via Silk.NET, GPU memory through VMA, GLSL to SPIR-V with shaderc. |
| `MNVTEngine.Platform` | `GameWindow` (GLFW window that exposes a Vulkan surface source), `InputState`, frame loop. Knows nothing about Graphics. |

The host (runtime, editor, samples) wires them together:
`VulkanGraphicsDevice.Create(window.VulkanSurface, width, height, logger, VulkanDeviceOptions.ForApplication(name, vsync, validation))`.
Graphics and Platform may not reference each other, and gameplay code never sees Vulkan types. Architecture tests enforce this.
Design decisions, the synchronization model and the migration from OpenGL are in [the Vulkan migration document](vulkan-migration.md).

## Frame loop (`GameWindow.Run`)

1. `InputState.BeginFrame()` clears per-frame edges, then window events are pumped.
2. `GameClock.Advance(elapsed)`: long frames are capped at 0.25 s, then time scale and pause are applied.
3. `FixedTimestep.Accumulate` runs zero or more `FixedUpdate(step)` calls (default 60 Hz, max 8 steps per frame).
4. `Update(FrameTime)`, then `Render(FrameTime, interpolation)`. `Render` ends with `device.Present()`.

`Present` submits the frame and presents it, then waits until the frame two back has finished on the GPU (2 frames in flight). With VSync the wait is where the frame rate is limited, as a GL buffer swap was, so it is not counted as CPU time. When the window is minimized, rendering is skipped and the loop sleeps. Exceptions from game callbacks are not caught: `Unload` still runs, then the exception propagates.

## Resource ownership

- Every GPU object derives from `GraphicsResource`. Whoever creates a resource disposes it. `Dispose` is idempotent.
- There are no finalizers. `Dispose` never destroys a Vulkan object directly. The object is queued and destroyed once the frames that may use it have completed on the GPU.
- `GraphicsResourceTracker` (`device.Resources`) knows every live resource. It throws if a resource is created or disposed on another thread.
- `device.Dispose()` waits for the GPU, releases anything still alive and logs each one as a leak (event 1001).
- Images, buffers, pipelines and shader modules get debug names (`VK_EXT_debug_utils`), so validation messages and RenderDoc show them.

## Coordinates and textures

- World space is Y-up. `Camera2D.OrthographicSize` is half the visible height at zoom 1. `UsePixelUnits()` makes 1 unit equal 1 pixel.
- Screen space (mouse, `ScreenToWorld`) is pixels with the origin at the top-left.
- The device uses a negative-height viewport, so clip space is y-up as in `Matrix4x4.Ortho`. Viewport and scissor rectangles in the API have their origin at the bottom-left of the target.
- Uploaded images and render targets both store row 0 at the top (`IsFlippedVertically` is false). `SpriteBatch` honours the flag for any backend that sets it. Source rectangles are in texels with a top-left origin.
- `ReadPixels` returns RGBA rows top-down. It waits for the work recorded so far; it is meant for tests and screenshots.

## SpriteBatch

- `Begin(viewProjection, sortMode, material)` / `Draw...` / `End()`.
- Sprites are buffered on the CPU and sorted in `End`. Vertex data is uploaded once per chunk of 2048 sprites into the frame's streaming memory. Each run of consecutive sprites with the same texture becomes one indexed draw.
- Sort modes:
  - `Deferred`: submission order.
  - `Texture`: grouped by texture in order of first use. Use only for content where draw order doesn't matter.
  - `BackToFront` / `FrontToBack`: by `depth`, where higher means farther. All sorts are stable.
- One material per `Begin`. To switch blend mode or shader, call `End` and `Begin` again.
- Debug primitives (`DrawLine`, `DrawRectangle`, `FillRectangle`, `DrawCircle`) draw quads with `WhitePixel`, so they batch together with sprites.

## Materials and shaders

- `Material` = `Shader` + `BlendMode` (Opaque, Alpha, Premultiplied, Additive, Multiply) + named uniforms.
- Shaders are GLSL `#version 450 core` written GL-style: loose `uniform`s, and `in`/`out` without locations. shaderc compiles them to SPIR-V under Vulkan relaxed rules.
- **Shader compilation is a tool.** shaderc lives in `MNVTEngine.Graphics.Vulkan.ShaderCompiler` (`ShadercCompiler`, `options.WithShaderc()`), which the editor, Player, CLI, build and tests pass to the device. Exported games have no compiler: the build precompiles the sprite shader, the built-in shaders of the project's modules (`IResourceIntegration.BuiltinShaders`) and every material's shader into the default resources, and a shader missing from them fails with `ShaderCompilationException` ("not precompiled"). The validator rejects shaderc in a package (MNVB507).
  - Fragment inputs are linked to vertex outputs by name.
  - Material uniforms are matched by name through SPIR-V reflection.
  - Two names are reserved: `uViewProjection` and `uTexture` (binding 0, the only texture). Supported uniform types are `float`, `int`, `vec2`, `vec4` and `mat4`. Other types compile but cannot be set (they read as 0).
  - Storage buffers, push constants and extra samplers are rejected.
- Compile failures throw `ShaderCompilationException` with stage `vertex` or `fragment` and the compiler log. Linking and resource problems use stage `link`.
- Exported games ship every shader precompiled to SPIR-V in `defaultresources.mnvtpack` (`VulkanShaderPrecompiler` at build time, `VulkanDeviceOptions.PrecompiledShaders` at run time). In development, compiled SPIR-V is cached on disk by a hash of the source. Pipelines are created once per shader, blend mode, topology, vertex layout and target format, and the driver's pipeline cache is saved between runs. Both caches live in `%LOCALAPPDATA%\MNVTEngine\GpuCache\<application>` and are safe to delete.

## Scissor and dynamic buffers

- `DrawCommand.Scissor` clips a draw to a pixel rectangle of the current target, origin bottom-left. `Clear` ignores it and always clears the whole target.
- Vertex buffers and dynamic index buffers (`CreateDynamicIndexBuffer` + `SetData`) stream through per-frame memory. Data must be set in the frame it is drawn, and drawing data from an earlier frame throws. Immutable index buffers live in device-local memory and reject `SetData`.

## Validation and diagnostics

- The Khronos validation layer is used when it is installed (the LunarG Vulkan SDK) and requested:
  - on by default in Debug builds of the engine;
  - forced with `--gpu-validation` (`GameWindowOptions.GraphicsValidation`) or `MNVT_VULKAN_VALIDATION=1`, and turned off with `=0`.
  Messages are logged (event 1102) and counted (`device.Validation`). Loader messages about third-party overlay layers are logged as warnings only.
- `MNVT_VULKAN_DEVICE=<part of a name>` picks a GPU. Otherwise a discrete GPU wins, then the one with more device-local memory, among devices that meet the requirements.
- GPU timing (timestamp queries), allocator statistics, memory budget, pipeline and descriptor counts, and backend facts feed the [diagnostics](../engine/diagnostics.md).

## Platform requirements

The target needs a GPU and driver with **Vulkan 1.4**: dynamic rendering, synchronization2, push descriptors and host query reset, plus `VK_KHR_swapchain`. Without one, the device throws `GraphicsInitializationException` with the reason. Games then exit with code 4, and the editor prints the reason. GLFW, shaderc and VMA native binaries come with the NuGet packages.

## Tests

- `MNVTEngine.Graphics.Tests` uses a fake device. It covers batching, sorting, chunking, geometry and texture coordinates, the camera, resource tracking and image decoding.
- `MNVTEngine.Graphics.Vulkan.Tests` covers two kinds of tests:
  - **Unit tests, no GPU needed:** device selection, version rules, swapchain choices, SPIR-V reflection and linking, the shader cache.
  - **Rendering on a headless device**, reading pixels back: orientation, blending, sorting, render targets, scissor, uniforms, mipmaps, frames in flight, deferred destruction, caches, statistics, GPU timing, memory, leak reporting, and a real window's swapchain.
  The rendering tests request the validation layer and fail on any validation error. They are **skipped**, not passed, without a Vulkan 1.4 device.
