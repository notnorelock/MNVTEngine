using System.Globalization;
using MNVTEngine.Serialization;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Assets.Models;

/// <summary>Import settings of a model (stored in its <c>.meta</c>). Only settings both readers honor are offered.</summary>
public sealed record ModelImportSettings
{
    /// <summary>Uniform scale applied to the whole model after unit conversion (FBX files are converted to meters).</summary>
    public float Scale { get; init; } = 1f;

    /// <summary>Compute smooth normals for meshes that have none.</summary>
    public bool GenerateMissingNormals { get; init; } = true;

    /// <summary>Compute tangents (for normal maps) for meshes with UVs and no tangents.</summary>
    public bool GenerateTangents { get; init; } = true;

    /// <summary>Merge identical vertices and reorder for the GPU cache (FBX).</summary>
    public bool OptimizeMeshes { get; init; } = true;

    public bool ImportMaterials { get; init; } = true;

    public bool ImportAnimations { get; init; } = true;

    public bool ImportSkins { get; init; } = true;

    public static ModelImportSettings Read(JObject settings, ICollection<string>? problems = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var defaults = new ModelImportSettings();
        bool Bool(string key, bool fallback)
        {
            if (settings[key] is null)
            {
                return fallback;
            }

            if (settings[key]!.Type == JTokenType.Boolean)
            {
                return settings.Value<bool>(key);
            }

            problems?.Add($"'{key}' must be true or false; reset to {fallback}.");
            return fallback;
        }

        var scale = defaults.Scale;
        if (settings["scale"] is { } s)
        {
            if (s.Type is JTokenType.Float or JTokenType.Integer && s.Value<float>() is > 0f and <= 10000f and var value)
            {
                scale = value;
            }
            else
            {
                problems?.Add("'scale' must be a number above 0 (at most 10000); reset to 1.");
            }
        }

        return new ModelImportSettings
        {
            Scale = scale,
            GenerateMissingNormals = Bool("generateMissingNormals", defaults.GenerateMissingNormals),
            GenerateTangents = Bool("generateTangents", defaults.GenerateTangents),
            OptimizeMeshes = Bool("optimizeMeshes", defaults.OptimizeMeshes),
            ImportMaterials = Bool("importMaterials", defaults.ImportMaterials),
            ImportAnimations = Bool("importAnimations", defaults.ImportAnimations),
            ImportSkins = Bool("importSkins", defaults.ImportSkins),
        };
    }

    public JObject ToJson() => new()
    {
        ["scale"] = Scale,
        ["generateMissingNormals"] = GenerateMissingNormals,
        ["generateTangents"] = GenerateTangents,
        ["optimizeMeshes"] = OptimizeMeshes,
        ["importMaterials"] = ImportMaterials,
        ["importAnimations"] = ImportAnimations,
        ["importSkins"] = ImportSkins,
    };
}

/// <summary>What a reader gets: the source and how to find files and assets next to it.</summary>
public sealed record ModelSource(string RelativePath, byte[] Data, string? FullPath, string? AssetsDirectory, Func<string, Guid?> ResolveAsset)
{
    public string Extension => Path.GetExtension(RelativePath).ToLowerInvariant();
}

/// <summary>Reads one source format into <see cref="ModelData"/>; problems go to the import context.</summary>
public interface IModelReader
{
    IReadOnlyList<string> Extensions { get; }

    /// <exception cref="InvalidDataException">The file cannot be read as a model.</exception>
    ModelData Read(ModelSource source, ModelImportSettings settings, ICollection<string> warnings, ISet<Guid> dependencies);
}

/// <summary>
/// The asset importer for 3D models: FBX through Assimp, glTF and GLB through SharpGLTF. It reads the file, converts
/// it to <see cref="ModelData"/> (engine conventions, unique names, generated normals and tangents), records the textures
/// and buffers it uses as dependencies, reports every problem, and stores the result as the <c>model</c> artifact.
/// </summary>
public sealed class ModelImporter(IReadOnlyList<IModelReader> readers) : IAssetImporter
{
    public string Name => "Model";

    /// <summary>Bump when the artifact or conversion changes, so models reimport.</summary>
    public int Version => 1;

    public string AssetType => AssetTypes.Model;

    public IReadOnlyList<string> Extensions { get; } = [.. readers.SelectMany(r => r.Extensions)];

    public bool ReimportWhenDependenciesChange => true;

    public JObject NormalizeSettings(JObject settings, ICollection<string> problems) => ModelImportSettings.Read(settings, problems).ToJson();

    public void Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = ModelImportSettings.Read(context.Settings);
        var source = new ModelSource(context.RelativePath, context.Data, context.SourceFullPath, context.AssetsDirectory, context.ResolveAsset);
        var reader = readers.First(r => r.Extensions.Contains(source.Extension));
        ModelData model;
        try
        {
            model = reader.Read(source, settings, context.Warnings, context.Dependencies);
        }
        catch (InvalidDataException ex)
        {
            context.Errors.Add(ex.Message);
            return;
        }

        ModelProcessing.Finish(model, settings, context.Warnings);
        if (model.Meshes.Count == 0)
        {
            context.Warnings.Add("The model has no meshes.");
        }

        foreach (var warning in context.Warnings)
        {
            model.Warnings.Add(warning);
        }

        context.Artifacts[ModelArtifact.Name] = ModelArtifact.Write(model);
        var culture = CultureInfo.InvariantCulture;
        context.Properties["nodes"] = model.Nodes.Count.ToString(culture);
        context.Properties["meshes"] = model.Meshes.Count.ToString(culture);
        context.Properties["vertices"] = model.VertexCount.ToString(culture);
        context.Properties["triangles"] = model.TriangleCount.ToString(culture);
        context.Properties["materials"] = model.Materials.Count.ToString(culture);
        context.Properties["textures"] = model.Textures.Count.ToString(culture);
        context.Properties["skins"] = model.Skins.Count.ToString(culture);
        context.Properties["animations"] = model.Animations.Count.ToString(culture);
    }
}

/// <summary>The importers of this project, to add to an asset database opened for editing.</summary>
public static class ModelImporters
{
    public static ModelImporter Create() => new([new AssimpModelReader(), new GltfModelReader()]);

    /// <summary>The default importers plus the model importer: what the editor, Player and build use.</summary>
    public static IReadOnlyList<IAssetImporter> WithDefaults() => [.. AssetImporters.CreateDefault().Where(i => i is not ModelPlaceholderImporter), Create()];
}
