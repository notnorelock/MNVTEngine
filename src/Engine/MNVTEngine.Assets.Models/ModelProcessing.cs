using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;

namespace MNVTEngine.Assets.Models;

/// <summary>Steps every reader shares: unique names, generated normals and tangents, the import scale, validation.</summary>
internal static class ModelProcessing
{
    public static void Finish(ModelData model, ModelImportSettings settings, ICollection<string> warnings)
    {
        if (settings.Scale != 1f && model.Nodes.Count > 0)
        {
            var root = model.Nodes[0];
            model.Nodes[0] = root with { Position = root.Position * settings.Scale, Scale = root.Scale * settings.Scale };
        }

        foreach (var mesh in model.Meshes)
        {
            var m = mesh.Mesh;
            if (m.Normals is null && settings.GenerateMissingNormals)
            {
                m.RecalculateNormals();
            }

            if (m.Tangents is null && settings.GenerateTangents && m.Normals is not null && m.UV0 is not null)
            {
                m.RecalculateTangents();
            }

            foreach (var problem in m.Validate())
            {
                warnings.Add($"Mesh '{mesh.Name}': {problem}");
            }

            if (mesh.MorphTargetCount > 0)
            {
                warnings.Add($"Mesh '{mesh.Name}' has {mesh.MorphTargetCount} morph target(s) (blend shapes); they are not imported yet, the mesh keeps its base shape.");
            }
        }

        model.Meshes.RemoveAll(m => m.Mesh.Validate().Count > 0);
        foreach (var material in model.Materials)
        {
            if (!material.Textures.ContainsKey(TextureRole.MetallicRoughness)
                && (material.Textures.ContainsKey(TextureRole.Metallic) || material.Textures.ContainsKey(TextureRole.Roughness)))
            {
                warnings.Add($"Material '{material.Name}': separate metallic/roughness maps are not combined yet; the metallic and roughness factors are used.");
            }
        }
    }

    /// <summary>Makes names unique and safe for sub-asset references: "Body", "Body_1", ...; empty names get a fallback.</summary>
    public static List<string> UniqueNames(IEnumerable<string?> names, string fallback)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var raw in names)
        {
            var name = Clean(raw);
            if (name.Length == 0)
            {
                name = fallback;
            }

            var candidate = name;
            for (var i = 1; !used.Add(candidate); i++)
            {
                candidate = $"{name}_{i}";
            }

            result.Add(candidate);
        }

        return result;
    }

    private static string Clean(string? name) =>
        new string((name ?? "").Trim().Select(c => c is '#' or '"' or '/' or '\\' || char.IsControl(c) ? '_' : c).ToArray());

    /// <summary>The roughness that matches a Phong specular exponent (Blinn-Phong to GGX approximation).</summary>
    public static float RoughnessFromShininess(float shininess) => shininess <= 0f ? 1f : Mathf.Clamp01(MathF.Sqrt(2f / (shininess + 2f)));
}

/// <summary>
/// Finds the project texture an external texture path refers to: the path relative to the model, then the file name
/// next to the model, in a "textures" folder beside it, and finally a unique file of that name anywhere in Assets/.
/// Found textures become dependencies (the model reimports when they change). Missing ones and files the engine cannot
/// read (e.g. EXR) are reported, never replaced silently.
/// </summary>
internal sealed class TextureResolver(ModelSource source, ICollection<string> warnings, ISet<Guid> dependencies)
{
    private readonly Dictionary<string, ModelTexture> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private string[]? _allFiles;

    public ModelTexture Resolve(string sourcePath, string material, string role)
    {
        if (_byPath.TryGetValue(sourcePath, out var known))
        {
            return known;
        }

        var texture = new ModelTexture(Path.GetFileNameWithoutExtension(sourcePath)) { SourcePath = sourcePath };
        _byPath[sourcePath] = texture;
        if (source.FullPath is null || source.AssetsDirectory is null)
        {
            warnings.Add($"Material '{material}': {role} texture '{sourcePath}' cannot be resolved (the model was imported from memory).");
            return texture;
        }

        if (FindFile(sourcePath) is not { } file)
        {
            warnings.Add($"Material '{material}': {role} texture '{sourcePath}' was not found next to the model or in Assets/.");
            return texture;
        }

        var assets = Path.GetFullPath(source.AssetsDirectory) + Path.DirectorySeparatorChar;
        if (!file.StartsWith(assets, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"Material '{material}': {role} texture '{sourcePath}' is outside Assets/ ({file}); copy it into the project (Extract Textures).");
            return texture;
        }

        var relative = file[assets.Length..].Replace('\\', '/');
        if (source.ResolveAsset(relative) is not { } guid)
        {
            warnings.Add($"Material '{material}': {role} texture '{relative}' is not an asset.");
            return texture;
        }

        dependencies.Add(guid);
        if (!IsSupportedImage(file))
        {
            warnings.Add($"Material '{material}': {role} texture '{relative}' is in a format the engine cannot load ({Path.GetExtension(file)}); convert it to PNG, JPEG, TGA or BMP.");
            return texture;
        }

        texture.ProjectAsset = guid;
        return texture;
    }

    private static bool IsSupportedImage(string file) =>
        Path.GetExtension(file).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp";

    private string? FindFile(string sourcePath)
    {
        var modelDirectory = Path.GetDirectoryName(source.FullPath!)!;
        var normalized = sourcePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var name = Path.GetFileName(normalized);
        foreach (var candidate in new[]
        {
            Path.IsPathRooted(normalized) ? normalized : Path.Combine(modelDirectory, normalized),
            Path.Combine(modelDirectory, name),
            Path.Combine(modelDirectory, "textures", name),
            Path.Combine(modelDirectory, "Textures", name),
        })
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        _allFiles ??= Directory.EnumerateFiles(source.AssetsDirectory!, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)).ToArray();
        var matches = _allFiles.Where(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1)
        {
            warnings.Add($"Texture '{sourcePath}': {matches.Count} files named '{name}' in Assets/; none was chosen. Put the texture next to the model.");
            return null;
        }

        return matches.Count == 1 ? Path.GetFullPath(matches[0]) : null;
    }
}
