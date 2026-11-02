using System.Globalization;
using MNVTEngine.Serialization.Converters;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace MNVTEngine.Serialization;

/// <summary>
/// Canonical Json.NET configuration for every engine file (projects, scenes, assets, missions).
/// Polymorphic type names are never read from data: <see cref="TypeNameHandling.None"/> plus
/// <see cref="MetadataPropertyHandling.Ignore"/> means a "$type" property is treated as an
/// unknown member and rejected. Polymorphism goes through explicit registries instead.
/// </summary>
public static class MnvtJson
{
    public static JsonSerializerSettings CreateSettings()
    {
        var settings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Error,
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Double,
            Culture = CultureInfo.InvariantCulture,
            Formatting = Formatting.Indented,
            MaxDepth = 64,
            ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() },
        };

        foreach (var converter in CreateConverters())
        {
            settings.Converters.Add(converter);
        }

        return settings;
    }

    public static JsonSerializer CreateSerializer() => JsonSerializer.Create(CreateSettings());

    public static IReadOnlyList<JsonConverter> CreateConverters() =>
    [
        new Vector2Converter(),
        new Vector3Converter(),
        new Vector4Converter(),
        new QuaternionConverter(),
        new ColorConverter(),
        new RectConverter(),
        new BoundsConverter(),
        new Matrix4x4Converter(),
        new StringEnumConverter(new CamelCaseNamingStrategy(), allowIntegerValues: false),
    ];

    public static string Serialize<T>(T value) => JsonConvert.SerializeObject(value, CreateSettings());

    /// <exception cref="JsonException">The text is malformed or does not match <typeparamref name="T"/>.</exception>
    public static T Deserialize<T>(string json) =>
        JsonConvert.DeserializeObject<T>(json, CreateSettings())
        ?? throw new JsonSerializationException($"JSON 'null' is not a valid {typeof(T).Name}.");
}
