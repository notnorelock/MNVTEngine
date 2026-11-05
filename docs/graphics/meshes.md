# Meshes and 3D cameras

Part of the [3D plan](3d-plan.md). Conventions: right-handed, +Y up, objects and cameras face -Z, counter-clockwise
front faces (as in glTF; see [mathematics](../engine/mathematics.md)).

## Mesh (`MNVTEngine.Graphics.Meshes`)

`Mesh` is triangle data in CPU memory:

- **Streams.** Positions (required); normals, tangents (`Vector4`, W = bitangent sign, as in glTF), `UV0`, `UV1` and colors (optional).
- **Indices and submeshes.** 32-bit indices in a triangle list, and submeshes (index ranges, one material each). `SetIndices(indices)` without submeshes makes one submesh covering everything.
- **Bounds.** `Bounds` is recomputed whenever data changes.
- **Version.** Assigning a stream or calling `SetIndices` bumps `Version`. After editing arrays in place, call `MarkChanged()`.
- **`Validate()`.** Lists every problem: stream lengths, index count not a multiple of 3, indices out of range, submeshes outside the indices, NaN positions. An invalid mesh is never uploaded.
- **`RecalculateNormals()`.** Area-weighted smooth normals.
- **`RecalculateTangents()`.** Lengyel's method from UV0, orthogonalized against the normal, with mirrored UVs giving W = -1. It needs normals and UV0.
- **`IndexFormat`.** 16-bit when every index fits (up to 65,536 vertices), otherwise 32-bit.

## Primitives

`MeshPrimitives` builds `Cube`, `Plane` (XZ, facing +Y, subdivided), `Quad` (XY, facing +Z), `Sphere` (UV sphere),
`Cylinder`, `Capsule` (total height along Y) and `Cone` (base at -height/2).

- **Shape.** All are centered on the origin, with outward counter-clockwise faces.
- **Vertex data.** Unit normals, UVs in [0, 1] and tangents. Seams and hard edges duplicate vertices.
- **Tests.** For each shape, the tests check:
  - bounds and validity;
  - that the signed volume matches the analytic volume (outward winding);
  - that each face agrees with its normals;
  - that normals and tangents are unit length.

## GPU upload

- **`MeshVertex`.** The interleaved upload format (72 bytes), with shader locations: 0 position, 1 normal, 2 tangent, 3 uv0, 4 uv1, 5 color. Missing streams get neutral values: normal +Z, tangent +X, UVs 0, white.
- **`GpuMesh.Upload(device, mesh)`.** Creates one **static** vertex buffer and one immutable index buffer, 16- or 32-bit. Both are device-local, filled by a staging copy, and drawable in any later frame.
- **`GpuMeshCache`.** Keeps one GPU copy per `Mesh` instance:
  - Renderers sharing a mesh share its buffers.
  - A mesh whose `Version` changed is uploaded again. The old buffers are released through the device's deferred destruction, so frames in flight still read valid memory.
  - Disposing the cache releases every upload. `SizeInBytes` reports GPU memory.
- **Device API.** `IGraphicsDevice` gained `CreateStaticVertexBuffer(layout, bytes)` and `CreateIndexBuffer(ReadOnlySpan<uint>)`. `IndexBuffer.Format` selects the Vulkan index type at draw time. Dynamic buffers (sprites, UI) keep the per-frame `SetData` contract; static buffers reject `SetData`.

## Camera

`Camera` serves both 2D and 3D:

- **`Projection`.** `Orthographic` (default; `OrthographicSize` is half the visible height) or `Perspective` (vertical `FieldOfView` in degrees). It is saved with `NearClip` and `FarClip`.
- **Matrices.** `ProjectionMatrix` uses Vulkan's depth range: 0 at the near plane, 1 at the far plane. `ViewMatrix` is the inverse of the camera's position and rotation; scale is ignored. `ViewProjectionMatrix` combines them, and `CalculateFrustum()` gives the frustum for culling.
- **Picking.** `ScreenPointToRay(pixel)` starts on the near plane. `WorldToScreenPoint(world)` returns pixels (origin top-left) and the distance in front of the camera.
- **2D.** 2D rendering still uses the orthographic size and the camera's XY position and angle (`ScreenToWorld`).

The GL-style `Matrix4x4.Ortho`/`Perspective` put z = 0 exactly on the edge of Vulkan's depth range, where any z noise clips
half of a rotated mesh (the GPU mesh test covers this case). Every engine renderer therefore uses the ZeroToOne projections:
2D, UI and the debug overlay put z = 0 at depth 0.5.

## 3D rendering

- **Depth buffers.**
  - The back buffer always has one, sized with it. Render targets get one with `CreateRenderTarget(..., depth: true)`; the editor's views have one.
  - The format is D32_SFLOAT, or the first supported of D32_SFLOAT_S8 and D24_S8.
  - `ClearDepth()` clears to 1, the farthest value.
  - Every pass that uses a depth buffer starts with a barrier on it, so a depth buffer reused by frames in flight is correctly ordered (clean under synchronization validation).
- **Draw state.**
  - `DrawCommand.State` (`RenderState`) holds the depth test (less-or-equal), the depth write and the cull mode. The default, everything off, is what 2D drawing uses.
  - `RenderState.Opaque3D` tests and writes depth and culls back faces; `Transparent3D` tests depth without writing it.
  - Front faces are counter-clockwise on screen.
  - Drawing with depth state into a target without depth throws.
- **Per-draw data.** `DrawCommand.World` reaches shaders as `uModel`, and its inverse transpose as `uNormalMatrix`; both are names reserved for the renderer. Pipelines feed only the vertex attributes a shader reads.
- **GPU instancing.** `Renderer3D.InstancedVariant` turns on instanced draws. Opaque items that share a mesh, submesh, material and culling, and are not GPU-skinned, are drawn together with one instanced draw per 64 copies. The draw goes where the nearest of them is in the draw order.
  - `DrawCommand.Instances` reach shaders as `mat4 uInstances[64]`, indexed by `gl_InstanceIndex`. The instanced Standard and Unlit variants use it in place of `uModel`, with normals by its inverse transpose.
  - The 3D pass instances built-in Standard and Unlit materials (`SceneRenderer.GpuInstancing`, `GameRuntimeOptions.GpuInstancing`). Renderers of a shared `.mnvtmat` or of a model's materials qualify. Primitives with their own color each have their own built-in material, and so do renderers with material overrides; they draw one by one.
  - Shadow maps still draw casters one by one.
  - `Render3DStatistics.DrawCalls` and `InstancedItems` count draws and instanced items.
- **GPU skinning.** `GpuMesh.UploadSkinned` adds four joints and weights per vertex (`SkinnedMeshVertex`, 104 bytes, locations 6 and 7). `DrawCommand.Joints` (and `MeshDrawItem.Joints`) reach shaders as the `mat4 uJoints[128]` array, which shader reflection reads with its length and stride. Per-stage uniform blocks hold up to 16 KB.
- **`Renderer3D`** (backend-independent):
  - frustum-culls each `MeshDrawItem` by its world-space bounds;
  - draws opaque items front to back;
  - then draws transparent items back to front.
  - It returns statistics: visible, culled, draw calls and triangles.
- **Built-in shaders.** Materials use the Standard (lit PBR, see [3D lighting](materials-and-lighting.md#3d-lighting)) or Unlit shader. `BuiltinShaders.Unlit3D` draws texture (UV0) × vertex color × `uColor`.

### MeshRenderer

`MeshRenderer` draws a mesh at its GameObject's transform, through the scene's main camera, in a 3D pass before sprites:

- **Mesh.** `Mesh` is a mesh of an imported model (`"asset:<guid>#mesh/<name>"`, see [models](../engine/models.md)); it draws one item per submesh, with the model's material of each submesh. Otherwise `Primitive` is one of cube, sphere, capsule, cylinder, cone, plane or quad.
- **Built-in material.** `Color` and an optional `Texture` use the unlit material; a color with alpha below 1 is drawn as transparent.
- **Custom material.** `Material` (`.mnvtmat`) takes a custom shader instead. The shader must read the mesh layout and `uViewProjection`/`uModel`; its blend mode decides transparency.
- **`DoubleSided`.** Turns culling off.
- **Sharing.** Primitive meshes are generated once and uploaded once per renderer. Model meshes are uploaded on first use and released when the model is reimported.
- **Statistics.** The runtime's `--frames` exit log reports meshes drawn and culled; profiler counters are "Meshes drawn" and "Meshes culled".
- **Editor.** Play Mode's Game view shows the 3D pass. The editor's Scene view is still 2D: the 3D scene view and gizmos are Phase 9.

Sample: `samples/Showcase3D`, a perspective camera over a checkered floor with every primitive, spinning objects (`Scripts/Spinner.cs`), a transparent cube, a cube behind the camera that is culled, the same wooden crate imported from glTF and from FBX (Poly Haven, CC0), and two animated Khronos models: the skinned CesiumMan walking and BoxAnimated (CC-BY).
