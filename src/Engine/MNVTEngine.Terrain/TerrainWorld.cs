using Microsoft.Extensions.Logging;
using MNVTEngine.Terrain.Elements;
using MNVTEngine.Terrain.Geometry;
using MNVTEngine.Terrain.Meshing;

namespace MNVTEngine.Terrain;

/// <summary>Terrain cut points: single nodes forcing the terrain to their height, blending over their radius.</summary>
public sealed class TerrainCutPoints(TerrainWorld world)
{
    public const double DefaultRadius = 4;
    private readonly List<WorldNode> _points = [];
    private readonly Dictionary<WorldNode, double> _radii = new(ReferenceEqualityComparer.Instance);

    public IReadOnlyList<WorldNode> Points => _points;

    public WorldNode AddPoint(Vec3 position)
    {
        var node = new WorldNode(position, NodeKind.TerrainCut);
        _points.Add(node);
        _radii[node] = DefaultRadius;
        world.MarkTerrainDirty();
        return node;
    }

    public double GetRadius(WorldNode node) => _radii.GetValueOrDefault(node, DefaultRadius);

    public void SetRadius(WorldNode node, double radius)
    {
        if (_radii.ContainsKey(node))
        {
            _radii[node] = Math.Max(0.1, radius);
            world.MarkTerrainDirty();
        }
    }

    public void Move(WorldNode node, Vec3 position)
    {
        node.Position = position;
        world.MarkTerrainDirty();
    }

    public IReadOnlyList<TerrainCutPointInput> GetPointsWithRadius() => _points.Select(node => new TerrainCutPointInput(node.Position, GetRadius(node))).ToList();

    public void RemovePoint(WorldNode node)
    {
        if (_points.Remove(node))
        {
            _radii.Remove(node);
            world.MarkTerrainDirty();
        }
    }

    public void Clear()
    {
        _points.Clear();
        _radii.Clear();
        world.MarkTerrainDirty();
    }

    public bool IsCutPoint(WorldNode node) => _radii.ContainsKey(node);

    public List<CutPointData> Serialize() => _points.Select(n => new CutPointData { X = n.Position.X, Y = n.Position.Y, Z = n.Position.Z, Radius = GetRadius(n) }).ToList();

    public void Load(IEnumerable<CutPointData> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        Clear();
        foreach (var point in points)
        {
            var node = new WorldNode(new Vec3(point.X, point.Y, point.Z), NodeKind.TerrainCut);
            _points.Add(node);
            _radii[node] = point.Radius ?? DefaultRadius;
        }

        world.MarkTerrainDirty();
    }

    public PropertyDefinition? GetProperties(WorldNode node)
    {
        if (!IsCutPoint(node))
        {
            return null;
        }

        return new PropertyDefinition("Terrain Cut Point", "●",
        [
            new PropertySection("Cut Point",
            [
                new Vector3Property("Position", () => node.Position, v => Move(node, v)),
                new NumberProperty("Distance", () => GetRadius(node), v => SetRadius(node, v), 0.1, 100, 0.1),
                new ButtonProperty("Remove", () => RemovePoint(node)),
            ]),
        ]);
    }
}

public sealed class CutPointData
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Z { get; set; }

    public double? Radius { get; set; }
}

/// <summary>
/// A TerrainHeaven world: its elements, terrain cut points, foliage and placed meshes, with the services the original
/// reached through singletons (the terrain group mesher, the LOD preview level). Rebuilds keep TerrainHeaven's order:
/// terrain after everything that cuts it, then the elements standing on the terrain.
/// </summary>
public sealed class TerrainWorld
{
    private readonly List<WorldElement> _elements = [];
    private bool _terrainDirty;
    private bool _terrainDependentsDirty;
    private bool _terrainSurfaceChangedDirty;
    private bool _isUpdatingAll;
    private int _lodPreviewLevel;

    public TerrainWorld(ILogger? logger = null)
    {
        Logger = logger;
        GroupMesher = new TerrainGroupMesher(this, logger);
        CutPoints = new TerrainCutPoints(this);
    }

    public ILogger? Logger { get; }

    public TerrainGroupMesher GroupMesher { get; }

    public TerrainCutPoints CutPoints { get; }

    public IReadOnlyList<WorldElement> Elements => _elements;

    /// <summary>Raised after any element's geometry changed (the editor redraws, exporters go stale).</summary>
    public event Action? SceneGeometryChanged;

    /// <summary>Raised after the terrain surface changed (things standing on it re-sample their heights).</summary>
    public event Action? TerrainSurfaceChanged;

    /// <summary>The editor's LOD Preview (0-3): a session-only look at an export LOD level; never saved.</summary>
    public int LodPreviewLevel
    {
        get => _lodPreviewLevel;
        set
        {
            var clamped = Math.Clamp(value, 0, LodLevels.MaxLodIndex);
            if (clamped != _lodPreviewLevel)
            {
                _lodPreviewLevel = clamped;
                Update();
            }
        }
    }

    public void Add(WorldElement element, bool update = true)
    {
        ArgumentNullException.ThrowIfNull(element);
        _elements.Add(element);
        element.World = this;
        element.OnGeometryChanged = () =>
        {
            if (_isUpdatingAll)
            {
                return;
            }

            if (element.IsTerrainSurface())
            {
                // Adjacent tiles share one surface: rebuild the neighbours on the next flush whenever one side changes.
                _terrainDirty = true;
                _terrainDependentsDirty = true;
                _terrainSurfaceChangedDirty = true;
            }
            else
            {
                MarkTerrainDirty();
            }

            SceneGeometryChanged?.Invoke();
        };
        if (update)
        {
            element.Update();
        }
    }

    public bool Remove(WorldElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!_elements.Contains(element))
        {
            return false;
        }

        foreach (var nodeIndex in element.Connections.Keys.ToList())
        {
            element.Disconnect(nodeIndex);
        }

        _elements.Remove(element);
        element.Dispose();
        element.World = null;
        MarkTerrainDirty();
        SceneGeometryChanged?.Invoke();
        return true;
    }

    public void Clear()
    {
        foreach (var element in _elements)
        {
            element.Dispose();
            element.World = null;
        }

        _elements.Clear();
        _terrainDirty = false;
        _terrainDependentsDirty = false;
        _terrainSurfaceChangedDirty = false;
        TerrainSurfaceChanged?.Invoke();
        SceneGeometryChanged?.Invoke();
    }

    /// <summary>Rebuilds everything: roads and other cutters, then terrain, then what stands on the terrain.</summary>
    public void Update()
    {
        _isUpdatingAll = true;
        try
        {
            foreach (var element in _elements.Where(e => !e.IsTerrainSurface()).ToList())
            {
                element.Update();
            }

            foreach (var element in _elements.Where(e => e.IsTerrainSurface()).ToList())
            {
                element.Update();
            }

            foreach (var element in _elements.Where(e => e.DependsOnTerrainSurface()).ToList())
            {
                element.Update();
            }

            _terrainDirty = false;
            _terrainDependentsDirty = false;
            _terrainSurfaceChangedDirty = false;
        }
        finally
        {
            _isUpdatingAll = false;
        }

        TerrainSurfaceChanged?.Invoke();
        SceneGeometryChanged?.Invoke();
    }

    public void MarkTerrainDirty() => _terrainDirty = true;

    /// <summary>Rebuilds terrain and its dependents if anything marked them dirty (call once per editor frame).</summary>
    public void FlushDirty()
    {
        if ((!_terrainDirty && !_terrainDependentsDirty && !_terrainSurfaceChangedDirty) || _isUpdatingAll)
        {
            return;
        }

        var rebuildTerrain = _terrainDirty;
        var rebuildDependents = _terrainDependentsDirty || rebuildTerrain;
        var surfaceChanged = _terrainSurfaceChangedDirty || rebuildTerrain;
        _terrainDirty = false;
        _terrainDependentsDirty = false;
        _terrainSurfaceChangedDirty = false;
        _isUpdatingAll = true;
        try
        {
            if (rebuildTerrain)
            {
                foreach (var element in _elements.Where(e => e.IsTerrainSurface()).ToList())
                {
                    element.Update();
                }
            }

            if (rebuildDependents)
            {
                foreach (var element in _elements.Where(e => e.DependsOnTerrainSurface()).ToList())
                {
                    element.Update();
                }
            }
        }
        finally
        {
            _isUpdatingAll = false;
        }

        if (surfaceChanged)
        {
            TerrainSurfaceChanged?.Invoke();
        }

        SceneGeometryChanged?.Invoke();
    }

    public IEnumerable<Elements.Terrain> Terrains => _elements.OfType<Elements.Terrain>();
}
