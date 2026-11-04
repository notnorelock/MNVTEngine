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
/// Until scenes have lights it shows the surface at full ambient light: base color × occlusion + emission. The
/// metallic, roughness and normal inputs are bound and used by the lighting shader.</item>
/// <item><b>Unlit.</b> Base color × map × vertex color, with alpha cutoff.</item>
/// </list>
/// </summary>
public static class MaterialShaders
{
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
        uniform vec4 _EmissiveColor;
        uniform float _OcclusionStrength;
        uniform float _AlphaCutoff;
        uniform sampler2D _BaseColorMap;
        uniform sampler2D _OcclusionMap;
        uniform sampler2D _EmissiveMap;

        out vec4 fragColor;

        void main()
        {
            vec4 base = texture(_BaseColorMap, vTexCoord) * _BaseColor * vColor;
            if (base.a < _AlphaCutoff)
            {
                discard;
            }

            float occlusion = mix(1.0, texture(_OcclusionMap, vTexCoord).r, _OcclusionStrength);
            vec3 emission = texture(_EmissiveMap, vTexCoord).rgb * _EmissiveColor.rgb;
            fragColor = vec4(base.rgb * occlusion + emission, base.a);
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
