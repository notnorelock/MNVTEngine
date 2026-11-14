using System.Globalization;
using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Serialization;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Assets.Models;

/// <summary>Import settings of a model (stored in its <c>.meta</c>). Only settings both readers honor are offered.</summary>
public sealed record ModelImportSettings
{
    private const string AssetPrefix = "asset:";

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

    /// <summary>Lights in the file become <c>Light</c> components when the model is added to a scene.</summary>
    public bool ImportLights { get; init; } = true;

    /// <summary>Cameras in the file become (disabled) <c>Camera</c> components when the model is added to a scene.</summary>
    public bool ImportCameras { get; init; } = true;

    /// <summary>Old mesh names to current ones, so references to a mesh renamed in the source keep working (Relink).</summary>
    public IReadOnlyDictionary<string, string> MeshAliases { get; init; } = new SortedDictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Source material name to the project material used instead (Extract Materials, Remap).</summary>
    public IReadOnlyDictionary<string, Guid> Materials { get; init; } = new SortedDictionary<string, Guid>(StringComparer.Ordinal);

    /// <summary>Model texture name to the project texture used instead (Extract Textures, Remap).</summary>
    public IReadOnlyDictionary<string, Guid> Textures { get; init; } = new SortedDictionary<string, Guid>(StringComparer.Ordinal);

    /// <summary>
    /// Levels of detail generated for static meshes (not skinned, no blend shapes), coarsest last; empty = none. Their
    /// full-detail geometry is then streamed: loaded only while the mesh is drawn at its highest detail.
    /// </summary>
    public IReadOnlyList<ModelLodSetting> Lods { get; init; } = [];

    /// <summary>A reasonable starting point for the <c>lods</c> setting: half and a fifth of the triangles.</summary>
    public static IReadOnlyList<ModelLodSetting> DefaultLods { get; } = [new(0.5f, 0.5f), new(0.2f, 0.2f)];

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

        SortedDictionary<string, Guid> Remaps(string key)
        {
            var result = new SortedDictionary<string, Guid>(StringComparer.Ordinal);
            if (settings[key] is null)
            {
                return result;
            }

            if (settings[key] is not JObject table)
            {
                problems?.Add($"'{key}' must be an object of name: \"asset:<guid>\"; reset.");
                return result;
            }

            foreach (var property in table.Properties())
            {
                if (property.Value.Type == JTokenType.String && ((string)property.Value!) is var text && text.StartsWith(AssetPrefix, StringComparison.Ordinal)
                    && Guid.TryParse(text.AsSpan(AssetPrefix.Length), out var guid) && guid != Guid.Empty)
                {
                    result[property.Name] = guid;
                }
                else
                {
                    problems?.Add($"'{key}.{property.Name}' is not an asset reference; removed.");
                }
            }

            return result;
        }

        var aliases = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (settings["meshAliases"] is JObject aliasTable)
        {
            foreach (var property in aliasTable.Properties())
            {
                if (property.Value.Type == JTokenType.String && ((string)property.Value!) is { Length: > 0 } current && current != property.Name)
                {
                    aliases[property.Name] = current;
                }
                else
                {
                    problems?.Add($"'meshAliases.{property.Name}' must name another mesh; removed.");
                }
            }
        }
        else if (settings["meshAliases"] is not null)
        {
            problems?.Add("'meshAliases' must be an object of old name: \"current name\"; reset.");
        }

        var lods = new List<ModelLodSetting>();
        if (settings["lods"] is JArray lodList)
        {
            foreach (var item in lodList)
            {
                if (item is JObject lod && lod["ratio"] is { Type: JTokenType.Float or JTokenType.Integer } ratio && lod["screenSize"] is { Type: JTokenType.Float or JTokenType.Integer } size
                    && ratio.Value<float>() is > 0f and < 1f and var r && size.Value<float>() is > 0f and < 1f and var screen
                    && (lods.Count == 0 || (r < lods[^1].Ratio && screen < lods[^1].ScreenSize)))
                {
                    lods.Add(new ModelLodSetting(r, screen));
                }
                else
                {
                    problems?.Add("'lods' entries need a \"ratio\" and a \"screenSize\" between 0 and 1, each smaller than the previous entry's; the rest were removed.");
                    break;
                }
            }
        }
        else if (settings["lods"] is not null)
        {
            problems?.Add("'lods' must be a list of { \"ratio\", \"screenSize\" }; reset.");
        }

        return new ModelImportSettings
        {
            Lods = lods,
            Materials = Remaps("materials"),
            MeshAliases = aliases,
            Textures = Remaps("textures"),
            Scale = scale,
            GenerateMissingNormals = Bool("generateMissingNormals", defaults.GenerateMissingNormals),
            GenerateTangents = Bool("generateTangents", defaults.GenerateTangents),
            OptimizeMeshes = Bool("optimizeMeshes", defaults.OptimizeMeshes),
            ImportMaterials = Bool("importMaterials", defaults.ImportMaterials),
            ImportAnimations = Bool("importAnimations", defaults.ImportAnimations),
            ImportSkins = Bool("importSkins", defaults.ImportSkins),
            ImportLights = Bool("importLights", defaults.ImportLights),
            ImportCameras = Bool("importCameras", defaults.ImportCameras),
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
        ["importLights"] = ImportLights,
        ["importCameras"] = ImportCameras,
        ["meshAliases"] = new JObject(MeshAliases.Select(p => new JProperty(p.Key, p.Value))),
        ["materials"] = new JObject(Materials.Select(p => new JProperty(p.Key, AssetPrefix + p.Value.ToString("D")))),
        ["textures"] = new JObject(Textures.Select(p => new JProperty(p.Key, AssetPrefix + p.Value.ToString("D")))),
        ["lods"] = new JArray(Lods.Select(l => new JObject { ["ratio"] = l.Ratio, ["screenSize"] = l.ScreenSize })),
    };
}

/// <param name="Ratio">Fraction of the mesh's triangles to keep (0 to 1).</param>
/// <param name="ScreenSize">The level is drawn once the mesh covers less than this fraction of the screen's height.</param>
public sealed record ModelLodSetting(float Ratio, float ScreenSize);

/// <summary>What a reader gets: the source and how to find files and assets next to it.</summary>
public sealed record ModelSource(string RelativePath, byte[] Data, string? FullPath, string? AssetsDirectory, Func<string, Guid?> ResolveAsset)
{
    /// <summary>Assets whose content the reader used (external buffers); changing one reimports the model.</summary>
    public ISet<Guid> Inputs { get; init; } = new HashSet<Guid>();

    public string Extension => Path.GetExtension(RelativePath).ToLowerInvariant();
}

/// <summary>Reads one source format into <see cref="ModelData"/>; problems go to the import context.</summary>
public interface IModelReader
{
    IReadOnlyList<string> Extensions { get; }

    /// <param name="dependencies">Assets the model references (textures) and, through <see cref="ModelSource.Inputs"/>, files it reads.</param>
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

    /// <summary>
    /// Changes with the artifact format (so models imported with an older format reimport by themselves) and with
    /// <see cref="ConversionRevision"/> (bump it when the conversion changes).
    /// </summary>
    public int Version => (ModelArtifact.Version * 100) + ConversionRevision;

    /// <summary>1: FBX meshes are named after their node. 2: separate metallic and roughness maps are combined.</summary>
    private const int ConversionRevision = 2;

    public string AssetType => AssetTypes.Model;

    public IReadOnlyList<string> Extensions { get; } = [.. readers.SelectMany(r => r.Extensions)];

    public bool ReimportWhenDependenciesChange => true;

    /// <summary>Models take seconds to read and convert: the editor imports them without blocking.</summary>
    public bool ImportsInBackground => true;

    public JObject NormalizeSettings(JObject settings, ICollection<string> problems) => ModelImportSettings.Read(settings, problems).ToJson();

    public void Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = ModelImportSettings.Read(context.Settings);
        var source = new ModelSource(context.RelativePath, context.Data, context.SourceFullPath, context.AssetsDirectory, context.ResolveAsset) { Inputs = context.ImportInputs };
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

        ModelProcessing.Finish(model, settings, context.Warnings, context.Dependencies, context.ImportInputs, context.AssetFile);
        if (model.Meshes.Count == 0)
        {
            context.Warnings.Add("The model has no meshes.");
        }

        foreach (var warning in context.Warnings)
        {
            model.Warnings.Add(warning);
        }

        var lodMeshes = GenerateLods(model, settings.Lods);
        context.Artifacts[ModelArtifact.Name] = ModelArtifact.Write(model, context.Artifacts);
        var culture = CultureInfo.InvariantCulture;
        context.Properties["nodes"] = model.Nodes.Count.ToString(culture);
        context.Properties["meshes"] = model.Meshes.Count.ToString(culture);
        context.Properties["vertices"] = model.VertexCount.ToString(culture);
        context.Properties["triangles"] = model.TriangleCount.ToString(culture);
        context.Properties["materials"] = model.Materials.Count.ToString(culture);
        context.Properties["textures"] = model.Textures.Count.ToString(culture);
        context.Properties["skins"] = model.Skins.Count.ToString(culture);
        context.Properties["animations"] = model.Animations.Count.ToString(culture);
        context.Properties["lights"] = model.Lights.Count.ToString(culture);
        context.Properties["cameras"] = model.Cameras.Count.ToString(culture);
        context.Properties["lodMeshes"] = lodMeshes.ToString(culture);
    }

    /// <summary>Meshes below this many triangles get no levels of detail (they would save next to nothing).</summary>
    public const int MinLodTriangles = 64;

    /// <summary>Adds the configured levels of detail to every static mesh large enough; returns how many meshes got some.</summary>
    public static int GenerateLods(ModelData model, IReadOnlyList<ModelLodSetting> lods)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(lods);
        var count = 0;
        foreach (var mesh in model.Meshes.Where(m => lods.Count > 0 && m.Joints is null && m.BlendShapes.Count == 0 && m.TriangleCount >= MinLodTriangles))
        {
            var previous = mesh.TriangleCount;
            foreach (var lod in lods)
            {
                var reduced = MeshSimplifier.Simplify(mesh.Mesh, lod.Ratio).Mesh;
                if (reduced.TriangleCount > previous * 0.9f)
                {
                    break; // the shape does not simplify further (seams, borders): more levels would only cost memory
                }

                mesh.Lods.Add(new ModelMeshLod(reduced, lod.ScreenSize));
                previous = reduced.TriangleCount;
            }

            count += mesh.Lods.Count > 0 ? 1 : 0;
        }

        return count;
    }
}

/// <summary>The importers of this project, to add to an asset database opened for editing.</summary>
public static class ModelImporters
{
    public static ModelImporter Create() => new([new AssimpModelReader(), new GltfModelReader()]);

    /// <summary>The default importers plus the model importer: what the editor, Player and build use.</summary>
    public static IReadOnlyList<IAssetImporter> WithDefaults() => [.. AssetImporters.CreateDefault().Where(i => i is not ModelPlaceholderImporter), Create()];
}
