# Model test fixtures

Real-world models used by the model import tests. Licenses permit redistribution.

| Folder / file | Source | License | What it exercises |
|---|---|---|---|
| `WoodenCrate/fbx/wooden_crate_01_1k.fbx` + `textures/wooden_crate_01_diff_1k.jpg` | [Poly Haven: Wooden Crate 01](https://polyhaven.com/a/wooden_crate_01) (1k FBX download, Blender export, FBX 7400 binary) | CC0 1.0 | Binary FBX, external texture by relative path. The metal, roughness and normal maps it references (EXR) are deliberately **not** included: missing-texture diagnostics |
| `WoodenCrate/gltf/*` | Same asset, 1k glTF download | CC0 1.0 | glTF with an external `.bin` and external JPG textures (base color, ARM, normal) |
| `Khronos/CesiumMan.glb` | [Khronos glTF-Sample-Assets](https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/CesiumMan) | CC-BY 4.0 (Cesium logo: trademark), see `CesiumMan.LICENSE.md` | GLB, skinned mesh, skeleton, animation, embedded texture, hierarchy |
| `Khronos/BoxAnimated.glb` | [Khronos glTF-Sample-Assets](https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/BoxAnimated) | CC-BY 4.0, see `BoxAnimated.LICENSE.md` | GLB, several meshes and materials, node hierarchy, animation |
| `Converted/*.fbx` | The Khronos models above, converted with Assimp's FBX exporter (binary and ASCII) | as their sources | FBX skeleton, skinning, animation, several materials; ASCII FBX parsing |

Attribution for CC-BY models: "CesiumMan" and "Box Animated" by Cesium, from the Khronos glTF Sample Assets.
No CC0 skeletal FBX authored in a DCC tool was available as a direct download; the converted files stand in until one is.
