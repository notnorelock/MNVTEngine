using System.Globalization;
using System.Security.Cryptography;
using MNVTEngine.Assets;
using MNVTEngine.Assets.Models;
using MNVTEngine.Serialization;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Editor.Services;

/// <summary>
/// Editing an imported model's materials and textures, as in Unity's model import inspector. UI-free and testable.
/// <list type="bullet">
/// <item><b>Extract Textures</b> writes embedded textures, and textures found outside <c>Assets/</c>, as project files
/// with deterministic names (<c>&lt;Model&gt;_&lt;Texture&gt;.png</c>, identical content written once) and remaps the
/// model to them.</item>
/// <item><b>Extract Materials</b> writes a <c>.mnvtmat</c> per material (<c>&lt;Model&gt;_&lt;Material&gt;.mnvtmat</c>, extracting
/// the textures it needs) and remaps the model to it. Existing files are reused, never overwritten, so extracting
/// again creates no duplicates and keeps edits.</item>
/// <item><b>Remap</b> points a source material at any project material; <b>Reset</b> clears the material remaps.</item>
/// <item><b>Relink</b> finds mesh names that scenes and prefabs reference but the model no longer has (renamed in the
/// source) and points them at a current mesh through a mesh alias.</item>
/// </list>
/// Every change goes through the model's import settings (its <c>.meta</c>) and reimports it. Source files are never modified.
/// </summary>
public sealed class ModelWorkflow
{
    private readonly AssetDatabase _assets;

    /// <exception cref="KeyNotFoundException">No such asset.</exception>
    /// <exception cref="ArgumentException">The asset is not a model.</exception>
    public ModelWorkflow(AssetDatabase assets, Guid model)
    {
        ArgumentNullException.ThrowIfNull(assets);
        _assets = assets;
        Guid = model;
        var record = assets.Get(model) ?? throw new KeyNotFoundException($"Asset {model} does not exist.");
        if (record.Type != AssetTypes.Model)
        {
            throw new ArgumentException($"'{record.Path}' is not a model.", nameof(model));
        }

        Path = record.Path;
    }

    public Guid Guid { get; }

    /// <summary>Project-relative path of the model.</summary>
    public string Path { get; }

    /// <summary>The model's file name without extension, the prefix of extracted files.</summary>
    public string ModelName => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>The folder next to the model where extracted files go by default.</summary>
    public string DefaultFolder(string kind) => Combine(Folder(Path), kind);

    public ModelImportSettings Settings => ModelImportSettings.Read(_assets.GetSettings(Guid));

    /// <summary>The last successful import, or null when the model never imported.</summary>
    /// <exception cref="InvalidDataException">The artifact is unreadable.</exception>
    public ModelData? Load() => _assets.ReadArtifact(Guid, ModelArtifact.Name) is { } artifact ? ModelArtifact.Read(artifact) : null;

    /// <summary>
    /// Mesh names that scenes and prefabs reference (<c>asset:&lt;model&gt;#mesh/&lt;name&gt;</c>) but the model does not have,
    /// neither as a mesh nor as an alias, with the files using each.
    /// </summary>
    public IReadOnlyList<MissingMeshReference> FindMissingMeshReferences()
    {
        if (Load() is not { } model)
        {
            return [];
        }

        var prefix = $"asset:{Guid:D}#{Runtime.ModelCache.MeshSubAssetPrefix}";
        var missing = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var record in _assets.Assets.Where(a => a.Type is AssetTypes.Scene or AssetTypes.Prefab).ToList())
        {
            JToken document;
            try
            {
                document = JToken.Parse(System.Text.Encoding.UTF8.GetString(_assets.ReadAllBytes(record.Guid)));
            }
            catch (Newtonsoft.Json.JsonException)
            {
                continue; // a broken scene shows its own load error; it cannot be relinked
            }

            foreach (var value in document.SelectTokens("$..*").OfType<JValue>().Where(v => v.Type == JTokenType.String).Select(v => (string)v!))
            {
                if (value.StartsWith(prefix, StringComparison.Ordinal) && value[prefix.Length..] is { Length: > 0 } name && model.FindMesh(name) is null)
                {
                    (missing.TryGetValue(name, out var files) ? files : missing[name] = new SortedSet<string>(StringComparer.Ordinal)).Add(record.Path);
                }
            }
        }

        return [.. missing.Select(m => new MissingMeshReference(m.Key, [.. m.Value]))];
    }

    /// <summary>Points references to <paramref name="oldName"/> at <paramref name="mesh"/> (a mesh alias), or removes the alias when null; reimports.</summary>
    public IReadOnlyList<string> Relink(string oldName, string? mesh)
    {
        ArgumentException.ThrowIfNullOrEmpty(oldName);
        var settings = Settings;
        var aliases = new SortedDictionary<string, string>(settings.MeshAliases.ToDictionary(), StringComparer.Ordinal);
        if (mesh is null)
        {
            aliases.Remove(oldName);
        }
        else
        {
            aliases[oldName] = mesh;
        }

        return ApplySettings(settings with { MeshAliases = aliases });
    }

    /// <summary>Writes new import settings and reimports; returns the problems found while normalizing them.</summary>
    public IReadOnlyList<string> ApplySettings(ModelImportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return _assets.UpdateSettings(Guid, settings.ToJson());
    }

    /// <summary>Points a source material at a project material, or back at the imported one (null).</summary>
    public void RemapMaterial(string material, Guid? replacement) => ChangeRemap("materials", material, replacement);

    /// <summary>Points a model texture at a project texture, or back at the imported one (null).</summary>
    public void RemapTexture(string texture, Guid? replacement) => ChangeRemap("textures", texture, replacement);

    /// <summary>Clears every material remap: the model draws its imported materials again (extracted files stay).</summary>
    public void ResetMaterialRemaps() => ApplySettings(Settings with { Materials = new SortedDictionary<string, Guid>(StringComparer.Ordinal) });

    /// <summary>Extracts embedded textures and those outside <c>Assets/</c> into a folder and remaps the model to them.</summary>
    /// <returns>A line per texture: written, reused or skipped (with the reason).</returns>
    /// <exception cref="InvalidOperationException">The model has not imported.</exception>
    public IReadOnlyList<string> ExtractTextures(string folder)
    {
        var model = Load() ?? throw new InvalidOperationException($"'{Path}' has not imported; fix its import errors first.");
        var report = new List<string>();
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var targets = new Dictionary<string, string>(StringComparer.Ordinal); // texture name -> project path
        var byHash = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var texture in model.Textures.Where(t => t.ProjectAsset is null))
        {
            var (bytes, extension, problem) = texture.Embedded is { } embedded ? (embedded, ImageExtension(embedded), null) : ReadOutside(texture);
            if (bytes is null || extension is null)
            {
                report.Add($"{texture.Name}: skipped ({problem ?? "not a PNG, JPEG, TGA or BMP image"}).");
                continue;
            }

            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (byHash.TryGetValue(hash, out var same))
            {
                targets[texture.Name] = same;
                report.Add($"{texture.Name}: same image as {same}.");
                continue;
            }

            var path = UniquePath(Combine(folder, Safe($"{ModelName}_{texture.Name}") + extension), bytes);
            byHash[hash] = path;
            targets[texture.Name] = path;
            if (File.Exists(AssetPaths.Resolve(_assets.AssetsDirectory, path)))
            {
                report.Add($"{texture.Name}: reused {path}.");
            }
            else
            {
                files[path] = bytes;
                report.Add($"{texture.Name}: extracted to {path}.");
            }
        }

        if (targets.Count == 0)
        {
            return report.Count > 0 ? report : ["No embedded or outside textures to extract."];
        }

        var written = files.Count > 0 ? _assets.WriteFiles(files) : new Dictionary<string, Guid>();
        var settings = Settings;
        var remaps = new SortedDictionary<string, Guid>(settings.Textures.ToDictionary(), StringComparer.Ordinal);
        foreach (var (texture, path) in targets)
        {
            remaps[texture] = written.TryGetValue(path, out var guid) ? guid : _assets.TryGetGuid(path, out var existing) ? existing
                : throw new InvalidOperationException($"'{path}' is not an asset.");
        }

        ApplySettings(settings with { Textures = remaps });
        return report;
    }

    /// <summary>
    /// Writes a <c>.mnvtmat</c> per material that has no remap yet (extracting the textures they need first), reusing
    /// existing files, and remaps the model to them.
    /// </summary>
    /// <returns>A line per material: extracted, reused, or already remapped.</returns>
    /// <exception cref="InvalidOperationException">The model has not imported.</exception>
    public IReadOnlyList<string> ExtractMaterials(string folder, string textureFolder)
    {
        var model = Load() ?? throw new InvalidOperationException($"'{Path}' has not imported; fix its import errors first.");
        var report = new List<string>();
        if (model.Textures.Any(t => t.ProjectAsset is null && t.Embedded is not null))
        {
            report.AddRange(ExtractTextures(textureFolder));
            model = Load()!;
        }

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var material in model.Materials)
        {
            if (material.Override is { } existing && _assets.GetAssetType(existing) == AssetTypes.Material)
            {
                report.Add($"{material.Name}: already uses {(_assets.TryGetPath(existing, out var p) ? p : existing.ToString())}.");
                continue;
            }

            var path = Combine(folder, Safe($"{ModelName}_{material.Name}") + MaterialDefinition.Extension);
            targets[material.Name] = path;
            if (File.Exists(AssetPaths.Resolve(_assets.AssetsDirectory, path)))
            {
                report.Add($"{material.Name}: reused {path} (not overwritten).");
            }
            else
            {
                files[path] = System.Text.Encoding.UTF8.GetBytes(ToDefinition(material, model).ToJson());
                report.Add($"{material.Name}: extracted to {path}.");
            }
        }

        if (targets.Count == 0)
        {
            return report;
        }

        var written = files.Count > 0 ? _assets.WriteFiles(files) : new Dictionary<string, Guid>();
        var settings = Settings;
        var remaps = new SortedDictionary<string, Guid>(settings.Materials.ToDictionary(), StringComparer.Ordinal);
        foreach (var (material, path) in targets)
        {
            remaps[material] = written.TryGetValue(path, out var guid) ? guid : _assets.TryGetGuid(path, out var existing) ? existing
                : throw new InvalidOperationException($"'{path}' is not an asset.");
        }

        ApplySettings(settings with { Materials = remaps });
        return report;
    }

    /// <summary>The imported material as a Standard material definition (what Extract Materials writes).</summary>
    public static MaterialDefinition ToDefinition(ModelMaterial material, ModelData model)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(model);
        var definition = new MaterialDefinition
        {
            Builtin = MaterialDefinition.BuiltinStandard,
            Blend = material.AlphaMode == ModelAlphaMode.Blend ? "Alpha" : "Opaque",
            Cull = material.DoubleSided ? "None" : null,
        };
        var c = material.BaseColor;
        var e = material.Emissive;
        definition.Properties["_BaseColor"] = MaterialValue.Vector(c.R, c.G, c.B, c.A);
        definition.Properties["_Metallic"] = MaterialValue.Number(material.Metallic);
        definition.Properties["_Roughness"] = MaterialValue.Number(material.Roughness);
        definition.Properties["_NormalScale"] = MaterialValue.Number(material.NormalScale);
        definition.Properties["_OcclusionStrength"] = MaterialValue.Number(material.OcclusionStrength);
        definition.Properties["_EmissiveColor"] = MaterialValue.Vector(e.R, e.G, e.B, 1f);
        if (material.AlphaMode == ModelAlphaMode.Mask)
        {
            definition.Properties["_AlphaCutoff"] = MaterialValue.Number(material.AlphaCutoff);
        }

        foreach (var (role, property) in ModelMaterialSlots.Textures)
        {
            if (material.Textures.TryGetValue(role, out var index) && model.Textures[index].ProjectAsset is { } texture)
            {
                definition.Properties[property] = MaterialValue.TextureAsset(texture);
            }

            if (material.UVSets.TryGetValue(role, out var set) && set != 0)
            {
                definition.Properties[ModelMaterialSlots.UVSetProperty(property)] = MaterialValue.Number(set);
            }
        }

        return definition;
    }

    private void ChangeRemap(string table, string name, Guid? replacement)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var settings = Settings;
        var remaps = new SortedDictionary<string, Guid>((table == "materials" ? settings.Materials : settings.Textures).ToDictionary(), StringComparer.Ordinal);
        if (replacement is { } guid)
        {
            remaps[name] = guid;
        }
        else
        {
            remaps.Remove(name);
        }

        ApplySettings(table == "materials" ? settings with { Materials = remaps } : settings with { Textures = remaps });
    }

    /// <summary>An external texture that exists outside <c>Assets/</c>, read for copying in.</summary>
    private (byte[]? Bytes, string? Extension, string? Problem) ReadOutside(ModelTexture texture)
    {
        if (texture.SourcePath is not { } source)
        {
            return (null, null, "no source path");
        }

        var modelFolder = System.IO.Path.GetDirectoryName(_assets.FullPath(Guid))!;
        var normalized = source.Replace('\\', System.IO.Path.DirectorySeparatorChar).Replace('/', System.IO.Path.DirectorySeparatorChar);
        var file = System.IO.Path.IsPathRooted(normalized) ? normalized : System.IO.Path.GetFullPath(System.IO.Path.Combine(modelFolder, normalized));
        if (!File.Exists(file))
        {
            return (null, null, $"'{source}' was not found");
        }

        var bytes = File.ReadAllBytes(file);
        return (bytes, ImageExtension(bytes), null);
    }

    /// <summary>The extension of an encoded image the engine reads, from its signature; null for anything else.</summary>
    public static string? ImageExtension(ReadOnlySpan<byte> bytes) => bytes switch
    {
        [0x89, 0x50, 0x4E, 0x47, ..] => ".png",
        [0xFF, 0xD8, 0xFF, ..] => ".jpg",
        [0x42, 0x4D, ..] => ".bmp",
        [_, _, 2 or 10, ..] when bytes.Length > 18 => ".tga", // uncompressed or RLE true-color TGA (embedded raw texels are written as TGA)
        _ => null,
    };

    /// <summary>The path itself when free or holding identical bytes; otherwise the name with a content-hash suffix.</summary>
    private string UniquePath(string path, byte[] bytes)
    {
        var full = AssetPaths.Resolve(_assets.AssetsDirectory, path);
        if (!File.Exists(full) || File.ReadAllBytes(full).AsSpan().SequenceEqual(bytes))
        {
            return path;
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..8].ToLowerInvariant();
        return Combine(Folder(path), $"{System.IO.Path.GetFileNameWithoutExtension(path)}_{hash}{System.IO.Path.GetExtension(path)}");
    }

    private static string Safe(string name) =>
        string.Concat(name.Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) || ch is '#' ? '_' : ch));

    private static string Folder(string path) => System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";

    private static string Combine(string folder, string name) => folder.Length == 0 ? name : string.Create(CultureInfo.InvariantCulture, $"{folder.TrimEnd('/')}/{name}");
}

/// <summary>A mesh name that scenes or prefabs reference but the model no longer has.</summary>
public sealed record MissingMeshReference(string Mesh, IReadOnlyList<string> UsedBy);
