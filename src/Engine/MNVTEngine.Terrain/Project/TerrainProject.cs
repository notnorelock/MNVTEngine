using Microsoft.Extensions.Logging;
using MNVTEngine.Terrain.Elements;
using MNVTEngine.Terrain.Foliage;
using MNVTEngine.Terrain.Meshes;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace MNVTEngine.Terrain.Project;

public sealed class ConnectionData
{
    public int ElementA { get; set; }

    public int NodeA { get; set; }

    public int ElementB { get; set; }

    public int NodeB { get; set; }
}

/// <summary>A TerrainHeaven project file (version 1), field for field.</summary>
public sealed class ProjectData
{
    public int Version { get; set; } = 1;

    public ProjectSettingsData Settings { get; set; } = new();

    public List<ElementData> Elements { get; set; } = [];

    public List<ConnectionData> Connections { get; set; } = [];

    public List<CutPointData>? TerrainCutPoints { get; set; }

    /// <summary>Every texture the project references, so a host can preload or report missing files.</summary>
    public List<string>? TexturePaths { get; set; }

    public FoliageProjectData? Foliage { get; set; }

    public MeshProjectData? Meshes { get; set; }
}

/// <summary>
/// A whole terrain project: the element world, its settings, foliage and placed props, saved and loaded in TerrainHeaven's
/// JSON format. Project files are untrusted input: malformed data throws <see cref="InvalidDataException"/>, unknown
/// element types (including TerrainHeaven buildings, which are not ported) and broken connections are skipped with a warning.
/// </summary>
public sealed partial class TerrainProject
{
    public const int FormatVersion = 1;

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy { ProcessDictionaryKeys = false } },
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.Indented,
        MaxDepth = 64,
        MissingMemberHandling = MissingMemberHandling.Ignore,
    };

    private readonly ILogger? _logger;

    public TerrainProject(ILogger? logger = null)
    {
        _logger = logger;
        World = new TerrainWorld(logger);
    }

    public TerrainWorld World { get; }

    public ProjectSettings Settings { get; } = new();

    public FoliageStore Foliage { get; } = new();

    public MeshStore Meshes { get; } = new();

    public string Save() => JsonConvert.SerializeObject(ToData(), JsonSettings);

    public ProjectData ToData()
    {
        var elements = World.Elements;
        var ids = new Dictionary<WorldElement, int>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < elements.Count; index++)
        {
            ids[elements[index]] = index;
        }

        var elementData = elements.Select((element, index) => element.Serialize(index)).ToList();

        // Each connection is stored on both elements; keep it once.
        var connections = new List<ConnectionData>();
        var seen = new HashSet<(int, int, int, int)>();
        foreach (var element in elements)
        {
            var idA = ids[element];
            foreach (var (nodeA, connection) in element.Connections)
            {
                if (!ids.TryGetValue(connection.Element, out var idB))
                {
                    continue;
                }

                var key = (Math.Min(idA, idB), Math.Max(idA, idB), idA < idB ? nodeA : connection.NodeIndex, idA < idB ? connection.NodeIndex : nodeA);
                if (seen.Add(key))
                {
                    connections.Add(new ConnectionData { ElementA = idA, NodeA = nodeA, ElementB = idB, NodeB = connection.NodeIndex });
                }
            }
        }

        var foliage = Foliage.Serialize();
        return new ProjectData
        {
            Settings = Settings.GetData(),
            Elements = elementData,
            Connections = connections,
            TerrainCutPoints = World.CutPoints.Serialize(),
            TexturePaths = [.. elementData.SelectMany(e => e.Textures.Values)
                .Concat(elementData.SelectMany(e => (e.TerrainTextureLayers ?? []).Select(l => l.TexturePath)))
                .Concat(foliage.Types.Select(t => t.TexturePath))
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.Ordinal)],
            Foliage = foliage,
            Meshes = Meshes.Serialize(),
        };
    }

    /// <summary>Replaces the project with a saved one.</summary>
    public void Load(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        ProjectData? data;
        try
        {
            data = JsonConvert.DeserializeObject<ProjectData>(json, JsonSettings);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Not a valid terrain project: {exception.Message}", exception);
        }

        Load(data ?? throw new InvalidDataException("Not a valid terrain project: the file is empty."));
    }

    public void Load(ProjectData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Version != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported terrain project version {data.Version} (expected {FormatVersion}).");
        }

        World.Clear();
        World.CutPoints.Clear();
        Settings.LoadData(data.Settings);

        var elements = new List<WorldElement?>();
        foreach (var elementData in data.Elements ?? [])
        {
            var element = elementData is null ? null : CreateElement(elementData);
            elements.Add(element);
            if (element is null)
            {
                continue;
            }

            element.LoadCommon(new ElementData
            {
                Textures = (elementData!.Textures ?? []).ToDictionary(p => p.Key, p => NormalizeTexturePath(p.Value), StringComparer.Ordinal),
                TextureRotations = elementData.TextureRotations ?? [],
            });
            if (element is Elements.Terrain terrain)
            {
                foreach (var layer in terrain.TextureLayers.Where(l => !string.IsNullOrEmpty(l.TexturePath)))
                {
                    layer.TexturePath = NormalizeTexturePath(layer.TexturePath);
                }
            }

            World.Add(element, false);
        }

        foreach (var connection in data.Connections ?? [])
        {
            if (connection is null || !TryConnect(elements, connection))
            {
                LogBrokenConnection(connection?.ElementA ?? -1, connection?.NodeA ?? -1, connection?.ElementB ?? -1, connection?.NodeB ?? -1);
            }
        }

        World.CutPoints.Load((data.TerrainCutPoints ?? []).Where(p => p is not null));
        Foliage.Load(data.Foliage);
        foreach (var type in Foliage.Types)
        {
            type.TexturePath = string.IsNullOrEmpty(type.TexturePath) ? "" : NormalizeTexturePath(type.TexturePath);
        }

        Meshes.Load(data.Meshes);
        World.Update();
    }

    /// <summary>Every texture path the loaded project references (elements, terrain paint layers, foliage).</summary>
    public IReadOnlyList<string> GetTexturePaths() => ToData().TexturePaths ?? [];

    private WorldElement? CreateElement(ElementData data)
    {
        data.Nodes ??= [];
        data.Textures ??= [];
        data.TextureRotations ??= [];
        WorldElement? element = data.Type switch
        {
            "road" => Road.Deserialize(data),
            "intersection" => Intersection.Deserialize(data),
            "terrain" => Elements.Terrain.Deserialize(data, message => LogElementWarning(data.Id, message)),
            "terrainCutSpline" => TerrainCutSpline.Deserialize(data),
            "river" => RiverSpline.Deserialize(data),
            "fence" => Fence.Deserialize(data),
            "stairs" => Stairs.Deserialize(data),
            "terrainPolygon" => PolygonTerrain.Deserialize(data),
            _ => null,
        };
        if (element is null)
        {
            LogUnsupportedElement(data.Id, data.Type ?? "");
        }

        return element;
    }

    private static bool TryConnect(List<WorldElement?> elements, ConnectionData connection)
    {
        if (connection.ElementA < 0 || connection.ElementA >= elements.Count || connection.ElementB < 0 || connection.ElementB >= elements.Count)
        {
            return false;
        }

        var (a, b) = (elements[connection.ElementA], elements[connection.ElementB]);
        if (a is null || b is null || ReferenceEquals(a, b)
            || connection.NodeA < 0 || connection.NodeA >= a.NodeCount || connection.NodeB < 0 || connection.NodeB >= b.NodeCount)
        {
            return false;
        }

        a.Connect(connection.NodeA, b, connection.NodeB);
        return true;
    }

    /// <summary>
    /// The project-relative form of a texture reference: embedded data URLs (old projects) become a stable
    /// "legacy-texture-&lt;fnv1a&gt;.png" name, URLs their decoded file name, file paths forward-slashed without "./".
    /// </summary>
    public static string NormalizeTexturePath(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.StartsWith("data:", StringComparison.Ordinal))
        {
            var hash = 2166136261u;
            foreach (var c in source)
            {
                hash ^= c;
                hash *= 16777619;
            }

            return $"legacy-texture-{hash:x}.png";
        }

        // A one-letter scheme is a Windows drive ("C:\..."): a path here, not a URL.
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1 && !uri.IsFile)
        {
            var name = Uri.UnescapeDataString(uri.AbsolutePath.Split('/')[^1]);
            return name.Length > 0 ? name : "texture.png";
        }

        var path = source.Replace('\\', '/');
        return path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path;
    }

    [LoggerMessage(EventId = 7110, Level = LogLevel.Warning, Message = "Terrain project element {Id} has unsupported type '{Type}'; skipped")]
    private static partial void LogUnsupportedElementMessage(ILogger logger, int id, string type);

    [LoggerMessage(EventId = 7111, Level = LogLevel.Warning, Message = "Terrain project connection {ElementA}:{NodeA} - {ElementB}:{NodeB} points at a missing element or node; skipped")]
    private static partial void LogBrokenConnectionMessage(ILogger logger, int elementA, int nodeA, int elementB, int nodeB);

    [LoggerMessage(EventId = 7112, Level = LogLevel.Warning, Message = "Terrain project element {Id}: {Detail}")]
    private static partial void LogElementWarningMessage(ILogger logger, int id, string detail);

    private void LogUnsupportedElement(int id, string type)
    {
        if (_logger is not null)
        {
            LogUnsupportedElementMessage(_logger, id, type);
        }
    }

    private void LogBrokenConnection(int elementA, int nodeA, int elementB, int nodeB)
    {
        if (_logger is not null)
        {
            LogBrokenConnectionMessage(_logger, elementA, nodeA, elementB, nodeB);
        }
    }

    private void LogElementWarning(int id, string detail)
    {
        if (_logger is not null)
        {
            LogElementWarningMessage(_logger, id, detail);
        }
    }
}
