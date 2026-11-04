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
- **Skins** (joints and inverse bind matrices) and **animations** (translation, rotation and scale channels; step, linear or cubic spline). They are imported now and played in Phase 8.

Names are made unique (`Body`, `Body_1`, ...) and stripped of `#`, `/`, `\` and `"`, so they are safe in references.

## Import settings

Stored in the model's `.meta` under `settings`; invalid values are reported and reset.

| Setting | Default | Effect |
|---|---|---|
| `scale` | 1 | Uniform scale on the root, after unit conversion (0 < scale ≤ 10000). |
| `generateMissingNormals` | true | Smooth normals for meshes without any. |
| `generateTangents` | true | Tangents for meshes with normals and UVs (needed by normal maps). |
| `optimizeMeshes` | true | FBX: merge identical vertices, reorder for the vertex cache. |
| `importMaterials`, `importSkins`, `importAnimations` | true | Skip that part of the file. |

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

A found texture becomes a **dependency**: when it changes the model reimports. When files are added, a model whose
last import reported problems reimports too, so a missing texture is picked up once it is copied in. Textures embedded
in the file stay in the artifact.

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
  - With `Material` empty, each submesh uses the model's material, built on the Standard shader. `Material` set draws every submesh with it.
  - A missing model or mesh logs one warning and draws nothing.
- **At runtime.** `ResourceCache.GetModel`, `GetModelMesh` and `GetModelMaterial` read the artifact on first use. Reimports evict the model, its materials, its embedded textures and its GPU meshes.

## Not yet

- **Skinned meshes** draw in their bind pose; skinning and animation playback come in Phase 8.
- **Morph targets** (blend shapes) are reported and skipped.
- **Lights and cameras** in model files are reported and skipped.
- **Metallic and roughness maps** are used only as a combined glTF-style map; separate FBX maps are not combined.
- **Texture UV sets.** Textures on UV set 1 are sampled with UV set 0 (a warning says so).
