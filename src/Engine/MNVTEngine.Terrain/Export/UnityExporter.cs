using Microsoft.Extensions.Logging;
using MNVTEngine.Terrain.Elements;
using MNVTEngine.Terrain.Geometry;
using MNVTEngine.Terrain.Meshes;
using MNVTEngine.Terrain.Project;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace MNVTEngine.Terrain.Export;

/// <param name="LodLevels">LOD levels to bake, starting at LOD0 (1 to 4).</param>
/// <param name="Scale">
/// Baked into every element vertex. Foliage and prop placements and prop geometry stay unscaled: the importer applies
/// the scale once on their shared parent, so placement and size scale together.
/// </param>
public sealed record UnityExportOptions(int LodLevels = 1, double Scale = 1);

public readonly record struct ExportProgress(string Message, int Current, int Total);

/// <summary>Where the exporter reads texture bytes and prop meshes from (the project folder, the asset database...).</summary>
public interface ITerrainExportSource
{
    /// <summary>The bytes of a project texture, or null when it is missing.</summary>
    byte[]? ReadTexture(string projectPath);

    /// <summary>A prop's LOD0 groups (uncalibrated), or null when its file is missing.</summary>
    IReadOnlyList<MeshGroup>? LoadMesh(MeshAssetData asset);
}

/// <summary>
/// Exports a terrain project the way TerrainHeaven's Unity exporter lays it out: one OBJ + MTL pair per element and LOD
/// under Meshes/, textures under Textures/, terrain splat control maps under ControlMaps/, foliage.json, meshes.json
/// (each placed prop asset once, plus its placements) and manifest.json tying them together.
/// </summary>
public sealed partial class UnityExporter(ILogger? logger = null)
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy { ProcessDictionaryKeys = false } },
        NullValueHandling = NullValueHandling.Include,
        Formatting = Formatting.Indented,
    };

    private readonly MeshLodGenerator _lods = new();

    /// <summary>Writes the export into <paramref name="outputDirectory"/> (created if missing) and returns the manifest.</summary>
    public ExportManifest Export(TerrainProject project, string outputDirectory, UnityExportOptions options, ITerrainExportSource source, Action<ExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(source);

        var lodLevels = Math.Clamp(options.LodLevels, 1, LodLevels.MaxLodIndex + 1);
        var scale = options.Scale > 0 ? options.Scale : 1;
        var texturePaths = new List<string>();
        var exportable = project.World.Elements.Select(e => (Element: e, Kind: GetExportKind(e))).Where(e => e.Kind is not null).ToList();
        var usedAssets = project.Meshes.Assets.Select((asset, index) => (Asset: asset, Index: index)).Where(e => project.Meshes.GetInstances(e.Index).Count > 0).ToList();
        var total = (exportable.Count * lodLevels) + (usedAssets.Count * (lodLevels + 1)) + 4;
        var step = 0;
        void Report(string message) => progress?.Invoke(new ExportProgress(message, step, total));
        void Step(string message)
        {
            Report(message);
            step++;
        }

        var meshesDir = Directory.CreateDirectory(Path.Combine(outputDirectory, "Meshes")).FullName;
        var texturesDir = Directory.CreateDirectory(Path.Combine(outputDirectory, "Textures")).FullName;
        var controlMapsDir = Directory.CreateDirectory(Path.Combine(outputDirectory, "ControlMaps")).FullName;

        var objects = WriteObjects(meshesDir, controlMapsDir, exportable!, lodLevels, scale, texturePaths, Step);
        var (assets, placements) = WriteMeshAssets(project.Meshes, meshesDir, texturesDir, usedAssets, lodLevels, source, Step);

        Report("Writing foliage data...");
        var foliage = project.Foliage.Serialize();
        texturePaths.AddRange(foliage.Types.Select(t => t.TexturePath).Where(p => !string.IsNullOrEmpty(p)));
        File.WriteAllText(Path.Combine(outputDirectory, "foliage.json"), JsonConvert.SerializeObject(foliage, JsonSettings));
        step++;

        Report("Writing mesh prop placements...");
        File.WriteAllText(Path.Combine(outputDirectory, "meshes.json"), JsonConvert.SerializeObject(new ExportMeshes(1, assets, placements), JsonSettings));
        step++;

        var textures = WriteTextures(texturesDir, texturePaths.Distinct(StringComparer.Ordinal).ToList(), source, (current, count) => Report($"Writing textures ({current}/{count})..."));
        step++;

        Report("Writing manifest...");
        var manifest = new ExportManifest(1, scale, lodLevels, objects, "foliage.json", "meshes.json", [.. project.Settings.LodDistances], textures);
        File.WriteAllText(Path.Combine(outputDirectory, "manifest.json"), JsonConvert.SerializeObject(manifest, JsonSettings));
        step++;
        Report("Done");
        return manifest;
    }

    private static List<ExportObject> WriteObjects(string meshesDir, string controlMapsDir, List<(WorldElement Element, string Kind)> exportable, int lodLevels, double scale, List<string> texturePaths, Action<string> step)
    {
        var objects = new List<ExportObject>();
        var countByKind = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (element, kind) in exportable)
        {
            var index = countByKind.GetValueOrDefault(kind);
            countByKind[kind] = index + 1;
            var name = $"{kind}_{index}";
            var lods = new List<ExportLod>();
            for (var lod = 0; lod < lodLevels; lod++)
            {
                step($"Building {name} (LOD {lod})...");
                var objGroups = new List<ObjGroup>();
                var materials = new List<ObjMaterial>();
                var groups = new List<ExportLodGroup>();
                foreach (var group in GetExportGeometry(element, lod).Where(g => g.Triangles.Count > 0))
                {
                    var texture = element.GetGroupTextureReferences().GetValueOrDefault(group.Name);
                    if (!string.IsNullOrEmpty(texture))
                    {
                        texturePaths.Add(texture);
                    }

                    var materialName = $"{name}_{group.Name}";
                    var file = string.IsNullOrEmpty(texture) ? null : TextureFileName(texture);
                    objGroups.Add(Flatten(group, materialName, scale));
                    materials.Add(new ObjMaterial(materialName, file is null ? null : $"../Textures/{file}"));
                    groups.Add(new ExportLodGroup(group.Name, file));
                }

                lods.Add(WriteObjMtl(meshesDir, $"{name}_lod{lod}", objGroups, materials, groups));
            }

            ExportTerrainPaint? paint = null;
            if (element is Elements.Terrain terrain)
            {
                var controlMap = $"{name}_control.png";
                WriteControlMap(Path.Combine(controlMapsDir, controlMap), terrain.TexturePaint);
                var layers = Enumerable.Range(0, terrain.TextureLayers.Count).Select(i => (Path: terrain.EffectiveLayerTexture(i), terrain.TextureLayers[i].Tiling)).ToList();
                texturePaths.AddRange(layers.Select(l => l.Path).Where(p => p.Length > 0));
                paint = new ExportTerrainPaint($"ControlMaps/{controlMap}", terrain.TexturePaint.Resolution, [.. layers.Select(l => new ExportPaintLayer(l.Path.Length > 0 ? TextureFileName(l.Path) : null, l.Tiling))]);
            }

            objects.Add(new ExportObject(kind, name, lods, paint));
        }

        return objects;
    }

    private (List<ExportMeshAsset> Assets, List<ExportPlacement> Placements) WriteMeshAssets(
        MeshStore store, string meshesDir, string texturesDir, List<(MeshAssetData Asset, int Index)> used, int lodLevels, ITerrainExportSource source, Action<string> step)
    {
        var assets = new List<ExportMeshAsset>();
        var placements = new List<ExportPlacement>();
        var writtenTextures = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (asset, index) in used)
        {
            step($"Loading {asset.DisplayName}...");
            var lod0 = source.LoadMesh(asset);
            if (lod0 is null)
            {
                if (logger is not null)
                {
                    LogMissingMesh(logger, asset.DisplayName, asset.LibraryPath);
                }

                continue;
            }

            var fileBase = $"prop_{SafeId().Replace(asset.Id, "")}";
            var lods = new List<ExportLod>();
            for (var lod = 0; lod < lodLevels; lod++)
            {
                step($"Building {asset.DisplayName} (LOD {lod})...");
                var lodGroups = MeshCalibration.Calibrate(_lods.GetLodGroups(asset.LibraryPath, lod, lod0), asset.BaseScale, asset.PositionOffset.ToVec3());
                var objGroups = new List<ObjGroup>();
                var materials = new List<ObjMaterial>();
                var groups = new List<ExportLodGroup>();
                for (var groupIndex = 0; groupIndex < lodGroups.Count; groupIndex++)
                {
                    var group = lodGroups[groupIndex];
                    if (group.Positions.Length == 0)
                    {
                        continue;
                    }

                    var materialName = $"{fileBase}_g{groupIndex}";
                    string? textureFile = null;
                    if (!string.IsNullOrEmpty(group.TexturePath) && !writtenTextures.TryGetValue(group.TexturePath, out textureFile))
                    {
                        textureFile = WriteMeshTexture(texturesDir, group.TexturePath, materialName, source);
                        writtenTextures[group.TexturePath] = textureFile;
                    }

                    objGroups.Add(new ObjGroup(group.Name, materialName, group.Positions, group.Uvs));
                    materials.Add(new ObjMaterial(materialName, textureFile is null ? null : $"../Textures/{textureFile}"));
                    groups.Add(new ExportLodGroup(group.Name, textureFile));
                }

                lods.Add(WriteObjMtl(meshesDir, $"{fileBase}_lod{lod}", objGroups, materials, groups));
            }

            assets.Add(new ExportMeshAsset(asset.Id, asset.DisplayName, lods));
            placements.AddRange(store.GetInstances(index).Select(i => new ExportPlacement(asset.Id, i.Position, new PointData { X = i.RotationX, Y = i.RotationY, Z = i.RotationZ }, i.Scale)));
        }

        return (assets, placements);
    }

    private static ExportLod WriteObjMtl(string meshesDir, string fileBase, List<ObjGroup> objGroups, List<ObjMaterial> materials, List<ExportLodGroup> groups)
    {
        File.WriteAllText(Path.Combine(meshesDir, $"{fileBase}.obj"), ObjWriter.BuildObj($"{fileBase}.mtl", fileBase, objGroups));
        File.WriteAllText(Path.Combine(meshesDir, $"{fileBase}.mtl"), ObjWriter.BuildMtl(materials));
        return new ExportLod($"Meshes/{fileBase}.obj", $"Meshes/{fileBase}.mtl", groups);
    }

    private string? WriteMeshTexture(string texturesDir, string texturePath, string fileNameHint, ITerrainExportSource source)
    {
        var bytes = source.ReadTexture(texturePath);
        if (bytes is null)
        {
            if (logger is not null)
            {
                LogMissingTexture(logger, texturePath);
            }

            return null;
        }

        var extension = Path.GetExtension(TextureFileName(texturePath)).TrimStart('.').ToLowerInvariant();
        var fileName = $"{fileNameHint}.{(extension.Length > 0 ? extension : "png")}";
        File.WriteAllBytes(Path.Combine(texturesDir, fileName), bytes);
        return fileName;
    }

    private List<string> WriteTextures(string texturesDir, List<string> paths, ITerrainExportSource source, Action<int, int> step)
    {
        var names = new List<string>();
        for (var index = 0; index < paths.Count; index++)
        {
            step(index + 1, paths.Count);
            var bytes = source.ReadTexture(paths[index]);
            if (bytes is null)
            {
                if (logger is not null)
                {
                    LogMissingTexture(logger, paths[index]);
                }

                continue;
            }

            var fileName = TextureFileName(paths[index]);
            File.WriteAllBytes(Path.Combine(texturesDir, fileName), bytes);
            names.Add(fileName);
        }

        return names;
    }

    /// <summary>The splat control map as PNG, flipped so row 0 is the bottom (Unity's control-map UV origin).</summary>
    private static void WriteControlMap(string path, Meshing.TerrainTexturePaint paint)
    {
        var resolution = paint.Resolution;
        var flipped = new byte[paint.Data.Length];
        var row = resolution * 4;
        for (var y = 0; y < resolution; y++)
        {
            Array.Copy(paint.Data, y * row, flipped, (resolution - 1 - y) * row, row);
        }

        using var stream = File.Create(path);
        new StbImageWriteSharp.ImageWriter().WritePng(flipped, resolution, resolution, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, stream);
    }

    /// <summary>Cut splines and cut points only shape the terrain: they have no geometry of their own to export.</summary>
    private static string? GetExportKind(WorldElement element) => element switch
    {
        Elements.Terrain => "terrain",
        RiverSpline => "river",
        Intersection => "intersection",
        Road => "road",
        Fence => "fence",
        Stairs => "stairs",
        PolygonTerrain => "terrainPolygon",
        _ => null,
    };

    private static List<GeometryGroup> GetExportGeometry(WorldElement element, int lod) => element switch
    {
        Elements.Terrain e => e.GetExportGeometry(lod),
        RiverSpline e => e.GetExportGeometry(lod),
        Intersection e => e.GetExportGeometry(lod),
        Road e => e.GetExportGeometry(lod),
        Fence e => e.GetExportGeometry(lod),
        Stairs e => e.GetExportGeometry(lod),
        PolygonTerrain e => e.GetExportGeometry(lod),
        _ => [],
    };

    private static ObjGroup Flatten(GeometryGroup group, string materialName, double scale)
    {
        var positions = new List<double>(group.Triangles.Count * 9);
        var uvs = new List<double>(group.Triangles.Count * 6);
        foreach (var t in group.Triangles)
        {
            foreach (var p in (Vec3[])[t.A, t.B, t.C])
            {
                positions.AddRange([p.X * scale, p.Y * scale, p.Z * scale]);
            }

            uvs.AddRange([t.UvA.X, t.UvA.Y, t.UvB.X, t.UvB.Y, t.UvC.X, t.UvC.Y]);
        }

        return new ObjGroup(group.Name, materialName, positions, uvs);
    }

    /// <summary>The file name a project texture is exported under; never a path or a dot-name, since it is joined to the output folder.</summary>
    public static string TextureFileName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var name = path.Replace('\\', '/').Split('/')[^1];
        name = string.Concat(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        return name.Length == 0 || name.All(c => c == '.') ? "texture.png" : name;
    }

    [System.Text.RegularExpressions.GeneratedRegex("[^a-zA-Z0-9_-]")]
    private static partial System.Text.RegularExpressions.Regex SafeId();

    [LoggerMessage(EventId = 7120, Level = LogLevel.Warning, Message = "Unity export: mesh prop '{Name}' ({Path}) could not be loaded; skipped")]
    private static partial void LogMissingMesh(ILogger logger, string name, string path);

    [LoggerMessage(EventId = 7121, Level = LogLevel.Warning, Message = "Unity export: texture '{Path}' was not found; skipped")]
    private static partial void LogMissingTexture(ILogger logger, string path);
}

public sealed record ExportLodGroup(string Name, string? Texture);

public sealed record ExportLod(string ObjFile, string MtlFile, IReadOnlyList<ExportLodGroup> Groups);

public sealed record ExportPaintLayer(string? Texture, double Tiling);

public sealed record ExportTerrainPaint(string ControlMap, int Resolution, IReadOnlyList<ExportPaintLayer> Layers);

public sealed record ExportObject(
    string Type,
    string Name,
    IReadOnlyList<ExportLod> Lods,
    [property: JsonProperty(NullValueHandling = NullValueHandling.Ignore)] ExportTerrainPaint? TerrainPaint);

public sealed record ExportMeshAsset(string Id, string Name, IReadOnlyList<ExportLod> Lods);

public sealed record ExportPlacement(string AssetId, PointData Position, PointData Rotation, double Scale);

public sealed record ExportMeshes(int Version, IReadOnlyList<ExportMeshAsset> Assets, IReadOnlyList<ExportPlacement> Placements);

/// <summary>manifest.json: what the Unity importer reads first.</summary>
public sealed record ExportManifest(
    int Version,
    double Scale,
    int LodLevels,
    IReadOnlyList<ExportObject> Objects,
    string FoliageFile,
    string MeshesFile,
    IReadOnlyList<double> LodDistances,
    IReadOnlyList<string> Textures);
