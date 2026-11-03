# 2D rendering

## Layers

| Project | Role |
|---|---|
| `MNVTEngine.Graphics` | Backend-independent API: `IGraphicsDevice`, resources, `Material`, `SpriteBatch`, `Renderer2D`, `Camera2D`, `Image` decoding (StbImageSharp). No OpenGL types. |
| `MNVTEngine.Graphics.OpenGL` | `GLGraphicsDevice`: OpenGL 4.5 core with direct state access (DSA), via Silk.NET. |
| `MNVTEngine.Platform` | `GameWindow` (GLFW window + GL context), `InputState`, frame loop. Knows nothing about Graphics. |

The host (sample, later the Runtime/editor) wires them together:
`GLGraphicsDevice.Create(window.GraphicsContext, width, height, logger)`.
Graphics and Platform may not reference each other. Architecture tests enforce this.

## Frame loop (`GameWindow.Run`)

1. `InputState.BeginFrame()` clears per-frame edges, then window events are pumped.
2. `GameClock.Advance(elapsed)`: long frames are capped at 0.25 s, then time scale and pause are applied.
3. `FixedTimestep.Accumulate` runs zero or more `FixedUpdate(step)` calls (default 60 Hz, max 8 steps per frame).
4. `Update(FrameTime)`, then `Render(FrameTime, interpolation)`, then swap buffers.

When the window is minimized, rendering is skipped and the loop sleeps. Exceptions from game callbacks are not caught: `Unload` still runs, then the exception propagates.

## Resource ownership

- Every GPU object derives from `GraphicsResource`. Whoever creates a resource disposes it. `Dispose` is idempotent.
- There are no finalizers. GL objects can only be deleted on the context thread.
- `GraphicsResourceTracker` (`device.Resources`) knows every live resource. It throws if a resource is created or disposed on another thread.
- `device.Dispose()` releases anything still alive and logs each one as a leak (event 1001).
- Objects are labelled with `glObjectLabel`, so RenderDoc shows their debug names.

## Coordinates and textures

- World space is Y-up. `Camera2D.OrthographicSize` is half the visible height at zoom 1. `UsePixelUnits()` makes 1 unit equal 1 pixel.
- Screen space (mouse, `ScreenToWorld`) is pixels with the origin at the top-left.
- Uploaded images store row 0 at the top. Render targets store row 0 at the bottom (`IsFlippedVertically`). `SpriteBatch` picks texture coordinates so that both appear upright. Source rectangles are in texels with a top-left origin.
- `ReadPixels` always returns rows top-down.

## SpriteBatch

- `Begin(viewProjection, sortMode, material)` / `Draw...` / `End()`.
- Sprites are buffered on the CPU and sorted in `End`. Vertex data is uploaded once per chunk of 2048 sprites, using buffer orphaning. Each run of consecutive sprites with the same texture becomes one indexed draw.
- Sort modes:
  - `Deferred`: submission order.
  - `Texture`: grouped by texture in order of first use. Use only for content where draw order doesn't matter.
  - `BackToFront` / `FrontToBack`: by `depth`, where higher means farther. All sorts are stable.
- One material per `Begin`. To switch blend mode or shader, call `End` and `Begin` again.
- Debug primitives (`DrawLine`, `DrawRectangle`, `FillRectangle`, `DrawCircle`) draw quads with `WhitePixel`, so they batch together with sprites.

## Materials and shaders

- `Material` = `Shader` + `BlendMode` (Opaque, Alpha, Premultiplied, Additive, Multiply) + named uniforms.
- Shaders are GLSL 4.50 core for now (`ShaderSource`). Two names are reserved: `uViewProjection` and `uTexture` (binding 0).
- Compile and link failures throw `ShaderCompilationException`, which carries the stage and the driver log.

## Scissor and dynamic indices

- `DrawCommand.Scissor` clips a draw to a pixel rectangle of the current target, origin bottom-left. `Clear` ignores it and always clears the whole target.
- `CreateDynamicIndexBuffer` + `IndexBuffer.SetData` support geometry rebuilt every frame (used by the editor UI). Immutable index buffers reject `SetData`.

## Diagnostics

`GameWindowOptions.DebugContext = true` creates a debug GL context. Driver messages are routed to `ILogger` (event 1002): high severity is logged as Error and medium as Warning.

## Platform requirements

The target needs a GPU and driver with OpenGL 4.5. GLFW native binaries come with the Silk.NET packages.

## Tests

- `MNVTEngine.Graphics.Tests` uses a fake device. It covers batching, sorting, chunking, geometry and texture coordinates, the camera, resource tracking and image decoding.
- `MNVTEngine.Graphics.OpenGL.Tests` renders in a hidden window and reads pixels back. It covers orientation, blending, sorting, render targets, shader errors and leak reporting. These tests are **skipped**, not passed, when no GL 4.5 context can be created.
