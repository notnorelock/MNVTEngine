using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics.Materials;

/// <summary>The texture a material slot falls back to when none is assigned.</summary>
public enum DefaultTexture
{
    /// <summary>(1, 1, 1, 1): multiplies to no change.</summary>
    White,

    /// <summary>(0, 0, 0, 1).</summary>
    Black,

    /// <summary>(0.5, 0.5, 1): the unperturbed tangent-space normal.</summary>
    FlatNormal,
}

/// <summary>How an editor shows a property.</summary>
public enum MaterialPropertyUI
{
    Float,
    Range,
    Color,
    HdrColor,
    Texture,
}

/// <summary>A property of a built-in shader, with its default and how to edit it.</summary>
public sealed record MaterialPropertyInfo(string Name, string Label, MaterialPropertyUI UI, Vector4 Default, float Min = 0f, float Max = 1f, DefaultTexture Texture = DefaultTexture.White, string? Tooltip = null)
{
    public bool IsTexture => UI == MaterialPropertyUI.Texture;
}

/// <summary>
/// The built-in 3D shaders and their properties (glTF 2.0 metallic-roughness names).
/// <list type="bullet">
/// <item><b>Standard.</b> PBR inputs: base color and map, metallic, roughness, the metallic-roughness map (G =
/// roughness, B = metallic), normal map and scale, occlusion map and strength, emission and map, alpha cutoff.
/// Lit by the scene's lights (GGX specular, Smith geometry, Schlick Fresnel, in linear space) with ambient light and
/// a directional shadow map; a scene without lights shows base color × occlusion + emission.</item>
/// <item><b>Unlit.</b> Base color × map × vertex color, with alpha cutoff.</item>
/// </list>
/// </summary>
public static class MaterialShaders
{
    /// <summary>Id of the built-in shadow caster shader (<see cref="ShadowCaster"/>); not selectable in material files.</summary>
    public const string ShadowCasterId = "builtin:shadow-caster";

    private const string MeshVertex = """
        #version 450 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec4 aTangent;
        layout(location = 3) in vec2 aTexCoord;
        layout(location = 5) in vec4 aColor;

        uniform mat4 uViewProjection;
        uniform mat4 uModel;
        uniform mat4 uNormalMatrix;

        out vec3 vWorldPosition;
        out vec3 vNormal;
        out vec4 vTangent;
        out vec2 vTexCoord;
        out vec4 vColor;

        void main()
        {
            vec4 world = uModel * vec4(aPosition, 1.0);
            vWorldPosition = world.xyz;
            vNormal = normalize(mat3(uNormalMatrix) * aNormal);
            vTangent = vec4(normalize(mat3(uModel) * aTangent.xyz), aTangent.w);
            vTexCoord = aTexCoord;
            vColor = aColor;
            gl_Position = uViewProjection * world;
        }
        """;

    public static ShaderSource Standard { get; } = new(
        "Builtin/Standard",
        MeshVertex,
        """
        #version 450 core
        in vec3 vWorldPosition;
        in vec3 vNormal;
        in vec4 vTangent;
        in vec2 vTexCoord;
        in vec4 vColor;

        uniform vec4 _BaseColor;
        uniform float _Metallic;
        uniform float _Roughness;
        uniform float _NormalScale;
        uniform vec4 _EmissiveColor;
        uniform float _OcclusionStrength;
        uniform float _AlphaCutoff;
        uniform sampler2D _BaseColorMap;
        uniform sampler2D _MetallicRoughnessMap;
        uniform sampler2D _NormalMap;
        uniform sampler2D _OcclusionMap;
        uniform sampler2D _EmissiveMap;

        uniform vec4 uCameraPosition;
        uniform vec4 uAmbient;
        uniform int uLightCount;
        uniform mat4 uLight0;
        uniform mat4 uLight1;
        uniform mat4 uLight2;
        uniform mat4 uLight3;
        uniform mat4 uLight4;
        uniform mat4 uLight5;
        uniform mat4 uLight6;
        uniform mat4 uLight7;
        uniform mat4 uShadowMatrix;
        uniform vec4 uShadowParams;
        uniform sampler2D uShadowMap;

        out vec4 fragColor;

        const float PI = 3.14159265;

        // Colors and textures are stored in sRGB (the swapchain is UNORM): light in linear space, write sRGB.
        vec3 toLinear(vec3 c) { return pow(max(c, vec3(0.0)), vec3(2.2)); }
        vec3 toSrgb(vec3 c) { return pow(max(c, vec3(0.0)), vec3(1.0 / 2.2)); }

        float unpackDepth(vec4 rgba) { return dot(rgba, vec4(1.0, 1.0 / 255.0, 1.0 / 65025.0, 1.0 / 16581375.0)); }

        // 3x3 percentage-closer filtering over the packed shadow map; 1 = lit.
        float shadowVisibility(vec3 normal, vec3 toLight)
        {
            vec4 clip = uShadowMatrix * vec4(vWorldPosition, 1.0);
            vec3 ndc = clip.xyz / clip.w;
            vec2 uv = vec2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5); // the viewport is flipped (negative height): +y is row 0
            if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || ndc.z > 1.0)
            {
                return 1.0;
            }

            float slope = 1.0 - clamp(dot(normal, toLight), 0.0, 1.0);
            float bias = uShadowParams.y * (1.0 + 3.0 * slope);
            float lit = 0.0;
            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    float stored = unpackDepth(texture(uShadowMap, uv + vec2(x, y) * uShadowParams.z));
                    lit += ndc.z - bias <= stored ? 1.0 : 0.0;
                }
            }

            return lit / 9.0;
        }

        float distributionGgx(float nDotH, float roughness)
        {
            float a = roughness * roughness;
            float a2 = a * a;
            float d = nDotH * nDotH * (a2 - 1.0) + 1.0;
            return a2 / max(PI * d * d, 1e-6);
        }

        float geometrySmith(float nDotV, float nDotL, float roughness)
        {
            float k = (roughness + 1.0) * (roughness + 1.0) / 8.0;
            return (nDotV / (nDotV * (1.0 - k) + k)) * (nDotL / (nDotL * (1.0 - k) + k));
        }

        vec3 shade(mat4 light, vec3 n, vec3 v, vec3 albedo, float metallic, float roughness, vec3 f0)
        {
            int type = int(light[0].w + 0.5);
            vec3 l;
            float attenuation = 1.0;
            if (type == 0)
            {
                l = -normalize(light[1].xyz);
            }
            else
            {
                vec3 toLight = light[0].xyz - vWorldPosition;
                float distance = length(toLight);
                l = toLight / max(distance, 1e-4);
                float range = max(light[1].w, 1e-4);
                float ratio = distance / range;
                float window = clamp(1.0 - ratio * ratio * ratio * ratio, 0.0, 1.0);
                attenuation = window * window / (1.0 + 25.0 * ratio * ratio);
                if (type == 2)
                {
                    float cosAngle = dot(-l, normalize(light[1].xyz));
                    attenuation *= smoothstep(light[3].y, max(light[3].x, light[3].y + 1e-4), cosAngle);
                }
            }

            float nDotL = max(dot(n, l), 0.0);
            if (nDotL <= 0.0 || attenuation <= 0.0)
            {
                return vec3(0.0);
            }

            if (light[3].z > -0.5 && uShadowParams.x > 0.5)
            {
                attenuation *= mix(1.0, shadowVisibility(n, l), light[3].w);
            }

            vec3 h = normalize(v + l);
            float nDotV = max(dot(n, v), 1e-4);
            vec3 f = f0 + (1.0 - f0) * pow(1.0 - max(dot(h, v), 0.0), 5.0);
            vec3 specular = distributionGgx(max(dot(n, h), 0.0), roughness) * geometrySmith(nDotV, nDotL, roughness) * f / (4.0 * nDotV * nDotL + 1e-4);
            vec3 diffuse = (1.0 - f) * (1.0 - metallic) * albedo / PI;
            vec3 radiance = toLinear(light[2].rgb) * light[2].w * attenuation;
            // Scaled by PI so a white light of intensity 1 lights a white diffuse surface facing it to full white.
            return (diffuse + specular) * radiance * nDotL * PI;
        }

        void main()
        {
            vec4 base = texture(_BaseColorMap, vTexCoord) * _BaseColor * vColor;
            if (base.a < _AlphaCutoff)
            {
                discard;
            }

            float occlusion = mix(1.0, texture(_OcclusionMap, vTexCoord).r, _OcclusionStrength);
            vec3 emission = texture(_EmissiveMap, vTexCoord).rgb * _EmissiveColor.rgb;
            if (uAmbient.w < 0.5)
            {
                // Unlit scene (no lights): the surface at full brightness.
                fragColor = vec4(base.rgb * occlusion + emission, base.a);
                return;
            }

            vec4 metallicRoughness = texture(_MetallicRoughnessMap, vTexCoord);
            float metallic = clamp(_Metallic * metallicRoughness.b, 0.0, 1.0);
            float roughness = clamp(_Roughness * metallicRoughness.g, 0.04, 1.0);

            vec3 n = normalize(vNormal);
            vec3 tangent = vTangent.xyz - n * dot(n, vTangent.xyz);
            if (dot(tangent, tangent) > 1e-8)
            {
                tangent = normalize(tangent);
                vec3 bitangent = cross(n, tangent) * (vTangent.w < 0.0 ? -1.0 : 1.0);
                vec3 mapped = texture(_NormalMap, vTexCoord).xyz * 2.0 - 1.0;
                mapped.xy *= _NormalScale;
                n = normalize(mat3(tangent, bitangent, n) * mapped);
            }

            vec3 v = normalize(uCameraPosition.xyz - vWorldPosition);
            if (!gl_FrontFacing)
            {
                n = -n; // double-sided surfaces seen from behind
            }

            vec3 albedo = toLinear(base.rgb);
            vec3 f0 = mix(vec3(0.04), albedo, metallic);
            vec3 color = vec3(0.0);
            mat4 lights[8] = mat4[8](uLight0, uLight1, uLight2, uLight3, uLight4, uLight5, uLight6, uLight7);
            for (int i = 0; i < 8; i++)
            {
                if (i >= uLightCount)
                {
                    break;
                }

                color += shade(lights[i], n, v, albedo, metallic, roughness, f0);
            }

            // Ambient light from all around: diffuse plus a rough reflection, so metals are not black without lights.
            vec3 fAmbient = f0 + (max(vec3(1.0 - roughness), f0) - f0) * pow(1.0 - max(dot(n, v), 0.0), 5.0);
            vec3 ambient = toLinear(uAmbient.rgb) * ((1.0 - fAmbient) * (1.0 - metallic) * albedo + fAmbient) * occlusion;
            color += ambient + toLinear(emission);
            fragColor = vec4(toSrgb(color), base.a);
        }
        """);

    /// <summary>
    /// Writes the depth seen from a light into an RGBA8 target, 8 bits per channel (see <c>ShadowMap</c>); the Standard
    /// shader unpacks it. Built in; not a material shader.
    /// </summary>
    public static ShaderSource ShadowCaster { get; } = new(
        "Builtin/ShadowCaster",
        """
        #version 450 core
        layout(location = 0) in vec3 aPosition;

        uniform mat4 uViewProjection;
        uniform mat4 uModel;

        void main()
        {
            gl_Position = uViewProjection * uModel * vec4(aPosition, 1.0);
        }
        """,
        """
        #version 450 core
        out vec4 fragColor;

        void main()
        {
            vec4 packed = fract(vec4(1.0, 255.0, 65025.0, 16581375.0) * gl_FragCoord.z);
            packed -= packed.yzww * vec4(1.0 / 255.0, 1.0 / 255.0, 1.0 / 255.0, 0.0);
            fragColor = packed;
        }
        """);

    public static ShaderSource Unlit { get; } = new(
        "Builtin/Unlit",
        MeshVertex,
        """
        #version 450 core
        in vec3 vWorldPosition;
        in vec3 vNormal;
        in vec4 vTangent;
        in vec2 vTexCoord;
        in vec4 vColor;

        uniform vec4 _BaseColor;
        uniform float _AlphaCutoff;
        uniform sampler2D _BaseColorMap;

        out vec4 fragColor;

        void main()
        {
            vec4 color = texture(_BaseColorMap, vTexCoord) * _BaseColor * vColor;
            if (color.a < _AlphaCutoff)
            {
                discard;
            }

            fragColor = color;
        }
        """);

    private static readonly Vector4 One = new(1f, 1f, 1f, 1f);

    public static IReadOnlyList<MaterialPropertyInfo> StandardProperties { get; } =
    [
        new("_BaseColor", "Base color", MaterialPropertyUI.Color, One),
        new("_BaseColorMap", "Base color map", MaterialPropertyUI.Texture, One, Tooltip: "Albedo in sRGB; alpha is opacity."),
        new("_Metallic", "Metallic", MaterialPropertyUI.Range, new Vector4(0f, 0f, 0f, 0f)),
        new("_Roughness", "Roughness", MaterialPropertyUI.Range, new Vector4(0.5f, 0f, 0f, 0f)),
        new("_MetallicRoughnessMap", "Metallic-roughness map", MaterialPropertyUI.Texture, One, Tooltip: "glTF layout: G = roughness, B = metallic (multiplied by the factors)."),
        new("_NormalMap", "Normal map", MaterialPropertyUI.Texture, One, Texture: DefaultTexture.FlatNormal, Tooltip: "Tangent-space normals (OpenGL convention, +Y up)."),
        new("_NormalScale", "Normal scale", MaterialPropertyUI.Float, One),
        new("_OcclusionMap", "Occlusion map", MaterialPropertyUI.Texture, One, Tooltip: "Ambient occlusion in the R channel."),
        new("_OcclusionStrength", "Occlusion strength", MaterialPropertyUI.Range, One),
        new("_EmissiveColor", "Emission", MaterialPropertyUI.HdrColor, new Vector4(0f, 0f, 0f, 1f)),
        new("_EmissiveMap", "Emission map", MaterialPropertyUI.Texture, One),
        new("_AlphaCutoff", "Alpha cutoff", MaterialPropertyUI.Range, new Vector4(0f, 0f, 0f, 0f), Tooltip: "Pixels with lower alpha are discarded (0 = off). Above 0 the material draws in the cutout queue."),
    ];

    public static IReadOnlyList<MaterialPropertyInfo> UnlitProperties { get; } =
        [.. StandardProperties.Where(p => p.Name is "_BaseColor" or "_BaseColorMap" or "_AlphaCutoff")];

    /// <summary>The documented properties of a built-in shader id (<c>builtin:standard</c>, <c>builtin:unlit</c>); empty otherwise.</summary>
    public static IReadOnlyList<MaterialPropertyInfo> PropertiesOf(string? builtin) => builtin switch
    {
        "builtin:standard" => StandardProperties,
        "builtin:unlit" => UnlitProperties,
        _ => [],
    };

    /// <summary>
    /// Gives every property of the material's shader a value: the documented default for built-in properties, zero for
    /// other uniforms, and a default texture (flat normal for names containing "Normal", white otherwise) for samplers.
    /// </summary>
    public static void ApplyDefaults(Material material, IReadOnlyList<MaterialPropertyInfo> documented, Func<DefaultTexture, Texture2D> textures)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(documented);
        ArgumentNullException.ThrowIfNull(textures);
        foreach (var info in documented)
        {
            if (info.IsTexture)
            {
                if (material.GetTexture(info.Name) is null)
                {
                    material.SetTexture(info.Name, textures(info.Texture));
                }
            }
            else if (!material.TryGetUniform(info.Name, out _))
            {
                _ = info.UI is MaterialPropertyUI.Color or MaterialPropertyUI.HdrColor ? material.SetVector(info.Name, info.Default) : material.SetFloat(info.Name, info.Default.X);
            }
        }

        foreach (var property in material.Shader.Properties)
        {
            if (property.Kind == ShaderPropertyKind.Texture && material.GetTexture(property.Name) is null)
            {
                material.SetTexture(property.Name, textures(property.Name.Contains("Normal", StringComparison.Ordinal) ? DefaultTexture.FlatNormal : DefaultTexture.White));
            }
        }
    }
}
