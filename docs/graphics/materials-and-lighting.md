# Materials, 3D Lighting and 2D Lighting

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
- **Lighting.** Standard is lit by the scene's [3D lights](#3d-lighting). In a scene without lights it shows base color × occlusion + emission, as before lighting existed.
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

## 3D lighting

| Component | Settings |
|---|---|
| `Light` | `Type` (Directional, Point, Spot), `Color`, `Intensity`, `Range` (point, spot), `SpotAngle` and `InnerSpotAngle` (full cone angles), `Shadows`, `ShadowStrength`, `ShadowDistance` (directional) |
| `AmbientLight` | `Color` and `Intensity` of the light that reaches everything from all around (default when absent: blue-grey 0.25, 0.26, 0.3; black when the scene has an environment) |
| `SceneEnvironment` | `Sky` (an equirectangular 2:1 texture), `Intensity`, `Rotation`, `DrawSkybox`: image-based lighting and the skybox |
| `Camera` | `ToneMapping` (None, ACES) and `Exposure` for lit 3D scenes |

- **When a scene is lit.** A scene with an enabled `Light`, `AmbientLight` or `SceneEnvironment` is lit. One with neither draws Standard surfaces unlit, at full brightness, so existing scenes look unchanged.
- **Direction.** Directional and spot lights shine along the GameObject's forward (-Z). **GameObject > Light** creates them already pointing down.
- **Shading (Standard).** Physically based: GGX distribution, Smith geometry and Schlick Fresnel, with metallic-roughness, normal and occlusion maps.
  - Colors and textures are sRGB. Lighting is computed in linear space and written back as sRGB.
  - The camera's `Exposure` scales the result. Its `ToneMapping`, ACES (Narkowicz fit), rolls highlights off instead of clipping them at white.
  - A white light of intensity 1 lights a white surface facing it to full white.
  - Point and spot lights fade smoothly to zero at `Range`. The spot edge fades between the inner and outer cones.
  - Ambient light adds a diffuse term and a rough reflection, so metals are not black without lights.
  - **Image-based lighting** (`SceneEnvironment`):
    - Surfaces reflect the sky image. The reflection gets blurrier with roughness, which samples smaller mips; the image always loads with mipmaps.
    - They are also lit diffusely by its blurriest mip.
    - `DrawSkybox` draws the sky behind the scene, on a cube around the camera drawn first.
- **How many.** Up to 8 lights per frame: directional lights first, then the point and spot lights whose range comes closest to the camera. More logs one warning.
- **Shadows.** Lights with `Shadows` cast them from opaque and alpha-cutout meshes onto every Standard surface.
  - **Directional:** the first one. A 2048×2048 map covering the camera's view up to `ShadowDistance`, fit to a bounding sphere and moved in whole texels, so edges do not shimmer.
  - **Spot:** up to 2 per frame (the most important). A 1024×1024 perspective map of the cone, out to the range.
  - **Point:** 1 per frame. Six 90° faces in a 3×2 atlas of 512×512 faces, out to the range; the shader picks the face.
  - All are filtered with 3×3 PCF with a slope-scaled depth bias. Depth is packed into RGBA8 targets, so it works on every device.
  - **Cutout surfaces** (`_AlphaCutoff` above 0) cast the shape they draw. Transparent surfaces do not cast shadows.
- **Custom shaders** (with a custom vertex stage on meshes) can read the same uniforms. The renderer sets them on every material that declares them; material files cannot set them:
  - `vec4 uCameraPosition`;
  - `vec4 uAmbient` (w = 0 in an unlit scene);
  - `int uLightCount` and `mat4 uLight0`…`uLight7`, packed as documented on `LightingUniforms`;
  - `mat4 uShadowMatrix`, `vec4 uShadowParams` and `sampler2D uShadowMap`.
- **Editor previews.** Material and model previews are lit by a fixed key light and fill.
- **Profiling.** The shadow pass is the profiler sample `Render.Shadows`.

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
