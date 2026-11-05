# Models (FBX, glTF, GLB)

3D models are assets like textures: drop an `.fbx`, `.gltf` or `.glb` into `Assets/` and it gets a `.meta` with a stable
GUID. The model importer (`MNVTEngine.Assets.Models`) turns it into a **model artifact**. Scenes reference the model's
meshes as sub-assets, so they pick up reimports. See the [plan](../graphics/3d-plan.md) for the decisions behind this.

## Readers

| Format | Reader | Notes |
|---|---|---|
| `.fbx` (binary and ASCII 7.x) | Assimp 6 (`Silk.NET.Assimp`) | Triangulated; split per material into submeshes; converted to meters (`AI_CONFIG_FBX_CONVERT_TO_M` + global scale); UVs flipped to the engine's top-left origin; at most 4 skin influences. Phong materials are converted to metallic-roughness (shininess becomes roughness, opacity becomes alpha blending); PBR properties win when the file has them. |
| `.gltf` (external or embedded buffers), `.glb` | SharpGLTF | Already in engine conventions (right-handed, +Y up, meters, metallic-roughness); taken as is. One engine mesh per glTF mesh, one submesh per primitive. Only the default scene is imported. |

Both readers produce `ModelData`, the engine's own model description (`MNVTEngine.Assets.Models`, in the Assets
project so the runtime can read it without the importer):

- **Nodes,** parents first, with local position, rotation and scale. Node 0 is the root.
- **Meshes,** with positions, normals, tangents, two UV sets, vertex colors, submeshes, a material per submesh, and joints/weights for skinned meshes.
- **Materials,** metallic-roughness: base color, metallic, roughness, emission, normal scale, occlusion strength, alpha mode (opaque, mask, blend), double-sided, and textures by role.
- **Textures:** a project texture (by GUID), an image embedded in the file, or an unresolved source path.
- **Skins** (joints and inverse bind matrices) and **animations** (translation, rotation and scale channels; step, linear or cubic spline). They play with [`ModelAnimator`](#animation-and-skinning).

Names are made unique (`Body`, `Body_1`, ...) and stripped of `#`, `/`, `\` and `"`, so they are safe in references.
FBX meshes are named after their node (the object name in the modelling tool), not the FBX geometry name, which tools
rename freely; glTF meshes keep their mesh names. A renamed node or mesh breaks references to it: they log a warning
and draw nothing until they are re-pointed.

## Import settings

Stored in the model's `.meta` under `settings`; invalid values are reported and reset.

| Setting | Default | Effect |
|---|---|---|
| `scale` | 1 | Uniform scale on the root, after unit conversion (0 < scale ≤ 10000). |
| `generateMissingNormals` | true | Smooth normals for meshes without any. |
| `generateTangents` | true | Tangents for meshes with normals and UVs (needed by normal maps). |
| `optimizeMeshes` | true | FBX: merge identical vertices, reorder for the vertex cache. |
| `importMaterials`, `importSkins`, `importAnimations` | true | Skip that part of the file. |
| `materials` | `{}` | Source material name to `"asset:<guid>"` of a `.mnvtmat` used instead (Extract Materials, Remap). |
| `textures` | `{}` | Model texture name to `"asset:<guid>"` of a project texture used instead (Extract Textures). |

## Textures

An external texture path is resolved in this order:

1. relative to the model;
2. the file name next to the model;
3. the file name in a `textures`/`Textures` folder beside it;
4. a unique file of that name anywhere in `Assets/`.

Texture names are never guessed beyond that. The import reports, as warnings:

- a texture that was not found;
- several files with the same name;
- a texture outside `Assets/`;
- a format the engine cannot load (for example EXR).

A found texture becomes a **dependency** (it is packed with the model). Its content is not read by the import, so
editing it does not reimport the model. A glTF's `.bin` buffers are **import inputs**: changing one reimports the
model. When files are added, a model whose last import reported problems reimports too, so a missing texture is picked
up once it is copied in. Textures embedded in the file stay in the artifact until they are extracted.

A texture listed in the `textures` remap table is replaced by that project texture, and its problem is no longer
reported. Texture names are unique within the model (`diffuse`, `diffuse_1`, ...).

## Model inspector

Select a model in the Project panel. The inspector shows its import messages and facts, then:

- **Add to scene** instantiates it (see below).
- **Preview:** the model with its current materials; drag to orbit.
- **Import settings** with **Apply** (writes the meta and reimports) and **Revert**.
- **Materials:** each source material shows whether it is imported, extracted or remapped.
  - Click it to remap it to any `.mnvtmat` (or back to *Imported*), or drop a material on it.
  - **Extract Materials** writes `<Model>_<Material>.mnvtmat` (Standard shader, the imported values and textures) into a `Materials` folder next to the model. Embedded textures are extracted first, because a material file can only reference project textures. Existing files are reused and never overwritten, so extracting again creates no duplicates and keeps your edits.
  - **Reset remaps** draws the imported materials again; extracted files stay.
- **Textures:** project, embedded or missing. **Extract Textures** writes embedded images, and images found outside `Assets/`, as `<Model>_<Texture>.<ext>` into a `Textures` folder next to the model. Identical images are written once. A different file already at that path gets a content-hash suffix. EXR and other formats the engine cannot read are skipped and reported.
- **Meshes, skins and animations,** with vertex, triangle, joint and channel counts.

All of it is `ModelWorkflow` (Editor.Services, UI-free and tested). Every change goes through the model's meta and a
reimport; source files are never modified. A remap whose source material or texture no longer exists is reported as
a warning (renamed in the source?) and ignored. A remap target that cannot be loaded falls back to the imported material.

## Artifacts

- An import that succeeds writes `Library/Artifacts/<guid>/model`, a versioned binary (`ModelArtifact`, magic `MNVTMODL`). It is validated completely when read.
- Each artifact file is written next to its target and moved over it.
- An import that fails keeps the **last good artifact**. Scenes keep drawing it while the error shows in the asset inspector.
- Deleting the model deletes its artifacts. `.gltf` buffers (`.bin`) are dependencies too.
- `AssetDatabase.ReadArtifact(guid, name)` reads an artifact: from the archive in exported games, from `Library/` in development.

Who imports:

- **The editor** imports models.
- **The Player** imports them before it runs (`ContentCompiler.ImportModels`).
- **The build** imports them too: `ImportModels` reports import errors as `MNVB220` and warnings as `MNVB221`; a model without an artifact fails the build with `MNVB222`.
- **Exported games** ship only the artifact, not the source file. They never contain Assimp or SharpGLTF; an architecture test enforces this.
- **Read-only databases** (the runtime) give model files the Model type without importing them.

## Using a model

- **Editor:** drag the model from the Assets panel into the Hierarchy or the Scene view, or select it and press **Add to scene**. This creates one GameObject per node, with the node's transform. Nodes with a mesh get a `MeshRenderer` whose `Mesh` is `"asset:<guid>#mesh/<name>"`.
- **Code:** `ModelInstantiation.Instantiate(scene, guid, modelData, name, parent)` does the same.
- **`MeshRenderer.Mesh`.** It takes precedence over `Primitive`.
  - A reference to a whole model (no `#mesh/...`) draws the model's mesh if it has exactly one.
  - With `Material` empty, each submesh uses the model's material: the remapped `.mnvtmat` if there is one, else the imported material on the Standard shader. `Material` set draws every submesh with it.
  - A missing model or mesh logs one warning and draws nothing.
- **At runtime.** `ResourceCache.GetModel`, `GetModelMesh` and `GetModelMaterial` read the artifact on first use. Reimports evict the model, its materials, its embedded textures and its GPU meshes.

## Animation and skinning

- **`ModelAnimator`** plays a model's animation clips on its GameObject hierarchy. Adding a model with clips to a scene puts one on the root, playing the first clip on start and looping.
  - Settings: `Model`, `Clip` (empty = first), `Speed` (negative plays backwards), `Loop`, `PlayOnStart`.
  - Game code: `Play(clip, restart)`, `Pause`, `Resume`, `Stop`, `Sample(seconds)`, `PlaybackTime`, `Duration`, `ClipNames` and the `Finished` event for clips that do not loop.
  - Each animated node is the descendant GameObject of the same name (node names are unique within a model), and the model's root node is the animator's own GameObject. Its local position, rotation and scale follow the clip. Nodes the clip animates but that are missing (renamed or deleted) stay still, with one warning.
- **Sampling** follows glTF: values hold before the first key and after the last. Step holds the previous key; linear interpolates, with rotations by shortest-path slerp. Cubic splines are Hermite curves whose tangents are scaled by the key interval.
- **Skinning.** A `MeshRenderer` whose mesh is skinned deforms with the joints of its skin. The joints are the GameObjects of the joint nodes, found by name under the model's root, so animating or moving them by hand both work.
  - Skinning is linear blend skinning on the CPU (up to four joints per vertex) into a dynamic vertex buffer per renderer. It runs once per frame and is shared by the shadow and main passes. Bounds follow the deformed mesh for culling.
  - Per frame it costs about the vertex count times four matrix blends. GPU skinning is a later optimization.
- **Hosts** give scenes the clips through `Scene.ModelAnimations` (`ModelAnimationProvider`, which reads model artifacts), like `Scene.TextAssets`. The runtime and editor Play Mode set it.
- **Tested** with the Khronos CesiumMan (GLB, and as FBX written by Assimp's exporter) and BoxAnimated. Assimp's FBX exporter tags meter data as centimeters, so such files need `"scale": 100`; FBX files from Blender or Maya carry their real units.

## Not yet

- **Animation blending, layers, state machines and root motion.** One clip plays at a time.
- **Morph targets** (blend shapes) are reported and skipped.
- **Lights and cameras** in model files are reported and skipped.
- **Metallic and roughness maps** are used only as a combined glTF-style map; separate FBX maps are not combined.
- **Texture UV sets.** Textures on UV set 1 are sampled with UV set 0 (a warning says so).
