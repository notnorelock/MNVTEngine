using MNVTEngine.Terrain.Elements;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace MNVTEngine.Terrain.Editing;

/// <summary>A saved set of inspector values, textures and UV transforms for one element type.</summary>
public sealed class ElementPreset
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>The element's type name (Road, Terrain, ...); a preset only applies to the same type.</summary>
    public string ElementType { get; set; } = "";

    public string ElementTitle { get; set; } = "";

    /// <summary>Values keyed "section\0label": numbers, booleans or strings.</summary>
    public Dictionary<string, JValue> Properties { get; set; } = [];

    public Dictionary<string, string> Textures { get; set; } = [];

    public Dictionary<string, double> TextureRotations { get; set; } = [];

    public Dictionary<string, UVTransform> UvTransforms { get; set; } = [];

    /// <summary>Unix milliseconds.</summary>
    public long CreatedAt { get; set; }
}

/// <summary>
/// Element presets and the per-type default applied to newly placed elements. Stored as TerrainHeaven's preset JSON
/// (<see cref="ToJson"/>/<see cref="Load"/>); the host decides where it persists (editor preferences).
/// </summary>
public sealed class ElementPresets
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy { ProcessDictionaryKeys = false } },
        MaxDepth = 32,
    };

    private List<ElementPreset> _presets = [];
    private Dictionary<string, string> _defaults = new(StringComparer.Ordinal);

    /// <summary>Raised after every change; persist <see cref="ToJson"/> here.</summary>
    public event Action? Changed;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ElementPreset> GetPresets() => _presets.OrderByDescending(p => p.CreatedAt).ToList();

    public ElementPreset? GetPreset(string id) => _presets.FirstOrDefault(p => p.Id == id);

    public bool IsDefault(ElementPreset preset) => preset is not null && _defaults.GetValueOrDefault(preset.ElementType) == preset.Id;

    public ElementPreset SavePreset(string name, WorldElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        var definition = element.GetProperties();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var preset = new ElementPreset
        {
            Id = Geometry.Js.NewId(),
            Name = string.IsNullOrWhiteSpace(name) ? $"{definition.Title} Preset" : name.Trim(),
            ElementType = element.GetType().Name,
            ElementTitle = definition.Title,
            Textures = new Dictionary<string, string>(element.GetGroupTextureReferences(), StringComparer.Ordinal),
            TextureRotations = new Dictionary<string, double>(element.TextureRotations, StringComparer.Ordinal),
            UvTransforms = element.GetUVGroups().ToDictionary(g => g, element.GetUVTransform, StringComparer.Ordinal),
            CreatedAt = now,
        };
        foreach (var (section, property) in TerrainClipboard.ValueProperties(element))
        {
            if (TerrainClipboard.GetValue(property) is { } value)
            {
                preset.Properties[Key(section, property.Label)] = new JValue(value);
            }
        }

        _presets.Add(preset);
        Changed?.Invoke();
        return preset;
    }

    public void DeletePreset(string id)
    {
        if (GetPreset(id) is not { } preset)
        {
            return;
        }

        _presets.Remove(preset);
        if (_defaults.GetValueOrDefault(preset.ElementType) == id)
        {
            _defaults.Remove(preset.ElementType);
        }

        Changed?.Invoke();
    }

    /// <summary>Toggles the preset as the default of its element type.</summary>
    public void SetDefault(string id)
    {
        if (GetPreset(id) is not { } preset)
        {
            return;
        }

        if (_defaults.GetValueOrDefault(preset.ElementType) == id)
        {
            _defaults.Remove(preset.ElementType);
        }
        else
        {
            _defaults[preset.ElementType] = id;
        }

        Changed?.Invoke();
    }

    public bool ApplyDefault(WorldElement element) =>
        element is not null && _defaults.TryGetValue(element.GetType().Name, out var id) && ApplyPreset(id, element);

    public bool ApplyPreset(string id, WorldElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (GetPreset(id) is not { } preset || preset.ElementType != element.GetType().Name)
        {
            return false;
        }

        // Inspectors show some fields only after a discriminator is set (box thickness once Style is Box), so re-read
        // the definition a few times: setting a discriminator reveals its dependent fields for the next pass.
        var pending = new Dictionary<string, JValue>(preset.Properties, StringComparer.Ordinal);
        for (var pass = 0; pass < 4 && pending.Count > 0; pass++)
        {
            foreach (var (section, property) in TerrainClipboard.ValueProperties(element))
            {
                var key = Key(section, property.Label);
                if (pending.Remove(key, out var value) && ToClr(value) is { } clr)
                {
                    TerrainClipboard.SetValue(property, clr);
                }
            }
        }

        foreach (var (group, degrees) in preset.TextureRotations)
        {
            element.SetTextureRotation(group, degrees);
        }

        foreach (var (group, transform) in preset.UvTransforms.Where(t => t.Value is not null))
        {
            element.SetUVTransform(group, transform.Clone());
        }

        foreach (var (group, path) in preset.Textures)
        {
            element.SetGroupTextureReference(group, path);
        }

        element.Update();
        element.OnPropertiesChanged?.Invoke();
        return true;
    }

    public string ToJson() => JsonConvert.SerializeObject(new { version = 1, presets = _presets, defaults = _defaults }, JsonSettings);

    /// <summary>Loads stored presets; anything that is not a version 1 store leaves the presets empty and returns false.</summary>
    public bool Load(string? json)
    {
        _presets = [];
        _defaults = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        Store? store;
        try
        {
            store = JsonConvert.DeserializeObject<Store>(json, JsonSettings);
        }
        catch (JsonException)
        {
            // Corrupt preferences: start empty, as TerrainHeaven does; the caller reports the false result.
            return false;
        }

        if (store is not { Version: 1, Presets: { } presets })
        {
            return false;
        }

        _presets = presets.Where(p => p is not null && !string.IsNullOrEmpty(p.Id)).ToList();
        foreach (var preset in _presets)
        {
            preset.Properties ??= [];
            preset.Textures ??= [];
            preset.TextureRotations ??= [];
            preset.UvTransforms ??= [];
        }

        _defaults = new Dictionary<string, string>(store.Defaults ?? [], StringComparer.Ordinal);
        return true;
    }

    private static string Key(string section, string label) => $"{section}\0{label}";

    private static object? ToClr(JValue value) => value.Type switch
    {
        JTokenType.Integer or JTokenType.Float => value.ToObject<double>(),
        JTokenType.Boolean => value.ToObject<bool>(),
        JTokenType.String => value.ToObject<string>(),
        _ => null,
    };


    private sealed class Store
    {
        public int Version { get; set; }

        public List<ElementPreset>? Presets { get; set; }

        public Dictionary<string, string>? Defaults { get; set; }
    }
}
