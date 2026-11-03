# Materials and 2D Lighting

## Materials (`.mnvtmat`)

```json
{
  "schemaVersion": 1,
  "shader": "asset:0f5e0000-0000-4000-8000-000000000070",
  "blend": "Alpha",
  "uniforms": { "uGlow": [0.9, 0.5, 0.1, 1.0], "uSpeed": 4.0 }
}
```

| Field | Meaning |
|---|---|
| `shader` | `"builtin:sprite"` (default), or a fragment-shader asset (`.frag`, `.glsl`) |
| `blend` | `Opaque`, `Alpha` (default), `Premultiplied`, `Additive`, `Multiply` |
| `uniforms` | A number, or an array of 1, 2 or 4 numbers, per uniform. `uViewProjection`, `uTexture` and `uTime` are reserved. |

**Custom shaders.** A custom shader is the **fragment stage** for the built-in sprite vertex shader:

- It receives `vTexCoord`, `vColor` and `uTexture` (binding 0).
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
- **Validation.** The importer (and `mnvt build`) validates the JSON and records the shader as a dependency. GLSL compiles when the material is first used, because compilation needs a GPU.
- **Fallback.** An invalid material or a shader that fails to compile logs one error with the compiler output, and the sprite draws with the default material.
- **Reloading.** Editing a material or its shader reloads it in the editor.

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
