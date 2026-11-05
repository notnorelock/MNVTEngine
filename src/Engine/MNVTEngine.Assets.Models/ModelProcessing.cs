using MNVTEngine.Graphics;
using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;
using StbImageWriteSharp;

namespace MNVTEngine.Assets.Models;

/// <summary>Steps every reader shares: unique names, generated normals and tangents, the import scale, validation.</summary>
internal static class ModelProcessing
{
    /// <param name="inputs">Assets whose content the import reads (the maps combined into metallic-roughness maps).</param>
    /// <param name="assetFile">The file of a project asset (remapped textures), to read it.</param>
    public static void Finish(ModelData model, ModelImportSettings settings, ICollection<string> warnings, ISet<Guid> dependencies, ISet<Guid>? inputs = null, Func<Guid, string?>? assetFile = null)
    {
        var textureNames = UniqueNames(model.Textures.Select(t => t.Name), "Texture");
        for (var i = 0; i < model.Textures.Count; i++)
        {
            var texture = model.Textures[i];
            texture.Name = textureNames[i];
            if (settings.Textures.TryGetValue(texture.Name, out var replacement))
            {
                (texture.ProjectAsset, texture.Embedded, texture.Problem, texture.FullPath) = (replacement, null, null, assetFile?.Invoke(replacement));
                dependencies.Add(replacement);
            }
            else if (texture.Problem is { } problem)
            {
                warnings.Add(problem);
            }
        }

        foreach (var material in model.Materials)
        {
            if (settings.Materials.TryGetValue(material.Name, out var replacement))
            {
                material.Override = replacement;
                dependencies.Add(replacement);
            }
        }

        foreach (var stale in settings.Materials.Keys.Where(k => model.FindMaterial(k) is null))
        {
            warnings.Add($"The material remap for '{stale}' matches no material of the model (renamed in the source?); it is ignored.");
        }

        foreach (var stale in settings.Textures.Keys.Where(k => model.Textures.TrueForAll(t => t.Name != k)))
        {
            warnings.Add($"The texture remap for '{stale}' matches no texture of the model (renamed in the source?); it is ignored.");
        }

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

            var shapeNames = UniqueNames(mesh.BlendShapes.Select(s => s.Name), "Shape");
            for (var i = 0; i < mesh.BlendShapes.Count; i++)
            {
                mesh.BlendShapes[i].Name = shapeNames[i];
            }

            while (mesh.DefaultBlendShapeWeights.Count < mesh.BlendShapes.Count)
            {
                mesh.DefaultBlendShapeWeights.Add(0f);
            }
        }

        RemoveInvalidMeshes(model, warnings);
        foreach (var (alias, current) in settings.MeshAliases)
        {
            if (model.Meshes.Exists(m => m.Name == alias))
            {
                warnings.Add($"The mesh alias '{alias}' is a mesh of the model again; the alias is ignored.");
            }
            else if (model.Meshes.Exists(m => m.Name == current))
            {
                model.MeshAliases[alias] = current;
            }
            else
            {
                warnings.Add($"The mesh alias '{alias}' points to '{current}', which is not a mesh of the model; it is ignored.");
            }
        }

        foreach (var mesh in model.Meshes.Where(m => m.Mesh.UV1 is null))
        {
            foreach (var material in mesh.SubMeshMaterials.Where(i => i >= 0 && i < model.Materials.Count).Select(i => model.Materials[i]).Distinct())
            {
                if (material.UVSets.Values.Any(set => set == 1))
                {
                    warnings.Add($"Material '{material.Name}' samples a texture with UV set 1, which mesh '{mesh.Name}' does not have.");
                }
            }
        }

        CombineMetallicRoughness(model, warnings, inputs);
    }

    /// <summary>
    /// The Standard shader reads metallic and roughness from one glTF-layout map (G = roughness, B = metallic). Materials
    /// with separate maps (FBX) get one baked from them: the maps' red channel, resampled to the larger size; a missing
    /// map leaves its channel at 1 so the factor alone applies. A material with a map uses factor 1 for it, so the map
    /// decides, as in the modelling tool. The baked map is embedded in the artifact (Extract Textures writes it out).
    /// </summary>
    private static void CombineMetallicRoughness(ModelData model, ICollection<string> warnings, ISet<Guid>? inputs)
    {
        var baked = new Dictionary<(int Metallic, int Roughness), int>();
        foreach (var material in model.Materials)
        {
            var hasMetallic = material.Textures.TryGetValue(TextureRole.Metallic, out var metallicIndex);
            var hasRoughness = material.Textures.TryGetValue(TextureRole.Roughness, out var roughnessIndex);
            if (material.Textures.ContainsKey(TextureRole.MetallicRoughness) || (!hasMetallic && !hasRoughness))
            {
                continue;
            }

            var key = (hasMetallic ? metallicIndex : -1, hasRoughness ? roughnessIndex : -1);
            if (!baked.TryGetValue(key, out var combined))
            {
                var metallic = hasMetallic ? Decode(model.Textures[metallicIndex], material.Name, "metallic", warnings, inputs) : null;
                var roughness = hasRoughness ? Decode(model.Textures[roughnessIndex], material.Name, "roughness", warnings, inputs) : null;
                if ((hasMetallic && metallic is null) || (hasRoughness && roughness is null))
                {
                    continue; // reported by Decode; the factors apply
                }

                combined = model.Textures.Count;
                var name = UniqueNames(model.Textures.Select(t => t.Name).Append($"{material.Name}_MetallicRoughness"), "Texture")[^1];
                model.Textures.Add(new ModelTexture(name) { Embedded = Bake(metallic, roughness) });
                baked[key] = combined;
            }

            material.Textures[TextureRole.MetallicRoughness] = combined;
            if (hasMetallic)
            {
                material.Metallic = 1f;
            }

            if (hasRoughness)
            {
                material.Roughness = 1f;
            }

            var uvSet = material.UVSets.GetValueOrDefault(hasRoughness ? TextureRole.Roughness : TextureRole.Metallic);
            if (uvSet != 0)
            {
                material.UVSets[TextureRole.MetallicRoughness] = uvSet;
            }
        }
    }

    /// <summary>A map's pixels: embedded, or the project file it was resolved to (then read, so it becomes an import input).</summary>
    private static Image? Decode(ModelTexture texture, string material, string role, ICollection<string> warnings, ISet<Guid>? inputs)
    {
        byte[] bytes;
        if (texture.Embedded is { } embedded)
        {
            bytes = embedded;
        }
        else if (texture.FullPath is { } path && texture.ProjectAsset is { } guid)
        {
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (IOException ex)
            {
                warnings.Add($"Material '{material}': the {role} map '{texture.Name}' cannot be read to combine it into a metallic-roughness map ({ex.Message}); the factor applies.");
                return null;
            }

            inputs?.Add(guid);
        }
        else
        {
            // Unresolved (reported already): nothing to read.
            warnings.Add($"Material '{material}': the {role} map '{texture.Name}' is missing, so it is not combined into a metallic-roughness map; the factor applies.");
            return null;
        }

        try
        {
            return Image.Decode(bytes, texture.Name);
        }
        catch (InvalidDataException ex)
        {
            warnings.Add($"Material '{material}': {ex.Message}; the {role} factor applies.");
            return null;
        }
    }

    /// <summary>A glTF-layout metallic-roughness PNG: R 255, G roughness, B metallic, A 255 (missing maps: 255).</summary>
    internal static byte[] Bake(Image? metallic, Image? roughness)
    {
        var width = Math.Max(metallic?.Width ?? 1, roughness?.Width ?? 1);
        var height = Math.Max(metallic?.Height ?? 1, roughness?.Height ?? 1);
        var pixels = new byte[width * height * 4];
        static byte Sample(Image? image, int x, int y, int width, int height) =>
            image is null ? (byte)255 : image.Pixels[((((y * image.Height) / height) * image.Width) + ((x * image.Width) / width)) * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)255, Sample(roughness, x, y, width, height), Sample(metallic, x, y, width, height), (byte)255);
            }
        }

        using var stream = new MemoryStream();
        new ImageWriter().WritePng(pixels, width, height, ColorComponents.RedGreenBlueAlpha, stream);
        return stream.ToArray();
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

    /// <summary>
    /// Drops meshes that cannot be drawn (reported above) and renumbers the nodes' mesh indices to match. Nodes of a
    /// dropped mesh draw nothing: they lose the mesh, its skin and their blend shape weight channels.
    /// </summary>
    private static void RemoveInvalidMeshes(ModelData model, ICollection<string> warnings)
    {
        var newIndex = new int[model.Meshes.Count];
        var kept = new List<ModelMesh>();
        for (var i = 0; i < model.Meshes.Count; i++)
        {
            newIndex[i] = model.Meshes[i].Mesh.Validate().Count == 0 ? kept.Count : -1;
            if (newIndex[i] >= 0)
            {
                kept.Add(model.Meshes[i]);
            }
        }

        if (kept.Count == model.Meshes.Count)
        {
            return;
        }

        for (var n = 0; n < model.Nodes.Count; n++)
        {
            var node = model.Nodes[n];
            if (node.Mesh < 0 || node.Mesh >= newIndex.Length)
            {
                continue;
            }

            if (newIndex[node.Mesh] < 0)
            {
                warnings.Add($"Node '{node.Name}' draws nothing: its mesh '{model.Meshes[node.Mesh].Name}' is invalid.");
                model.Nodes[n] = node with { Mesh = -1, Skin = -1 };
                foreach (var animation in model.Animations)
                {
                    animation.Channels.RemoveAll(c => c.Node == n && c.Path == AnimationPath.Weights);
                }
            }
            else
            {
                model.Nodes[n] = node with { Mesh = newIndex[node.Mesh] };
            }
        }

        model.Meshes.Clear();
        model.Meshes.AddRange(kept);
    }

    /// <summary>A linear color component (glTF light colors) to sRGB (engine colors).</summary>
    public static float LinearToSrgb(float c) => c <= 0.0031308f ? 12.92f * c : (1.055f * MathF.Pow(c, 1f / 2.4f)) - 0.055f;

    /// <summary>The roughness that matches a Phong specular exponent (Blinn-Phong to GGX approximation).</summary>
    public static float RoughnessFromShininess(float shininess) => shininess <= 0f ? 1f : Mathf.Clamp01(MathF.Sqrt(2f / (shininess + 2f)));
}

/// <summary>
/// Finds the project texture an external texture path refers to: the path relative to the model, then the file name
/// next to the model, in a "textures" folder beside it, and finally a unique file of that name anywhere in Assets/.
/// Found textures become dependencies (the model reimports when they change). Missing ones and files the engine cannot
/// read (e.g. EXR) are reported, never replaced silently.
/// </summary>
internal sealed class TextureResolver(ModelSource source, ISet<Guid> dependencies)
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
            texture.Problem = $"Material '{material}': {role} texture '{sourcePath}' cannot be resolved (the model was imported from memory).";
            return texture;
        }

        var (file, ambiguous) = FindFile(sourcePath);
        if (file is null)
        {
            texture.Problem = ambiguous ?? $"Material '{material}': {role} texture '{sourcePath}' was not found next to the model or in Assets/.";
            return texture;
        }

        var assets = Path.GetFullPath(source.AssetsDirectory) + Path.DirectorySeparatorChar;
        if (!file.StartsWith(assets, StringComparison.OrdinalIgnoreCase))
        {
            texture.Problem = $"Material '{material}': {role} texture '{sourcePath}' is outside Assets/ ({file}); copy it into the project (Extract Textures).";
            return texture;
        }

        var relative = file[assets.Length..].Replace('\\', '/');
        if (source.ResolveAsset(relative) is not { } guid)
        {
            texture.Problem = $"Material '{material}': {role} texture '{relative}' is not an asset.";
            return texture;
        }

        dependencies.Add(guid);
        if (!IsSupportedImage(file))
        {
            texture.Problem = $"Material '{material}': {role} texture '{relative}' is in a format the engine cannot load ({Path.GetExtension(file)}); convert it to PNG, JPEG, TGA or BMP, then remap the texture.";
            return texture;
        }

        (texture.ProjectAsset, texture.FullPath) = (guid, file);
        return texture;
    }

    private static bool IsSupportedImage(string file) =>
        Path.GetExtension(file).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp";

    /// <summary>The file a source path refers to, or null with the reason when several candidates match.</summary>
    private (string? File, string? Ambiguous) FindFile(string sourcePath)
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
                return (Path.GetFullPath(candidate), null);
            }
        }

        _allFiles ??= Directory.EnumerateFiles(source.AssetsDirectory!, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)).ToArray();
        var matches = _allFiles.Where(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1)
        {
            return (null, $"Texture '{sourcePath}': {matches.Count} files named '{name}' in Assets/; none was chosen. Put the texture next to the model or remap it.");
        }

        return (matches.Count == 1 ? Path.GetFullPath(matches[0]) : null, null);
    }
}
