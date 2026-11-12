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

/// <summary>Material property defaults shared by every renderer.</summary>
public static class MaterialProperties
{
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
