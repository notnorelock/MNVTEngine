# Models (FBX, glTF, GLB)

3D models are assets like textures: drop an `.fbx`, `.gltf` or `.glb` into `Assets/` and it gets a `.meta` with a stable
GUID. The model importer (`MNVTEngine.Assets.Models`) turns it into a **model artifact**. Scenes reference the model's
meshes as sub-assets, so they pick up reimports. See the [plan](../graphics/3d-plan.md) for the decisions behind this.

## Readers

| Format | Reader | Notes |
|---|---|---|
| `.fbx` (binary and ASCII 7.x) | Assimp 6 (`Silk.NET.Assimp`) | Triangulated; split per material into submeshes; converted to meters (`AI_CONFIG_FBX_CONVERT_TO_M` + global scale); UVs flipped to the engine's top-left origin; at most 4 skin influences. Phong materials are converted to metallic-roughness (shininess becomes roughness, opacity becomes alpha blending); PBR properties win when the file has them. Files written by Blender carry the roughness map in the shininess slot and the metallic map in the reflection slot; they are read as such (other tools' shininess maps are glossiness and stay unclassified). |
| `.gltf` (external or embedded buffers), `.glb` | SharpGLTF | Already in engine conventions (right-handed, +Y up, meters, metallic-roughness); taken as is. One engine mesh per glTF mesh, one submesh per primitive. Only the default scene is imported. |

Both readers produce `ModelData`, the engine's own model description (`MNVTEngine.Assets.Models`, in the Assets
project so the runtime can read it without the importer):

- **Nodes,** parents first, with local position, rotation and scale. Node 0 is the root.
- **Meshes,** with positions, normals, tangents, two UV sets, vertex colors, submeshes, a material per submesh, joints/weights for skinned meshes, and blend shapes (morph targets: position, normal and tangent offsets with names and default weights).
- **Materials,** metallic-roughness: base color, metallic, roughness, emission, normal scale, occlusion strength, alpha mode (opaque, mask, blend), double-sided, and textures by role.
- **Textures:** a project texture (by GUID), an image embedded in the file, or an unresolved source path.
- **Skins** (joints and inverse bind matrices) and **animations** (translation, rotation, scale and blend shape weight channels; step, linear or cubic spline). They play with [`ModelAnimator`](#animation-and-skinning).
- **Lights and cameras** on nodes. See [Lights and cameras](#lights-and-cameras).

A mesh that cannot be drawn (indices out of range, streams of the wrong length) is reported and dropped; the nodes that used it draw nothing, and the other nodes keep their meshes.

Names are made unique (`Body`, `Body_1`, ...) and stripped of `#`, `/`, `\` and `"`, so they are safe in references.
FBX meshes are named after their node (the object name in the modelling tool), not the FBX geometry name, which tools
rename freely; glTF meshes keep their mesh names. A renamed node or mesh breaks references to it: they log a warning
and draw nothing until they are relinked (see [Relinking renamed meshes](#relinking-renamed-meshes)).

## Import settings

Stored in the model's `.meta` under `settings`; invalid values are reported and reset.

| Setting | Default | Effect |
|---|---|---|
| `scale` | 1 | Uniform scale on the root, after unit conversion (0 < scale ≤ 10000). |
| `generateMissingNormals` | true | Smooth normals for meshes without any. |
| `generateTangents` | true | Tangents for meshes with normals and UVs (needed by normal maps). |
| `optimizeMeshes` | true | FBX: merge identical vertices, reorder for the vertex cache. |
| `importMaterials`, `importSkins`, `importAnimations`, `importLights`, `importCameras` | true | Skip that part of the file. |
| `materials` | `{}` | Source material name to `"asset:<guid>"` of a `.mnvtmat` used instead (Extract Materials, Remap). |
| `textures` | `{}` | Model texture name to `"asset:<guid>"` of a project texture used instead (Extract Textures). |
| `meshAliases` | `{}` | Old mesh name to a current mesh name, so references to the old name keep drawing (Relink). |
| `lods` | `[]` | Levels of detail for static meshes: `[{ "ratio": 0.5, "screenSize": 0.3 }, { "ratio": 0.15, "screenSize": 0.1 }]` (see [levels of detail](#levels-of-detail)). |

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

**Separate metallic and roughness maps** (FBX) are combined into one glTF-layout map, which the Standard shader
reads: G is roughness, B is metallic, from the red channel of each map. The larger map sets the size and the other is
resampled. A material with only one of the maps keeps 1 in the other channel, so its factor applies. With a map, the
factor is 1 and the map decides, as in the modelling tool. The combined map (`<Material>_MetallicRoughness`) is
embedded in the artifact, and **Extract Textures** writes it out.

The maps are read to combine them, so they become **import inputs**: editing one reimports the model. A missing map,
or one in a format the engine cannot read, is reported and its factor applies. The usual fix for EXR maps is to convert
them to PNG and remap them in the `textures` table; remapped maps are combined too.

**UV sets.** Each map samples the UV set its source chooses: glTF `texCoord`, FBX through Assimp's UV source. The
Standard shader has a property per map (`_BaseColorUVSet`, `_MetallicRoughnessUVSet`, `_NormalUVSet`,
`_OcclusionUVSet`, `_EmissiveUVSet`): 0 (the default) samples the first UV set, 1 the second. The Unlit shader has
`_BaseColorUVSet`. Imported materials, extracted `.mnvtmat` files and the shadows of cutout materials keep the sets.
A map on set 1 of a mesh without a second UV set is reported; a set above 1 is reported and sampled with set 0.

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
- **Meshes, skins, animations, lights and cameras,** with vertex, triangle, joint and channel counts.
- **Relink meshes:** see below.

All of it is `ModelWorkflow` (Editor.Services, UI-free and tested). Every change goes through the model's meta and a
reimport; source files are never modified. A remap whose source material or texture no longer exists is reported as
a warning (renamed in the source?) and ignored. A remap target that cannot be loaded falls back to the imported material.

## Relinking renamed meshes

Scenes reference model meshes by name (`asset:<guid>#mesh/<name>`). When a mesh is renamed in the source file, those
references draw nothing. In the model inspector, **Relink meshes > Find missing references** scans the saved scenes and
prefabs for mesh names the model no longer has, and lists the files using each. **Point at...** picks the current
mesh. The model gets a `meshAliases` entry (old name to current name), and the references resolve again without
editing the scenes. **Remove** deletes an alias.

An alias whose old name is a mesh of the model again, or whose target is not a mesh, is reported and ignored.
`ModelWorkflow.FindMissingMeshReferences` and `Relink` do the same from code.

## Artifacts

- An import that succeeds writes `Library/Artifacts/<guid>/model`, a versioned binary (`ModelArtifact`, magic `MNVTMODL`, version 5). It is validated completely when read; streamed full-detail geometry (`mesh<N>`, see [levels of detail](#levels-of-detail)) is validated when it is loaded.
- Each artifact file is written next to its target and moved over it.
- An import that fails keeps the **last good artifact**. Scenes keep drawing it while the error shows in the asset inspector.
- Deleting the model deletes its artifacts. `.gltf` buffers (`.bin`) are dependencies too.
- `AssetDatabase.ReadArtifact(guid, name)` reads an artifact: through the virtual file system: from the packages in exported games, from `Library/Artifacts` in development.

Who imports:

- **The editor** imports models in the background when files change (see [background imports](assets.md)): the inspector shows *Importing in the background...* and scenes keep the last good artifact until the import finishes. Settings changes from the model inspector import right away.
- **The Player** imports them before it runs (`ContentCompiler.ImportModels`).
- **The build** imports them too: `ImportModels` reports import errors as `MNVB220` and warnings as `MNVB221`; a model without an artifact fails the build with `MNVB222`.
- **Exported games** ship only the artifact, not the source file. They never contain Assimp or SharpGLTF; an architecture test enforces this.
- **Read-only databases** (the runtime) give model files the Model type without importing them.

## Levels of detail

- **Generation.** With `lods` set, the importer reduces every static mesh (not skinned, no blend shapes, at least 64
  triangles) to each `ratio` of its triangles with `MeshSimplifier` (quadric error edge collapse). Vertices only merge
  into existing vertices, so a level uses a subset of the mesh's vertices with their normals, UVs and colors unchanged.
  Open borders, UV and normal seams and submesh boundaries do not move, so meshes with many seams reduce less than
  asked: the Showcase3D crate goes from 5508 to 2754 and 1566 triangles for ratios 0.5 and 0.15. A level that would
  keep more than 90% of the previous one is skipped, with the levels after it. The model inspector lists each mesh's
  levels and edits them (*Levels of detail* under *Import settings*: triangle share and screen height per level).
- **Selection.** Each frame the mesh pass picks a level per renderer from its screen size: the diameter of its world
  bounds over the view's height at its distance (the whole height for orthographic views). A level applies below its
  `screenSize`; above the first one the full mesh is drawn. Shadows use the same level.
- **Streaming.** Meshes with levels keep their full-detail geometry in its own artifact (`mesh<N>`, magic `MNVTMESH`),
  shipped next to the model in exported games. The model artifact holds the levels, the bounds and the counts, so a
  model in the distance never reads its full geometry. When a mesh first needs full detail, it is read and validated
  on a worker while its first level stands in; full detail that no view drew for 5 seconds
  (`MeshScenePass.DetailKeepAlive`) is released, with its GPU copy. Code that reads `ModelMesh.Mesh` (picking, mesh
  sources, the inspector) gets the full mesh, loading it synchronously when needed. A damaged detail artifact logs
  error 2107 once and the first level keeps standing in.
- **Diagnostics.** The profiler counts *Meshes at reduced detail* and *Mesh detail loading*; `MeshScenePass.LodCounts`
  has the meshes drawn at each level in the last pass.
- **Not covered.** Skinned meshes and meshes with blend shapes have no levels; levels switch without cross-fading; the
  level does not take the render resolution or a global quality bias into account.

## Using a model

- **Editor:** drag the model from the Assets panel into the Hierarchy or the Scene view, or select it and press **Add to scene**. This creates one GameObject per node, with the node's transform. Nodes with a mesh get a `MeshRenderer` whose `Mesh` is `"asset:<guid>#mesh/<name>"`.
- **Code:** `ModelInstantiation.Instantiate(scene, guid, modelData, name, parent)` does the same.
- **`MeshRenderer.Mesh`.** It takes precedence over `Primitive`.
  - A reference to a whole model (no `#mesh/...`) draws the model's mesh if it has exactly one.
  - With `Material` empty, each submesh uses the model's material: the remapped `.mnvtmat` if there is one, else the imported material on the Standard shader. `Material` set draws every submesh with it.
  - A missing model or mesh logs one warning and draws nothing.
- **At runtime.** `ResourceCache.GetModel`, `GetModelMesh` and `GetModelMaterial` read the artifact on first use. Reimports evict the model, its materials, its embedded textures and its GPU meshes.

## Lights and cameras

Lights (glTF `KHR_lights_punctual`, FBX lights through Assimp) and cameras are kept with their nodes. Adding the model
to a scene gives the node's GameObject a `Light` or a `Camera`. A light or camera that points another way than the
engine's -Z goes on a child GameObject with that rotation.

- **Lights** are directional, point or spot. Ambient and area lights are reported and skipped.
  - glTF colors are linear and become sRGB, like every engine color.
  - Intensity: directional lights keep their glTF lux (Blender writes its sun strength unchanged).
  - Point and spot lights get candela × 4π / 683 / 1000, so a 1000 W Blender lamp has intensity 1.
  - FBX folds intensity into the color; the brightest channel becomes the intensity (100% is 1).
  - Range: the file's range. Without one (glTF: infinite), the range is where an inverse-square light of that intensity has faded to 1%: √(intensity × 100), at least 1 m.
  - Spot cones: glTF half angles become the engine's full angles. FBX angles are full angles already.
- **Cameras** are perspective (vertical field of view) or orthographic (half height), with their clip planes. An infinite far plane becomes 1000.
  - Assimp gives the horizontal field of view. The engine converts it with the camera's aspect, or 16:9 when the file has none.
  - Cameras are added **disabled**, so they do not take over the scene's main camera. Enable one to look through it.

## Animation and skinning

- **`ModelAnimator`** plays a model's animation clips on its GameObject hierarchy. Adding a model with clips to a scene puts one on the root, playing the first clip on start and looping.
  - Settings: `Model`, `Clip` (empty = first), `Speed` (negative plays backwards), `Loop`, `PlayOnStart`.
  - Game code: `Play(clip, restart)`, `Pause`, `Resume`, `Stop`, `Sample(seconds)`, `PlaybackTime`, `Duration`, `ClipNames` and the `Finished` event for clips that do not loop.
  - **Crossfades:** `CrossFade(clip, seconds)` blends from what plays to the new clip; both keep playing while their weights cross (`IsCrossFading`). Positions and scales are blended linearly, rotations as sign-aligned quaternion sums.
  - **Layers:** `SetLayer(layer, clip, weight, maskRoot)` plays a clip over the base on layer 1 and up, applied in order.
    - `maskRoot` limits a layer to a node and its descendants, e.g. an upper-body wave from `"Spine"`.
    - `SetLayerWeight` fades a layer; `SetLayer(layer, null)` removes it.
  - **Root motion:** `ApplyRootMotion` with `RootMotionNode` (empty = the model's root):
    - The node's animated movement moves the GameObject instead, converted to world space, and keeps going forward across loops.
    - The node stays in place. With `RootMotionHorizontalOnly`, the default, it keeps its vertical bob.
    - With `RootMotionRotation`, the node's turn around the up axis turns the GameObject too, so a turn-in-place clip turns it. The node keeps its first-frame heading, and the movement follows the GameObject's new heading, so a clip walking an arc walks the arc. Up to half a turn per clip cycle is carried across loops.
    - With `RootMotionNode` empty, the node is the GameObject itself: its animated position adds up instead of resetting.
  - Each animated node is the descendant GameObject of the same name (node names are unique within a model), and the model's root node is the animator's own GameObject. Its local position, rotation and scale follow the clip. Nodes the clip animates but that are missing (renamed or deleted) stay still, with one warning.
- **Sampling** follows glTF: values hold before the first key and after the last. Step holds the previous key; linear interpolates, with rotations by shortest-path slerp. Cubic splines are Hermite curves whose tangents are scaled by the key interval.
- **Skinning.** A `MeshRenderer` whose mesh is skinned deforms with the joints of its skin. The joints are the GameObjects of the joint nodes, found by name under the model's root, so animating or moving them by hand both work.
  - Skinning is linear blend skinning with up to four joints per vertex.
  - **On the GPU** (the default). The mesh is uploaded once with its joints and weights. Each frame the CPU computes only the joint matrices (joint world matrix times inverse bind matrix), and the skinned variants of the Standard, Unlit and shadow caster shaders move the vertices.
    - Culling uses the mesh's bounds moved by every joint matrix, which is conservative.
    - Up to 128 joints (`MaterialShaders.MaxJoints`).
  - **On the CPU**, into a dynamic vertex buffer per renderer, for meshes with blend shapes, more joints, or a material with another shader. It runs once per frame and is shared by the shadow and main passes. Bounds follow the deformed mesh. It costs about the vertex count times four matrix blends per frame.
  - `SceneRenderer.GpuSkinning = false` (`GameRuntimeOptions.GpuSkinning`) skins everything on the CPU. Both give the same picture; a test compares them.
- **Hosts** give scenes the clips through `Scene.ModelAnimations` (`ModelAnimationProvider`, which reads model artifacts), like `Scene.TextAssets`. The runtime and editor Play Mode set it.
- **Tested** with the Khronos CesiumMan (GLB, and as FBX written by Assimp's exporter) and BoxAnimated. Assimp's FBX exporter tags meter data as centimeters, so such files need `"scale": 100`; FBX files from Blender or Maya carry their real units.

## Blend shapes

Blend shapes (morph targets, shape keys) deform a mesh by weighted per-vertex offsets: facial expressions, correctives,
squash and stretch.

- **Import.**
  - glTF morph targets, with names from `extras.targetNames` (written by Blender and most exporters) and default weights from the mesh.
  - FBX blend shapes, through Assimp.
  - Names are unique per mesh; repeated names get `_2`, `_3`.
  - Animated weights import as weight channels: glTF `weights` channels, FBX morph animations.
- **`MeshRenderer`.**
  - Weights are kept per renderer: `BlendShapeWeights` (saved with the scene), with 0 the base shape and 1 the full shape.
  - Game code: `SetBlendShapeWeight(index or name, weight)`, `GetBlendShapeWeight`, `BlendShapeNames` and `ResetBlendShapeWeights()`.
  - A shape never set shows the model's default weight. Adding a model to a scene records its defaults.
- **Inspector.** A renderer of a mesh with blend shapes shows a slider per shape, with undo, and **Reset to model defaults**.
- **Animation.** `ModelAnimator` clips with weight channels set the weights of the node's renderer every frame.
- **Rendering.**
  - Blend shapes are applied on the CPU, before skinning, into the renderer's dynamic mesh.
  - They are recomputed only when a weight changes; a skinned mesh deforms every frame anyway.
  - Shadows, picking bounds and culling follow the deformed shape.
- **Tested** with the Khronos AnimatedMorphCube (CC0) and MorphPrimitivesTest (CC-BY), from GLB and as FBX written by Assimp's exporter.

## Animator controllers

A `.mnvtanimator` asset is a state machine of a model's clips, like Unity's Animator Controller. Put a
`ModelAnimatorController` next to the `ModelAnimator`, set its `Controller`, and drive it from game code:

```json
{ "schemaVersion": 1,
  "parameters": { "speed": { "type": "float" }, "grounded": { "type": "bool", "default": true }, "jump": { "type": "trigger" } },
  "defaultState": "Idle",
  "states": [ { "name": "Idle", "clip": "Idle" }, { "name": "Run", "clip": "Run", "speed": 1.2 }, { "name": "Jump", "clip": "Jump", "loop": false } ],
  "transitions": [
    { "from": "Idle", "to": "Run", "duration": 0.2, "conditions": [ { "parameter": "speed", "greater": 0.1 }, { "parameter": "grounded", "is": true } ] },
    { "from": "Run", "to": "Idle", "duration": 0.2, "conditions": [ { "parameter": "speed", "less": 0.1 } ] },
    { "from": "*", "to": "Jump", "duration": 0.1, "conditions": [ { "parameter": "jump" } ] },
    { "from": "Jump", "to": "Idle", "duration": 0.25, "exitTime": 1 } ] }
```

```csharp
var controller = GetComponent<ModelAnimatorController>()!;
controller.SetFloat("speed", velocity.Magnitude);
if (Input.GetKeyDown(Key.Space)) controller.SetTrigger("jump");
controller.StateChanged += (_, from, to) => Log.Info($"{from} -> {to}");
```

- **Parameters:** `float`, `int`, `bool` and `trigger`, with optional defaults. `SetFloat`, `SetInteger`, `SetBool`, `SetTrigger` and `ResetTrigger` set them, and the getters read them. An unknown name or a wrong type throws once the controller is loaded. Values set before it loads are kept, and unknown ones are reported then.
- **States** play one clip each, with their own `speed` and `loop`. Entering a state sets the animator's clip, speed and looping. The first state is the default unless `defaultState` says otherwise.
- **Transitions** are checked every frame in file order: the current state's first, then the "any state" (`"from": "*"`) ones.
  - The first transition whose conditions all hold, and whose `exitTime` has passed (in clip lengths: 1 is the end of the first cycle), crossfades to its state over `duration` seconds.
  - Conditions: `greater`, `less`, `equals` and `notEquals` compare numbers, `is` compares bools, and a trigger takes no comparison. A trigger is reset by the transition that uses it.
  - A transition needs a condition or an exit time, and an "any state" transition needs a condition.
- **Validation.** The importer checks state, parameter and condition kinds and names and reports every mistake on the asset. Clip names are checked against the model when they play.
- **From code:** `Use(AnimatorControllerDefinition.Create(...))` runs a controller built in code, and `Play(state, fade)` forces a state. `CurrentState`, `StateTime` and `NormalizedTime` tell where it is.
- **Layers** stay in game code (`ModelAnimator.SetLayer`); a controller drives the base layer.
- **Editing.** The editor's [Animator window](../editor/editor.md#animator) edits a controller as a graph (double-click the asset); the JSON stays hand-editable.
