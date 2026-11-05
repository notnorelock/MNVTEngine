using MNVTEngine.Terrain.Elements;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace MNVTEngine.Terrain.Foliage;

public sealed class FoliageColor
{
    public double R { get; set; } = 1;

    public double G { get; set; } = 1;

    public double B { get; set; } = 1;

    public double A { get; set; } = 1;

    public FoliageColor Clone() => (FoliageColor)MemberwiseClone();
}

/// <summary>One placed billboard. PascalCase in project files: the Unity exporter's record, copied as is.</summary>
[JsonObject(NamingStrategyType = typeof(DefaultNamingStrategy))]
public sealed class FoliageInstanceData
{
    public PointData Position { get; set; } = new();

    public double RotationY { get; set; }

    /// <summary>0..1 between the type's MinSize and MaxSize, so editing the preset resizes every placed instance.</summary>
    public double ScaleT { get; set; }

    /// <summary>0..1 between the type's ColorA and ColorB.</summary>
    public double ColorT { get; set; }

    public int TypeIndex { get; set; }

    public FoliageInstanceData Clone() => new()
    {
        Position = PointData.From(Position.ToVec3()),
        RotationY = RotationY,
        ScaleT = ScaleT,
        ColorT = ColorT,
        TypeIndex = TypeIndex,
    };
}

/// <summary>A foliage preset. <see cref="TexturePath"/> is the project-relative texture the billboards use.</summary>
[JsonObject(NamingStrategyType = typeof(DefaultNamingStrategy))]
public sealed class FoliageTypeData
{
    public string DisplayName { get; set; } = "New Foliage";

    public string TexturePath { get; set; } = "";

    public double MinSize { get; set; } = 0.5;

    public double MaxSize { get; set; } = 1;

    public double LengthFactor { get; set; } = 1;

    public FoliageColor ColorA { get; set; } = new();

    public FoliageColor ColorB { get; set; } = new();

    public double WindEffectiveness { get; set; } = 0.1;

    public double WindHeightOffset { get; set; } = 1;

    public double WindBaseOffset { get; set; }

    public double MaxDrawDistance { get; set; } = 40;

    public double AlphaCutoff { get; set; } = 0.5;

    public bool CastShadows { get; set; }

    public static FoliageTypeData CreateDefault(string displayName = "New Foliage") => new() { DisplayName = displayName };

    public FoliageTypeData Clone()
    {
        var clone = (FoliageTypeData)MemberwiseClone();
        clone.ColorA = ColorA.Clone();
        clone.ColorB = ColorB.Clone();
        return clone;
    }

    /// <summary>Billboard size for a placed instance's normalized scale.</summary>
    public (double Width, double Height) ResolveDimensions(double scaleT)
    {
        var height = MinSize + ((MaxSize - MinSize) * FoliageStore.Clamp01(scaleT));
        return (height * LengthFactor, height);
    }
}

[JsonObject(NamingStrategyType = typeof(DefaultNamingStrategy))]
public sealed class FoliageTypeLayerData
{
    public List<FoliageInstanceData> Instances { get; set; } = [];
}

[JsonObject(NamingStrategyType = typeof(DefaultNamingStrategy))]
public sealed class FoliageProjectData
{
    public int Version { get; set; } = 1;

    public List<FoliageTypeData> Types { get; set; } = [];

    public List<FoliageTypeLayerData> Layers { get; set; } = [];
}

/// <summary>Foliage presets and their placed instances, one layer of instances per preset.</summary>
public sealed class FoliageStore
{
    private List<FoliageTypeLayerData> _layers;

    public FoliageStore(IEnumerable<FoliageTypeData>? types = null)
    {
        Types = types?.Select(t => t.Clone()).ToList() ?? [];
        _layers = Types.Select(_ => new FoliageTypeLayerData()).ToList();
    }

    public List<FoliageTypeData> Types { get; private set; }

    internal static double Clamp01(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    public int AddType(FoliageTypeData? type = null)
    {
        Types.Add((type ?? FoliageTypeData.CreateDefault()).Clone());
        _layers.Add(new FoliageTypeLayerData());
        return Types.Count - 1;
    }

    public int DuplicateType(int typeIndex)
    {
        if (typeIndex < 0 || typeIndex >= Types.Count)
        {
            return -1;
        }

        var copy = Types[typeIndex].Clone();
        copy.DisplayName = $"{copy.DisplayName} Copy";
        return AddType(copy);
    }

    /// <summary>Edits a copy of the preset and stores it (callers holding the old object keep an unchanged snapshot).</summary>
    public void UpdateType(int typeIndex, Action<FoliageTypeData> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (typeIndex < 0 || typeIndex >= Types.Count)
        {
            return;
        }

        var updated = Types[typeIndex].Clone();
        edit(updated);
        Types[typeIndex] = updated;
    }

    public bool RemoveType(int typeIndex)
    {
        if (typeIndex < 0 || typeIndex >= Types.Count)
        {
            return false;
        }

        Types.RemoveAt(typeIndex);
        _layers.RemoveAt(typeIndex);
        for (var index = typeIndex; index < _layers.Count; index++)
        {
            foreach (var instance in _layers[index].Instances)
            {
                instance.TypeIndex = index;
            }
        }

        return true;
    }

    public IReadOnlyList<FoliageInstanceData> GetInstances(int typeIndex)
    {
        EnsureLayer(typeIndex);
        return _layers[typeIndex].Instances;
    }

    public void AddInstance(int typeIndex, FoliageInstanceData instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        EnsureLayer(typeIndex);
        var copy = instance.Clone();
        copy.ScaleT = Clamp01(copy.ScaleT);
        copy.ColorT = Clamp01(copy.ColorT);
        copy.TypeIndex = typeIndex;
        _layers[typeIndex].Instances.Add(copy);
    }

    public bool IsTooClose(int typeIndex, Geometry.Vec3 point, double spacing) =>
        GetInstances(typeIndex).Any(i => i.Position.ToVec3().DistanceToSquared(point) < spacing * spacing);

    /// <summary>Erases every instance (of any type) inside a sphere; returns how many went.</summary>
    public int RemoveInstancesNear(Geometry.Vec3 position, double radius) =>
        _layers.Sum(layer => layer.Instances.RemoveAll(i => i.Position.ToVec3().DistanceToSquared(position) <= radius * radius));

    public void Clear() => _layers = Types.Select(_ => new FoliageTypeLayerData()).ToList();

    public FoliageProjectData Serialize() => new()
    {
        Types = [.. Types.Select(t => t.Clone())],
        Layers = [.. _layers.Select(l => new FoliageTypeLayerData { Instances = [.. l.Instances.Select(i => i.Clone())] })],
    };

    public void Load(FoliageProjectData? data)
    {
        Types = data?.Types?.Where(t => t is not null).Select(t => t.Clone()).ToList() ?? [];
        _layers = Types.Select((_, index) => new FoliageTypeLayerData
        {
            Instances = [.. (data?.Layers?.ElementAtOrDefault(index)?.Instances ?? []).Where(i => i is not null).Select(i => new FoliageInstanceData
            {
                Position = PointData.From((i.Position ?? new PointData()).ToVec3()),
                RotationY = i.RotationY,
                ScaleT = Clamp01(i.ScaleT),
                ColorT = Clamp01(i.ColorT),
                TypeIndex = index,
            })],
        }).ToList();
    }

    private void EnsureLayer(int typeIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(typeIndex);
        while (_layers.Count <= typeIndex)
        {
            _layers.Add(new FoliageTypeLayerData());
        }
    }
}
