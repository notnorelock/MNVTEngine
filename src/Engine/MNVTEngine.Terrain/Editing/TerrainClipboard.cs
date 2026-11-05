using MNVTEngine.Terrain.Elements;
using MNVTEngine.Terrain.Geometry;

namespace MNVTEngine.Terrain.Editing;

public enum CopyMode
{
    /// <summary>Ctrl+C on an element: paste spawns a new one in front of the camera.</summary>
    Element,

    /// <summary>"Copy properties": paste applies them onto a selected element of the same type.</summary>
    Properties,
}

/// <summary>
/// Copy and paste of elements and their settings: every inspector value (except positions and buttons), texture
/// references, texture rotations and UV transforms. Pasting a whole element keeps the source's span and direction
/// (roads, fences, stairs), outlet count (intersections) or size (terrain tiles).
/// </summary>
public sealed class TerrainClipboard
{
    private Dictionary<string, object>? _properties;
    private Dictionary<string, string> _textures = [];
    private Dictionary<string, double> _rotations = [];
    private Dictionary<string, UVTransform> _uvTransforms = [];
    private double? _length;
    private Vec3? _direction;
    private double? _rise;
    private int? _outletCount;
    private (double Width, double Length)? _terrainSize;

    public CopyMode? Mode { get; private set; }

    /// <summary>The copied element's type name (as TerrainHeaven names them: Road, Terrain, ...).</summary>
    public string? CopiedType { get; private set; }

    public bool HasCopy => Mode is not null;

    public void CopyElement(WorldElement element)
    {
        Snapshot(element, CopyMode.Element);
        switch (element)
        {
            case Stairs stairs:
                var (a, b) = (stairs.NodeA.Position, stairs.NodeB.Position);
                _length = MathUtil.Hypot(b.X - a.X, b.Z - a.Z);
                _direction = new Vec3(b.X - a.X, 0, b.Z - a.Z).Normalized();
                _rise = b.Y - a.Y;
                break;
            case SplineElement spline when spline is Road or Fence:
                _length = spline.NodeA.Position.DistanceTo(spline.NodeB.Position);
                _direction = (spline.NodeB.Position - spline.NodeA.Position).Normalized();
                break;
            case Intersection intersection:
                _outletCount = intersection.OutletCount;
                break;
            case Elements.Terrain terrain:
                _terrainSize = (terrain.Width, terrain.Length);
                break;
            default:
                break;
        }
    }

    public void CopyProperties(WorldElement element) => Snapshot(element, CopyMode.Properties);

    public bool CanPastePropertiesOnto(WorldElement element) => Mode == CopyMode.Properties && CopiedType == element?.GetType().Name;

    public bool CanPasteElement() => Mode == CopyMode.Element && CopiedType is not null;

    public void PastePropertiesOnto(WorldElement element) => Apply(element);

    /// <summary>
    /// Spawns a copy 8 m in front of the camera (on y = 0), adds it to the world and applies the snapshot. Returns null
    /// for element types that cannot be pasted (rivers, cut splines, polygons).
    /// </summary>
    public WorldElement? PasteElement(Vec3 cameraPosition, Vec3 cameraForward, TerrainWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (Mode is null || CopiedType is null)
        {
            return null;
        }

        var forward = new Vec3(cameraForward.X, 0, cameraForward.Z);
        forward = forward.LengthSquared < 0.001 ? new Vec3(0, 0, -1) : forward.Normalized();
        var center = (cameraPosition + (forward * 8)).WithY(0);

        WorldElement? element = null;
        if (CopiedType is nameof(Road) or nameof(Fence) or nameof(Stairs) && _direction is { } copied && _length is { } length)
        {
            var direction = new Vec3(copied.X, 0, copied.Z);
            direction = direction.LengthSquared < 0.001 ? Vec3.UnitX : direction.Normalized();
            var a = center - (direction * (length / 2));
            var b = center + (direction * (length / 2));
            element = CopiedType switch
            {
                nameof(Fence) => new Fence(a, b),
                nameof(Stairs) => new Stairs(a, b + new Vec3(0, _rise ?? 3, 0)),
                _ => new Road(a, b),
            };
        }
        else if (CopiedType == nameof(Intersection) && _outletCount is { } outlets)
        {
            element = new Intersection(center, outlets);
        }
        else if (CopiedType == nameof(Elements.Terrain) && _terrainSize is { } size)
        {
            element = new Elements.Terrain(center, size.Width, size.Length);
        }

        if (element is null)
        {
            return null;
        }

        world.Add(element);
        Apply(element);
        return element;
    }

    /// <summary>The value-carrying inspector fields keyed by section and label (shared with presets).</summary>
    internal static IEnumerable<(string Section, PropertyItem Property)> ValueProperties(WorldElement element) =>
        element.GetProperties().Sections.SelectMany(s => s.Properties.Where(p => p is not ButtonProperty and not Vector3Property).Select(p => (s.Label, p)));

    internal static object? GetValue(PropertyItem property) => property switch
    {
        NumberProperty n => n.Get(),
        BooleanProperty b => b.Get(),
        SelectProperty s => s.Get(),
        _ => null,
    };

    /// <summary>Sets a value when its type matches the field (a stale snapshot never throws).</summary>
    internal static void SetValue(PropertyItem property, object value)
    {
        switch (property, value)
        {
            case (NumberProperty n, double d):
                n.Set(d);
                break;
            case (BooleanProperty b, bool flag):
                b.Set(flag);
                break;
            case (SelectProperty s, string text):
                s.Set(text);
                break;
            default:
                break;
        }
    }

    private static string Key(string section, string label) =>
        System.Text.RegularExpressions.Regex.Replace($"{section}-{label}", @"\s+", "-").ToLowerInvariant();

    private void Snapshot(WorldElement element, CopyMode mode)
    {
        ArgumentNullException.ThrowIfNull(element);
        Mode = mode;
        CopiedType = element.GetType().Name;
        (_length, _direction, _rise, _outletCount, _terrainSize) = (null, null, null, null, null);
        _properties = [];
        foreach (var (section, property) in ValueProperties(element))
        {
            if (GetValue(property) is { } value)
            {
                _properties[Key(section, property.Label)] = value;
            }
        }

        _textures = new Dictionary<string, string>(element.GetGroupTextureReferences(), StringComparer.Ordinal);
        _rotations = new Dictionary<string, double>(element.TextureRotations, StringComparer.Ordinal);
        _uvTransforms = element.GetUVGroups().ToDictionary(g => g, element.GetUVTransform, StringComparer.Ordinal);
    }

    private void Apply(WorldElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (_properties is not null)
        {
            foreach (var (section, property) in ValueProperties(element))
            {
                if (_properties.TryGetValue(Key(section, property.Label), out var value))
                {
                    SetValue(property, value);
                }
            }
        }

        foreach (var (group, path) in _textures)
        {
            element.SetGroupTextureReference(group, path);
        }

        foreach (var (group, degrees) in _rotations)
        {
            element.SetTextureRotation(group, degrees);
        }

        var targetGroups = element.GetUVGroups().ToHashSet(StringComparer.Ordinal);
        foreach (var (group, transform) in _uvTransforms.Where(t => targetGroups.Contains(t.Key)))
        {
            element.SetUVTransform(group, transform.Clone());
        }
    }
}
