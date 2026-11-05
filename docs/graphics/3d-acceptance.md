# 3D acceptance criteria: evidence

The 3D spec's acceptance criteria, each with where it is implemented and what proves it. The end-to-end test
`tests/MNVTEngine.Editor.Tests/AcceptanceTests.cs` (`TheModelWorkflow_FromImportToExport`) walks criteria 1–16 and 20 in
one project, with the services the editor uses. GPU tests and real-app runs cover rendering.

| # | Criterion | Implementation | Proof |
|---|---|---|---|
| 1 | Import an FBX model | Assimp reader (`AssimpModelReader`), [models](../engine/models.md) | Acceptance test; `ModelImportTests.Fbx_*`, ASCII FBX test |
| 2 | Import a GLB model | SharpGLTF reader (`GltfModelReader`) | Acceptance test; `ModelImportTests.Glb_*` |
| 3 | Models with multiple meshes | One engine mesh per FBX node / glTF mesh | Acceptance test (the crate has 3 meshes) |
| 4 | Preserve the hierarchy | `ModelData.Nodes`; `ModelInstantiation` creates one GameObject per node | Acceptance test; `ModelRuntimeTests.Instantiate_*`; editor Hierarchy screenshots |
| 5 | Several materials on different submeshes | Submesh per material (FBX) or primitive (glTF) | Acceptance test (two-primitive glTF) |
| 6 | Resolve external textures | `TextureResolver`: relative path, next to the model, `textures/`, unique name in `Assets/` | Acceptance test; `Fbx_MissingTexture_IsReportedNotGuessed`, `AddingMissingTexture_ReimportsModel` |
| 7 | Extract embedded textures | `ModelWorkflow.ExtractTextures` | Acceptance test (CesiumMan); `ModelWorkflowTests` |
| 8 | Extract materials into `.mnvtmat` | `ModelWorkflow.ExtractMaterials` | Acceptance test; reuse and keep-edits test |
| 9 | Extract textures into project folders | `<Model>/Textures/<Model>_<Texture>.<ext>`, deduplicated | Acceptance test |
| 10 | Remap imported materials | `materials` remap table in the model's meta; model inspector | Acceptance test; `Remap_*`; Player screenshot (gold crate) |
| 11 | Edit material properties in the Inspector | `MaterialInspector` over `MaterialDocument` | Acceptance test (edit and save); `MaterialDocumentTests` |
| 12 | Preview imported models | Model inspector preview: lit, drag to orbit, wheel to zoom | Editor screenshots |
| 13 | Reimport modified FBX and GLB | Source and settings hashes; staged artifacts; import inputs | Acceptance test (re-saved FBX, changed GLB); `CorruptModel_*`, `ChangingABuffer_*` |
| 14 | Valid references after reimport | Sub-asset names: FBX meshes are named after their node, glTF meshes by name | Acceptance test (every scene reference resolves after reimport; remaps and extracted textures kept) |
| 15 | Skeletal models and animations | Skins, clips, `ModelAnimator`, CPU skinning | Acceptance test; `SkinnedAnimatedFbx_*`; `SkinnedCharacter_*` (GLB and FBX, rendered); Player screenshots |
| 16 | Instantiate in a scene | Drag onto the Hierarchy or the Scene view, **Add to scene** | Acceptance test; `ModelRuntimeTests` |
| 17 | Render with Vulkan 1.4 | Vulkan backend, `Renderer3D`, dynamic rendering | `VulkanLightingTests`, `VulkanMeshTests`, runtime render tests with validation on |
| 18 | PBR materials and textures | Standard shader (GGX, metallic-roughness, normal, occlusion, emission) | `VulkanLightingTests`; `ModelMesh_RendersFromTheArtifact_*`; material and model previews |
| 19 | Lighting and real-time shadows | `Light`, `AmbientLight`, directional shadow map | `VulkanLightingTests.DirectionalShadows_*`; `Lights_LightTheScene_CastShadows_*`; Showcase3D |
| 20 | Export and run a standalone game with imported 3D assets | `ContentCompiler.ImportModels`; artifacts packed instead of sources | Acceptance test (archive contents); `Models_ShipAsTheirArtifact_*`; `mnvt build samples/Showcase3D --configuration release` then run |

## Manual check

```bash
scripts\mnvt.cmd editor samples/Showcase3D
scripts\mnvt.cmd build samples/Showcase3D --configuration release --run
```

In the editor:

1. Select `Models/CrateFbx/wooden_crate_01_1k.fbx` in the Project panel. The inspector shows the model's import settings, materials, textures and preview. Drag the preview to orbit it.
2. Press **Extract Materials**, then click the material to remap it.
3. Drag the model into the Scene view (3D mode) or the Hierarchy.
4. Press Play: the scene shows lit, shadowed models, the walking CesiumMan and the physics objects.
