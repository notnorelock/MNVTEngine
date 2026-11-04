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

The GL-style `Matrix4x4.Ortho`/`Perspective` put z = 0 exactly on the edge of Vulkan's depth range. That is fine for flat 2D
content, but any z noise clips half of a rotated mesh, which is why 3D uses the ZeroToOne projections. The GPU mesh test covers
this case.
