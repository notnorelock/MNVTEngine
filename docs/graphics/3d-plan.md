# 3D engine and model pipeline: audit and plan (Phase 0)

Audit date: 2026-11-04. This is the plan for the "Complete 3D Engine, FBX/GLB Import Pipeline & Unity-Style Asset
Workflow" specification. It records what exists, what is missing, the library choices with their evidence, and the
order of work. Sections are updated as phases land.

## 1. Existing architecture

| Area | State | Relevance to 3D |
|---|---|---|
| Mathematics | `Vector2/3/4`, `Quaternion` (Euler, AngleAxis, Slerp), `Matrix4x4` (TRS, inverse, Ortho, Perspective), `Bounds`, `Ray`, `Rect`, `Color`, `Mathf`. Column-vector convention, column-major storage, right-handed, Y up, camera looks down -Z (`docs/engine/mathematics.md`). | Most of the 3D math exists. Missing: `Plane`, `Frustum`, `BoundingSphere`, `LookAt`, `Transpose`, ray intersection tests, and a 0..1 depth projection for Vulkan. |
| Transform | 2D: `Vector2` position, `float` rotation (Z), `Vector2` scale; a 4x4 local-to-world matrix; parenting; scene format stores `{x, y}` and a float. | Must become a full 3D transform without breaking 2D games, scene files or the editor (see decision D1). |
| Graphics abstraction | `IGraphicsDevice` with one `DrawCommand` = material + **one texture** + vertex/index buffer (**16-bit indices**) + view-projection matrix + scissor. Render targets have **no depth**. Materials are shader + uniforms + blend mode. | Built for sprites. 3D needs depth attachments, depth/cull state, 32-bit indices, static GPU meshes, per-object data, several textures per material, shadow-map (depth) textures, and uniform data larger than a few values. |
| Vulkan backend | Vulkan 1.4, dynamic rendering, synchronization2, VMA, push descriptors, 2 frames in flight, deferred destruction, GPU timestamps, shaderc at runtime + SPIR-V precompiled in exports, SPIR-V reflection. | Reusable as is; pipeline state gains depth/cull/vertex formats; descriptor layout gains more texture slots; a depth image type is added. |
| 2D rendering | `SpriteBatch`, `Renderer2D`, `SceneRenderer` (sprites, tilemaps, 2D lights via a light buffer), `.mnvtmat` materials (shader + uniforms). | Stays. The 3D renderer is a separate pass in the same frame, before 2D overlays and UI. |
| ECS / scenes | Hybrid ECS (`World`, `ComponentPool`, `Query`, `GameSystem`), GameObjects with components, scene JSON schema 2, prefabs, `SceneSerializer` with a component registry. | Reused. New components: `MeshFilter`/`MeshRenderer`, `Light`, 3D camera settings, `Animator`, skinned renderer. |
| Assets | `.meta` GUIDs, SHA-256 source hash + settings hash + importer version, `Library/AssetDatabase.json` cache, importer settings normalization, dependency tracking, moves keep GUIDs, content archives in exports. | The right base. Missing: **sub-assets** (meshes, materials, clips inside one model), import **artifacts** (processed binary data in `Library/`), staged imports that keep the last good result, background import. |
| Editor | ImGui editor: Hierarchy, Inspector (reflection), Project panel, Asset inspector, Scene view (2D camera, 2D gizmos), Game view, UI Editor, Script Editor, undo by snapshots. | Reused. Additions: model import inspector, material inspector, model preview, 3D scene navigation and gizmos. |
| Physics | Box2D (`Physics2D`). | Untouched. 3D physics is a separate module (Phase 8). |
| Diagnostics | Metrics registry with providers, overlay windows, budgets, GPU timer with async timestamp queries. | New providers for 3D rendering and imports. |
| Build | Content compiler, archives, precompiled SPIR-V, build validator. | Import artifacts and generated sub-assets must be packed. |

## 2. Reusable systems

The GUID/meta asset database, importer settings and hashes, the dependency graph, the Vulkan device (memory, sync,
deferred destruction, shader compilation, reflection), materials as `.mnvtmat` JSON, the scene serializer and
component registry, the editor's inspector and asset panels, diagnostics providers, the content archive build
pipeline, and the UI/2D renderers (drawn after 3D).

## 3. Missing systems

- **Mathematics:** plane, frustum, bounding sphere, look-at, Vulkan depth projections, and intersection tests.
- **Transforms:** 3D transform with cached world matrices.
- **Meshes:** mesh assets (vertex streams, submeshes, 32-bit indices, bounds) and primitive generators.
- **Rendering:**
  - depth buffers;
  - a 3D renderer with render queues, frustum culling, per-object data and multiple cameras;
  - a PBR material model, lights and shadow maps.
- **Import pipeline:**
  - sub-assets and import artifacts;
  - FBX and glTF importers;
  - model import settings;
  - material and texture extraction, remapping, and staged and background imports.
- **Animation:** skeletons, skinning, clips, `Animator`, and morph targets.
- **Editor:** model import inspector, material inspector, model preview, and 3D scene view with gizmos and picking.
- **Physics:** 3D physics.

## Decisions (2026-11-04)

- **D1, Transform API: Unity-style, fully 3D.**
  - `Position`, `LocalPosition`, `LocalScale` and `LossyScale` become `Vector3`; `Rotation` and `LocalRotation` become `Quaternion`.
  - 2D convenience: `Rotation2D` and `LocalRotation2D`, in degrees about Z.
  - Existing scripts, samples and templates are migrated in the same change. Scene files keep loading: the 2D form of positions and rotations is still read.
- **D2, FBX: Assimp now, ufbx later.** Assimp (`Silk.NET.Assimp`) sits behind the engine's own model reader interface. A ufbx reader is a later phase, if production files need its fidelity.

## 4. FBX importer: evaluation

| Option | License | Delivery | Maintenance | FBX coverage | Integration cost |
|---|---|---|---|---|---|
| **Assimp 6.0.2** via `Silk.NET.Assimp` 2.23.0 (+ `Ultz.Native.Assimp` 6.0.2) | Assimp BSD-3, Silk.NET MIT | NuGet with prebuilt native binaries (win-x64, linux, osx). Same Silk.NET version as the Vulkan bindings. | Silk.NET 2.23.0 published 2026-01-23; Assimp actively released. | Binary and ASCII FBX 7.x (2011+), meshes, multiple materials, UV sets, normals/tangents, vertex colors, hierarchy, embedded textures, skinning and bind poses, animation, blend shapes (partial), cameras, lights. Material conversion is Phong-centric; some PBR vendor properties are kept only as raw properties. | Low: managed bindings exist. |
| **ufbx** (single C file) | MIT or Unlicense | Source only. A native DLL must be built (VS C++ tools are installed here) and a C shim plus P/Invoke bindings written; no .NET bindings exist. | Active (2.7k commits). | The most complete FBX reader: FBX 3000+, binary and ASCII, unified PBR material from vendor materials, skinning, blend shapes, animation layers, embedded media. | High: native build in the repo, a flattening shim, and packaging per platform. |
| Autodesk FBX SDK | Proprietary, redistribution restricted | Native C++ SDK; no maintained .NET binding (FbxSharp.Native last updated 2022) | Autodesk | Reference implementation | Licensing makes it unsuitable for shipping with an open engine |
| FbxSharp (managed) | — | NuGet, .NET Framework | Stale | Partial | Not viable |

**Recommendation:** Assimp through `Silk.NET.Assimp`, behind an engine-owned `IModelReader` interface. It ships
working natives today, matches the existing Silk.NET dependency, and covers the mandatory FBX features. Its known weak
spots (vendor PBR materials, some blend shape cases) are reported as import diagnostics. The interface leaves room for a ufbx-based reader
later, if production files need it, without changing the pipeline.

## 5. glTF importer

**SharpGLTF.Core** (MIT, pure managed, maintained; 1.0.6 published 2025-12-30): exact glTF 2.0 semantics. It supports
PBR metallic-roughness, KHR texture transforms, samplers, alpha modes, skins, morph targets, animations, embedded and
external buffers, and GLB. Assimp also reads glTF, but SharpGLTF keeps the material model exact. Both readers produce the same
engine-side `ImportedModel`, so everything after parsing is shared.

## 6. Native dependencies and licensing

- **Assimp native library** (BSD-3): packaged by `Ultz.Native.Assimp` per runtime identifier.
  - The export pipeline copies it into `Dependencies/Native` only for games that import models at **edit time**. Exported games read processed artifacts and never need Assimp.
  - The build validator checks that release exports do not contain it.
  - The license text is added to third-party notices.
- **SharpGLTF:** managed, MIT. It is also edit-time only.
- **3D physics** (Phase 8): candidate JoltPhysicsSharp (MIT, prebuilt natives). It is evaluated then.

## 7. Material and texture extraction

- **Imported materials.** Source materials are converted to an engine PBR description:
  - base color, metallic, roughness and emission factors, alpha mode and cutoff, double-sided;
  - texture slots, with role detection from explicit material connections first and file names only as a fallback, marked as a guess.
  - These live as **sub-assets** of the model (read-only, regenerated on reimport).
- **Extract Materials** writes real `.mnvtmat` files with deterministic names (`<Model>_<Material>.mnvtmat`) into a chosen folder. It records the mapping `source material name -> material GUID` in the model's meta. Reimports reuse the mapping, so no duplicates are created.
- **Remap** replaces a source material with any existing `.mnvtmat`. **Reset** clears the remaps. Remaps survive reimports while the source material name still exists; stale ones are reported.
- **Textures:**
  - External textures referenced by the model are resolved relative to the model, then to its folder tree.
  - Textures already inside `Assets/` are referenced by GUID, not copied.
  - Textures outside `Assets/` are reported, and **Extract Textures** copies them in.
  - Embedded textures are extracted to files on request, with deterministic names and content-hash deduplication.
  - Source files are never modified.

## 8. Asset identity and reimport

- **Model GUIDs.** A model keeps its file GUID (meta). Sub-assets are addressed as `asset:<model-guid>/<kind>/<stable-name>`, for example `mesh/Body` or `material/Skin`.
- **Stable names.** They come from source names, made unique deterministically, and are recorded in the meta (`subAssets`). A renamed source node can be re-linked by hand instead of silently breaking references.
- **Import artifacts.** Processed meshes, skeletons and clips are written to `Library/Artifacts/<guid>/` as versioned binary, keyed by source hash + settings hash + importer version.
- **Staged imports.** A reimport writes to a staging folder and swaps on success. A failed import keeps the last good artifact and reports the error.
- **Background imports.** Parsing and processing run on worker threads with progress and cancellation. GPU uploads happen on the main thread.
- **Exports.** Artifacts are packed into the content archives.

## 9. Project structure

New projects are created only where a dependency boundary needs one:

- `MNVTEngine.Mathematics` — extended.
- `MNVTEngine.Graphics` — extended:
  - `Meshes/`: mesh, vertex formats, primitives;
  - `Materials/`: PBR material model;
  - `Rendering3D/`: render queues, culling, lights, shadows, and the 3D renderer, which is backend-independent.
- `MNVTEngine.Graphics.Vulkan` — extended with depth images, pipeline depth/cull state, more texture slots, 32-bit indices and static buffers.
- `MNVTEngine.Scenes` — extended: 3D transform, `MeshRenderer`, `Light`, 3D camera settings.
- `MNVTEngine.Assets` — extended with sub-assets, artifacts, staged/background import, and the model import settings and workflow.
- `MNVTEngine.Assets.Models` — new. FBX (Assimp) and glTF (SharpGLTF) readers into `ImportedModel`. It is a separate project so that native and parsing dependencies stay out of games and runtime.
- `MNVTEngine.Animation` — new in Phase 8: skeletons, clips, `Animator`, skinning.
- `MNVTEngine.Physics3D` — new in Phase 8, after evaluation.

## 10. Phases

Each phase ships with tests, documentation, validation-layer runs and a commit.

| Phase | Content | Exit criteria |
|---|---|---|
| 1 | Math (plane, frustum, sphere, look-at, Vulkan projections, intersections); 3D transform with cached matrices; scene format v3 with v2 read | Unit tests; every 2D sample, test and scene unchanged. **Done.** |
| 2 | Mesh asset, vertex formats, primitives; camera perspective/orthographic; GPU mesh buffers ([meshes](meshes.md)) | Mesh and primitive tests; GPU upload tests. **Done.** Depth images moved to Phase 3: with dynamic rendering, every pipeline in a pass with a depth attachment must declare its format, so depth belongs with the pipeline changes. |
| 3 | First 3D renderer: depth images and attachments, mesh pipelines (depth/cull state), per-object data, multiple objects, culling; `MeshRenderer` | A 3D scene with primitives renders correctly with validation on (screenshots) |
| 4 | PBR material model and `.mnvtmat` v2, texture slots, material inspector | Serialization tests; materials edited in the editor and visible in the scene |
| 5 | Sub-assets, artifacts, staged imports; FBX and glTF readers; meshes, hierarchy, materials, textures; diagnostics | Real CC0/CC-BY fixtures (FBX binary + ASCII, GLB) import and render |
| 6 | Model import inspector, extraction, remapping, reimport, model preview, drag into scene | Spec workflow end to end in the editor |
| 7 | Directional/point/spot lights, PBR shading, directional shadow maps (then spot and point) | Lit, shadowed scene; GPU tests |
| 8 | Skeletons, skinning, clips, `Animator`; 3D physics | Animated skinned FBX and GLB characters; physics tests |
| 9 | 3D scene view, gizmos, picking, preview polish, batching, profiling, docs | Acceptance criteria 1–20 |

**Fixtures.** The tests use real assets with compatible licenses, committed with their license files:

- Khronos glTF Sample Assets (per-model licenses, many CC-BY 4.0 or CC0);
- Kenney and Quaternius packs (CC0), which ship FBX and GLB with textures, hierarchy and animated skeletal characters.
