# Materials and 2D Lighting

## Materials (`.mnvtmat`, schema 2)

Materials serve sprites, tilemaps and 3D meshes. Create one from the Project panel (right-click the file list, then
**Create > Material**) and edit it in the [material inspector](../editor/editor.md#material-inspector).

```json
{
  "schemaVersion": 2,
  "shader": "builtin:standard",
  "blend": "Opaque",
  "cull": "Back",
  "renderQueue": 2000,
  "properties": {
    "_BaseColor": [1.0, 0.77, 0.34, 1.0],
    "_BaseColorMap": "asset:0f5e0000-0000-4000-8000-000000000071",
    "_Metallic": 1.0,
    "_Roughness": 0.3
  }
}
```

| Field | Meaning |
|---|---|
| `shader` | Which shader to use. Built-in: `builtin:standard` (3D PBR), `builtin:unlit` (3D) or `builtin:sprite` (2D, the default). Custom: a fragment-shader asset (`.frag`, `.glsl`) as `"asset:<guid>"`. |
| `vertexShader` | Optional, with a custom shader. A vertex-shader asset; meshes need one that reads the [mesh vertex layout](meshes.md). Without one, the built-in sprite vertex stage is used. |
| `blend` | `Opaque`, `Alpha`, `Premultiplied`, `Additive`, `Multiply`. Default: `Opaque` for the 3D shaders, `Alpha` otherwise. |
| `cull` | `Back`, `Front` or `None`. Default: `Back` for 3D shaders, `None` for 2D. |
| `depthTest`, `depthWrite` | Booleans. Default: test on; write off for transparent materials. |
| `renderQueue` | Draw order, 0–5000. 2000 is opaque; 2450 is cutout (the default when `_AlphaCutoff` > 0); 3000 and above is transparent. The default is 3000 when blending, 2000 otherwise. |
| `properties` | Values by GLSL name. Each is a number, an array of 2 or 4 numbers (colors are 4), or a texture `"asset:<guid>"`. The names `uViewProjection`, `uTexture`, `uTime`, `uModel` and `uNormalMatrix` are reserved. |

Schema 1 files, which named properties `uniforms`, load and are migrated; saving writes schema 2.

**Standard and Unlit properties** follow glTF 2.0 metallic-roughness:

| Property | Kind | Default |
|---|---|---|
| `_BaseColor`, `_BaseColorMap` | color, texture | white, white |
| `_Metallic`, `_Roughness` | 0–1 | 0, 0.5 |
| `_MetallicRoughnessMap` | texture: G = roughness, B = metallic | white |
| `_NormalMap`, `_NormalScale` | tangent-space texture, number | flat normal, 1 |
| `_OcclusionMap`, `_OcclusionStrength` | texture (R channel), 0–1 | white, 1 |
| `_EmissiveColor`, `_EmissiveMap` | HDR color, texture | black, white |
| `_AlphaCutoff` | 0–1; lower alpha is discarded | 0 (off) |

- **Unlit** uses `_BaseColor`, `_BaseColorMap` and `_AlphaCutoff`.
- **Lighting.** Until lights arrive (Phase 7 of the [3D plan](3d-plan.md)), Standard shows the surface at full ambient light: base color × occlusion + emission. Metallic, roughness and the normal map are stored, edited and bound now; the lighting shader uses them.
- **Defaults.** Every slot a material leaves empty gets its default: a 1×1 white, black or flat-normal texture, or the default number.

**Textures and samplers.** A shader can declare any number of `sampler2D`s. Textures are bound by name: `uTexture` is the
draw's texture (the sprite, or the mesh renderer's), and every other sampler comes from the material. Leave their
bindings to the compiler; two samplers on one explicit binding are an error.

**Custom shaders.** A custom shader is the **fragment stage** for the built-in sprite vertex shader:

- It receives `vTexCoord`, `vColor` and `uTexture`.
- It may declare `uniform float uTime`, which the renderer sets to seconds since start.

```glsl
#version 450 core
in vec2 vTexCoord;
in vec4 vColor;
layout(binding = 0) uniform sampler2D uTexture;
uniform float uTime;
uniform vec4 uGlow;
uniform float uSpeed;
out vec4 fragColor;

void main()
{
    vec4 color = texture(uTexture, vTexCoord) * vColor;
    float pulse = 0.5 + 0.5 * sin(uTime * uSpeed);
    fragColor = vec4(color.rgb + uGlow.rgb * pulse * color.a, color.a);
}
```

**Using materials.**

- Assign a material to `SpriteRenderer.Material` or `Tilemap.Material`. Sprites with different materials batch separately: a draw breaks when the texture **or** the material changes.
- Assign a material to `MeshRenderer.Material` for 3D; without one, the renderer's color and texture use a built-in Standard material.
- **Validation.** The importer (and `mnvt build`) validates the JSON and records shaders and textures as dependencies. GLSL compiles when the material is first used, because compilation needs a GPU; `mnvt build` precompiles it.
- **Fallback.** An invalid material or a shader that fails to compile logs one error with the compiler output, and the sprite draws with the default material.
- **Reloading.** Editing a material, its shaders or its textures reloads it in the editor.

## 2D lighting

| Component | Settings |
|---|---|
| `Light2D` | `Color`, `Intensity`, `Radius` (world units, scaled by the transform) |
| `GlobalLight2D` | Ambient `Color` and `Intensity` (default when absent: dark blue-grey 0.15) |

A scene without an enabled `Light2D` is unlit, so existing scenes look unchanged. With lights, `SceneRenderer`:

1. Draws the scene normally.
2. Clears a light buffer (a render target of the view's size) to the ambient color.
3. Adds a soft disc for every light (additive, squared falloff to `Radius`).
4. Multiplies the light buffer over the scene.

**Characteristics:**

- **Coverage.** Every sprite and tile is lit the same way, with no per-sprite shader work. The number of lights is unlimited, and each costs one quad.
- **Saturation.** Light adds up and saturates at the sprite's own color: 8-bit buffer, no HDR.
- **Light buffers.** One buffer is kept per view size (the editor's Scene and Game views), and they are released with the renderer.
- **Tests.** A GPU test checks the pixels under a light against ambient, a custom-shader material, the fallback for a broken shader, and that nothing leaks.

## Limitations

- There are no shadows, normal maps, spot or cone lights, or light layers. Everything in a lit scene is lit, so emissive sprites cannot opt out yet.
- There is no HDR or bloom.
- Custom vertex stages are not supported; materials replace the fragment stage only.
- The editor has no gizmo for a light's radius.
