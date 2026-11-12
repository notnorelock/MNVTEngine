using MNVTEngine.Mathematics;
using Microsoft.Extensions.Logging;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;
using MNVTEngine.Terrain.Project;

namespace MNVTEngine;

/// <summary>
/// A terrain world built with the terrain tools (a <c>.mnvtterrain</c> asset: TerrainHeaven project JSON) on this
/// GameObject: terrain tiles, roads, intersections, rivers, stairs, fences, polygon patches, foliage and placed props,
/// in the GameObject's local space. Rendered by the host; a <c>MeshCollider</c> on the same GameObject collides with its
/// opaque surfaces (water does not collide).
/// </summary>
[ComponentId("MNVTEngine.WorldTerrain")]
public sealed partial class WorldTerrain : Component, IMeshDataSource
{
    private AssetRef _project;
    private int _lodLevel;
    private TerrainProject? _data;
    private bool _loadAttempted;
    private MeshData? _collision;
    private int _collisionVersion = -1;

    /// <summary>The terrain world asset.</summary>
    [SerializeField]
    [AssetType(AssetTypes.Terrain)]
    public AssetRef Project
    {
        get => _project;
        set
        {
            if (_project != value)
            {
                _project = value;
                Reload();
            }
        }
    }

    /// <summary>Level of detail the world is built at: 0 (as authored) to 3 (coarsest).</summary>
    [SerializeField]
    public int LodLevel
    {
        get => _lodLevel;
        set
        {
            var clamped = Math.Clamp(value, 0, Terrain.Geometry.LodLevels.MaxLodIndex);
            if (_lodLevel != clamped)
            {
                _lodLevel = clamped;
                if (_data is not null)
                {
                    _data.World.LodPreviewLevel = clamped;
                    MarkChanged();
                }
            }
        }
    }

    [SerializeField]
    public bool DrawFoliage { get; set; } = true;

    [SerializeField]
    public bool DrawProps { get; set; } = true;

    /// <summary>The loaded world (loaded on first use); null when there is no asset or it failed to load (see <see cref="LoadError"/>).</summary>
    public TerrainProject? Data
    {
        get
        {
            EnsureLoaded();
            return _data;
        }
    }

    /// <summary>Why the asset could not be loaded, or null.</summary>
    public string? LoadError { get; private set; }

    /// <summary>Changes whenever the geometry, foliage or props change, so renderers know when to rebuild.</summary>
    public int Version { get; private set; }

    /// <summary>Uses an in-memory world instead of the asset (the terrain editor edits this one live).</summary>
    public void SetData(TerrainProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _data = project;
        _loadAttempted = true;
        LoadError = null;
        project.World.LodPreviewLevel = _lodLevel;
        MarkChanged();
    }

    /// <summary>Loads the asset again on next use (it changed on disk).</summary>
    public void Reload()
    {
        _data = null;
        _loadAttempted = false;
        LoadError = null;
        MarkChanged();
    }

    /// <summary>Call after editing <see cref="Data"/>: renderers rebuild and colliders take the new triangles.</summary>
    public void MarkChanged()
    {
        Version++;
        if (GameObject is { } owner)
        {
            foreach (var collider in owner.GetComponents<Collider>())
            {
                Physics.RefreshShape(collider);
            }
        }
    }

    /// <summary>Distance along a world-space ray to the nearest surface of the world (any element), or null.</summary>
    public float? Raycast(Ray ray)
    {
        if (Data is not { } data || GameObject is null)
        {
            return null;
        }

        var toWorld = Transform.LocalToWorldMatrix;
        if (!Matrix4x4.TryInvert(toWorld, out var toLocal))
        {
            return null;
        }

        var origin = Point(toLocal, ray.Origin, 1f);
        var direction = Point(toLocal, ray.Direction, 0f);
        double? best = null;
        foreach (var triangle in data.World.Elements.SelectMany(e => e.Geometry).SelectMany(g => g.Triangles))
        {
            if (Terrain.Geometry.RayCasting.IntersectTriangle(origin, direction, triangle.A, triangle.B, triangle.C) is { } t && (best is null || t < best))
            {
                best = t;
            }
        }

        return best is { } hit ? Vector3.Distance(ray.Origin, Point(toWorld, origin + (direction * hit), 1f)) : null;

        static Vector3 Point(Matrix4x4 m, Vector3 v, float w)
        {
            var r = m * new Vector4(v.X, v.Y, v.Z, w);
            return new Vector3(r.X, r.Y, r.Z);
        }
    }

    /// <summary>Opaque surfaces of every element, in local space; translucent ones (water) are left out.</summary>
    public MeshData? GetMeshData()
    {
        if (Data is not { } data)
        {
            return null;
        }

        if (_collisionVersion != Version)
        {
            var positions = new List<Mathematics.Vector3>();
            foreach (var element in data.World.Elements)
            {
                foreach (var group in element.Geometry)
                {
                    if (element.GetMaterialHint(group.Name) is { } hint && (hint.Opacity < 1 || !hint.DepthWrite))
                    {
                        continue;
                    }

                    foreach (var t in group.Triangles)
                    {
                        positions.Add(t.A);
                        positions.Add(t.B);
                        positions.Add(t.C);
                    }
                }
            }

            _collision = positions.Count == 0 ? null : new MeshData([.. positions], [.. Enumerable.Range(0, positions.Count)]);
            _collisionVersion = Version;
        }

        return _collision;
    }

    private void EnsureLoaded()
    {
        if (_loadAttempted || _project.IsNone || GameObject is null)
        {
            return;
        }

        _loadAttempted = true;
        var scene = GameObject.Scene;
        if (scene.TextAssets is not { } reader)
        {
            LoadError = "This host cannot read assets.";
            return;
        }

        try
        {
            var project = new TerrainProject(scene.Logger);
            project.Load(reader.ReadText(_project, AssetTypes.Terrain));
            project.World.LodPreviewLevel = _lodLevel;
            if (_lodLevel > 0)
            {
                project.World.Update();
            }

            _data = project;
            Version++;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
        {
            LoadError = ex.Message;
            LogLoadFailed(scene.Logger, ex, GameObject.Name, _project.ToString());
        }
    }

    [LoggerMessage(EventId = 7130, Level = LogLevel.Error, Message = "WorldTerrain on '{GameObject}': {Asset} could not be loaded")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception, string gameObject, string asset);
}

public static class TerrainComponents
{
    public static ComponentRegistry Register(ComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Register<WorldTerrain>();
    }
}

/// <summary>The "terrain" engine module. Terrain: registers terrain worlds.</summary>
public sealed class TerrainModule : Scenes.IEngineModule, Runtime.IResourceIntegration, Runtime.IMeshItemIntegration
{
    public string Id => "terrain";

    public void Register(Scenes.ComponentRegistry components) => TerrainComponents.Register(components);

    public void RegisterResources(Runtime.ResourceCache resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (!resources.HasBuiltinShader(Graphics.Materials.MaterialShaders.TerrainSplatId))
        {
            resources.RegisterBuiltinShader(Graphics.Materials.MaterialShaders.TerrainSplatId, Graphics.Materials.MaterialShaders.TerrainSplat);
        }
    }

    /// <summary>Terrain has no pass of its own: it draws in world.3d's pass, with its lights and shadows.</summary>
    public Runtime.IMeshItemSource CreateItemSource(Runtime.RenderServices services) => new Runtime.TerrainMeshSource(services);
}
