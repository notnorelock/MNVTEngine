using MNVTEngine.Terrain.Elements;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace MNVTEngine.Terrain.Meshes;

/// <summary>
/// A placeable prop: a reference into the mesh library (<see cref="LibraryPath"/>, the main file's relative path) plus
/// the unit/pivot calibration baked into every placed instance. <see cref="Id"/> is a short file-name-safe identity
/// used to name exported files.
/// </summary>
[JsonObject(NamingStrategyType = typeof(DefaultNamingStrategy))]
public sealed class MeshAssetData
{
    public string Id { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string LibraryPath { get; set; } = "";

    public double BaseScale { get; set; } = 1;

    public PointData PositionOffset { get; set; } = new();

    public static MeshAssetData Create(string displayName, string libraryPath) => new()
    {
        Id = Geometry.Js.NewId(),
        DisplayName = displayName,
        LibraryPath = libraryPath,
    };

    public MeshAssetData Clone()
    {
        var clone = (MeshAssetData)MemberwiseClone();
        clone.PositionOffset = PointData.From((PositionOffset ?? new PointData()).ToVec3());
        return clone;
    }

}

/// <summary>One placed prop: full Euler rotation in degrees (props rest at any orientation) and a uniform scale.</summary>
[JsonObject(NamingStrategyType = typeof(DefaultNamingStrategy))]
public sealed class MeshInstanceData
{
    public PointData Position { get; set; } = new();

    public double RotationX { get; set; }

    public double RotationY { get; set; }

    public double RotationZ { get; set; }

    public double Scale { get; set; } = 1;

    public int AssetIndex { get; set; }

    public MeshInstanceData Clone()
    {
        var clone = (MeshInstanceData)MemberwiseClone();
        clone.Position = PointData.From(Position.ToVec3());
        return clone;
    }
}

[JsonObject(NamingStrategyType = typeof(DefaultNamingStrategy))]
public sealed class MeshAssetLayerData
{
    public List<MeshInstanceData> Instances { get; set; } = [];
}

[JsonObject(NamingStrategyType = typeof(DefaultNamingStrategy))]
public sealed class MeshProjectData
{
    public int Version { get; set; } = 1;

    public List<MeshAssetData> Assets { get; set; } = [];

    public List<MeshAssetLayerData> Layers { get; set; } = [];
}

/// <summary>Prop assets and their placed instances, one layer of instances per asset.</summary>
public sealed class MeshStore
{
    private List<MeshAssetLayerData> _layers;

    public MeshStore(IEnumerable<MeshAssetData>? assets = null)
    {
        Assets = assets?.Select(a => a.Clone()).ToList() ?? [];
        _layers = Assets.Select(_ => new MeshAssetLayerData()).ToList();
    }

    public List<MeshAssetData> Assets { get; private set; }

    public int AddAsset(MeshAssetData asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        Assets.Add(asset.Clone());
        _layers.Add(new MeshAssetLayerData());
        return Assets.Count - 1;
    }

    public void RenameAsset(int assetIndex, string displayName)
    {
        if (assetIndex >= 0 && assetIndex < Assets.Count)
        {
            Assets[assetIndex].DisplayName = displayName;
        }
    }

    /// <summary>Sets the calibration; a non-positive scale is ignored.</summary>
    public void UpdateAsset(int assetIndex, double? baseScale = null, Geometry.Vec3? positionOffset = null)
    {
        if (assetIndex < 0 || assetIndex >= Assets.Count)
        {
            return;
        }

        var asset = Assets[assetIndex];
        if (baseScale is > 0)
        {
            asset.BaseScale = baseScale.Value;
        }

        if (positionOffset is { } offset)
        {
            asset.PositionOffset = PointData.From(offset);
        }
    }

    public bool RemoveAsset(int assetIndex)
    {
        if (assetIndex < 0 || assetIndex >= Assets.Count)
        {
            return false;
        }

        Assets.RemoveAt(assetIndex);
        _layers.RemoveAt(assetIndex);
        for (var index = assetIndex; index < _layers.Count; index++)
        {
            foreach (var instance in _layers[index].Instances)
            {
                instance.AssetIndex = index;
            }
        }

        return true;
    }

    public IReadOnlyList<MeshInstanceData> GetInstances(int assetIndex)
    {
        EnsureLayer(assetIndex);
        return _layers[assetIndex].Instances;
    }

    public void AddInstance(int assetIndex, MeshInstanceData instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        EnsureLayer(assetIndex);
        var copy = instance.Clone();
        copy.AssetIndex = assetIndex;
        _layers[assetIndex].Instances.Add(copy);
    }

    public bool IsTooClose(int assetIndex, Geometry.Vec3 point, double spacing) =>
        GetInstances(assetIndex).Any(i => i.Position.ToVec3().DistanceToSquared(point) < spacing * spacing);

    public bool RemoveInstanceAt(int assetIndex, int instanceIndex)
    {
        if (assetIndex < 0 || assetIndex >= _layers.Count || instanceIndex < 0 || instanceIndex >= _layers[assetIndex].Instances.Count)
        {
            return false;
        }

        _layers[assetIndex].Instances.RemoveAt(instanceIndex);
        return true;
    }

    public int RemoveInstancesNear(Geometry.Vec3 position, double radius) =>
        _layers.Sum(layer => layer.Instances.RemoveAll(i => i.Position.ToVec3().DistanceToSquared(position) <= radius * radius));

    public void Clear() => _layers = Assets.Select(_ => new MeshAssetLayerData()).ToList();

    public MeshProjectData Serialize() => new()
    {
        Assets = [.. Assets.Select(a => a.Clone())],
        Layers = [.. _layers.Select(l => new MeshAssetLayerData { Instances = [.. l.Instances.Select(i => i.Clone())] })],
    };

    /// <summary>Loads saved props; files from before calibration existed get scale 1 and no offset.</summary>
    public void Load(MeshProjectData? data)
    {
        Assets = (data?.Assets ?? []).Where(a => a is not null).Select(a =>
        {
            var asset = a.Clone();
            asset.BaseScale = a.BaseScale > 0 && double.IsFinite(a.BaseScale) ? a.BaseScale : 1;
            asset.Id ??= "";
            asset.DisplayName ??= "";
            asset.LibraryPath ??= "";
            return asset;
        }).ToList();
        _layers = Assets.Select((_, index) => new MeshAssetLayerData
        {
            Instances = [.. (data?.Layers?.ElementAtOrDefault(index)?.Instances ?? []).Where(i => i is not null).Select(i => new MeshInstanceData
            {
                Position = PointData.From((i.Position ?? new PointData()).ToVec3()),
                RotationX = i.RotationX,
                RotationY = i.RotationY,
                RotationZ = i.RotationZ,
                Scale = i.Scale,
                AssetIndex = index,
            })],
        }).ToList();
    }

    private void EnsureLayer(int assetIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(assetIndex);
        while (_layers.Count <= assetIndex)
        {
            _layers.Add(new MeshAssetLayerData());
        }
    }
}
