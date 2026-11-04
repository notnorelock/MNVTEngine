using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Serialization;

/// <summary>A material property value: 1, 2 or 4 numbers (uniforms; colors are 4), or a texture asset.</summary>
public readonly record struct MaterialValue
{
    private MaterialValue(float[]? numbers, Guid? texture) => (Numbers, Texture) = (numbers, texture);

    public float[]? Numbers { get; }

    public Guid? Texture { get; }

    public bool IsTexture => Texture is not null;

    public static MaterialValue Number(float value) => new([value], null);

    /// <exception cref="ArgumentException">Not 1, 2 or 4 components.</exception>
    public static MaterialValue Vector(params float[] components)
    {
        ArgumentNullException.ThrowIfNull(components);
        return components.Length is 1 or 2 or 4 ? new([.. components], null) : throw new ArgumentException("A material vector has 1, 2 or 4 components.", nameof(components));
    }

    public static MaterialValue TextureAsset(Guid asset) => new(null, asset);

    public bool Equals(MaterialValue other) => Texture == other.Texture && (Numbers ?? []).AsSpan().SequenceEqual(other.Numbers ?? []);

    public override int GetHashCode() => HashCode.Combine(Texture, Numbers?.Length);
}

/// <summary>
/// A material asset (<c>.mnvtmat</c>, schema 2):
/// <code>
/// { "schemaVersion": 2, "shader": "builtin:standard", "blend": "Opaque", "cull": "Back", "renderQueue": 2000,
///   "properties": { "_BaseColor": [1, 0.8, 0.6, 1], "_Metallic": 0.2, "_BaseColorMap": "asset:&lt;guid&gt;" } }
/// </code>
/// <list type="bullet">
/// <item><b>Shader.</b> <c>builtin:standard</c> (PBR metallic-roughness), <c>builtin:unlit</c>, <c>builtin:sprite</c>
/// (the default), or <c>"asset:&lt;guid&gt;"</c> of a fragment shader. A custom fragment shader runs with the sprite
/// vertex stage, or with <c>"vertexShader": "asset:&lt;guid&gt;"</c> (needed on meshes).</item>
/// <item><b>Render settings.</b> <c>blend</c>, <c>cull</c> (Back, Front, None), <c>depthTest</c>, <c>depthWrite</c> and
/// <c>renderQueue</c> (2000 opaque, 2450 cutout, 3000+ transparent) are optional; absent ones follow the shader and
/// the blend mode.</item>
/// <item><b>Properties.</b> Uniforms (a number, or an array of 2 or 4) and textures (<c>"asset:&lt;guid&gt;"</c>) by their
/// GLSL names; the renderer's own names are rejected.</item>
/// </list>
/// Schema 1 named properties "uniforms" and had no textures or render settings; it is migrated on load.
/// The definition is mutable so editors can change it and write it back with <see cref="ToJson"/>.
/// </summary>
public sealed partial class MaterialDefinition
{
    public const string Extension = ".mnvtmat";
    public const string BuiltinSprite = "builtin:sprite";
    public const string BuiltinStandard = "builtin:standard";
    public const string BuiltinUnlit = "builtin:unlit";
    public const int CurrentSchemaVersion = 2;

    public static readonly IReadOnlyList<string> BuiltinShaders = [BuiltinStandard, BuiltinUnlit, BuiltinSprite];

    public static readonly IReadOnlyList<string> BlendModes = ["Opaque", "Alpha", "Premultiplied", "Additive", "Multiply"];

    public static readonly IReadOnlyList<string> CullModes = ["Back", "Front", "None"];

    /// <summary>Supplied by the engine; materials may not set them.</summary>
    public static readonly IReadOnlyList<string> ReservedUniforms = ["uViewProjection", "uTexture", "uTime", "uModel", "uNormalMatrix"];

    /// <summary>A built-in shader id (<see cref="BuiltinShaders"/>), or null when <see cref="Shader"/> is a custom one.</summary>
    public string? Builtin { get; set; } = BuiltinSprite;

    /// <summary>GUID of the custom fragment shader asset; null for a built-in shader.</summary>
    public Guid? Shader { get; set; }

    /// <summary>GUID of a custom vertex shader asset; null uses the sprite vertex stage with a custom fragment shader.</summary>
    public Guid? VertexShader { get; set; }

    /// <summary>One of <see cref="BlendModes"/>.</summary>
    public string Blend { get; set; } = "Alpha";

    /// <summary>One of <see cref="CullModes"/>, or null for the default (Back for 3D shaders).</summary>
    public string? Cull { get; set; }

    public bool? DepthTest { get; set; }

    public bool? DepthWrite { get; set; }

    public int? RenderQueue { get; set; }

    public SortedDictionary<string, MaterialValue> Properties { get; } = new(StringComparer.Ordinal);

    /// <summary>The numeric properties (uniforms).</summary>
    public IReadOnlyDictionary<string, float[]> Uniforms =>
        Properties.Where(p => p.Value.Numbers is not null).ToDictionary(p => p.Key, p => p.Value.Numbers!, StringComparer.Ordinal);

    /// <summary>Texture and shader assets the material references.</summary>
    public IEnumerable<Guid> Dependencies =>
        new[] { Shader, VertexShader }.OfType<Guid>().Concat(Properties.Values.Select(v => v.Texture).OfType<Guid>()).Distinct();

    public static bool IsValidPropertyName(string name) => PropertyName().IsMatch(name) && !ReservedUniforms.Contains(name);

    /// <exception cref="SerializationException">Malformed or invalid; <see cref="SerializationException.Diagnostics"/> lists every problem.</exception>
    public static MaterialDefinition Parse(string json, string sourceName)
    {
        var diagnostics = new List<SerializationDiagnostic>();
        void Error(string path, string message) => diagnostics.Add(new SerializationDiagnostic(DiagnosticSeverity.Error, path, message));

        JObject root;
        try
        {
            root = JObject.Parse(json);
            new JsonMigrationPipeline(CurrentSchemaVersion).Add(1, document =>
            {
                if (document["uniforms"] is { } uniforms)
                {
                    document.Remove("uniforms");
                    document["properties"] = uniforms;
                }
            }).Migrate(root, diagnostics);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new SerializationException(sourceName, ex.Message, diagnostics, ex);
        }

        var definition = new MaterialDefinition();
        var shaderText = root.Value<string>("shader") ?? BuiltinSprite;
        if (BuiltinShaders.Contains(shaderText))
        {
            definition.Builtin = shaderText;
        }
        else if (AssetGuid(shaderText) is { } shader)
        {
            (definition.Builtin, definition.Shader) = (null, shader);
        }
        else
        {
            Error("shader", $"Expected one of {string.Join(", ", BuiltinShaders)} or a shader asset reference \"asset:<guid>\".");
        }

        if (root["vertexShader"] is { Type: not JTokenType.Null } vertex)
        {
            if (AssetGuid(vertex.Value<string>()) is { } guid && definition.Shader is not null)
            {
                definition.VertexShader = guid;
            }
            else
            {
                Error("vertexShader", "A vertex shader is an asset reference \"asset:<guid>\", used with a custom (asset) fragment shader.");
            }
        }

        definition.Blend = root.Value<string>("blend") ?? (definition.Builtin is BuiltinStandard or BuiltinUnlit ? "Opaque" : "Alpha");
        if (!BlendModes.Contains(definition.Blend))
        {
            Error("blend", $"Unknown blend mode '{definition.Blend}'; use one of {string.Join(", ", BlendModes)}.");
        }

        definition.Cull = root.Value<string>("cull");
        if (definition.Cull is not null && !CullModes.Contains(definition.Cull))
        {
            Error("cull", $"Unknown cull mode '{definition.Cull}'; use one of {string.Join(", ", CullModes)}.");
        }

        definition.DepthTest = ReadBool(root, "depthTest", Error);
        definition.DepthWrite = ReadBool(root, "depthWrite", Error);
        if (root["renderQueue"] is { Type: not JTokenType.Null } queue)
        {
            if (queue.Type == JTokenType.Integer && queue.Value<int>() is >= 0 and <= 5000)
            {
                definition.RenderQueue = queue.Value<int>();
            }
            else
            {
                Error("renderQueue", "The render queue is a whole number from 0 to 5000.");
            }
        }

        if (root["properties"] is JObject values)
        {
            foreach (var property in values.Properties())
            {
                ReadProperty(definition, property, Error);
            }
        }
        else if (root["properties"] is { Type: not JTokenType.Null })
        {
            Error("properties", "'properties' must be an object.");
        }

        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            throw new SerializationException(sourceName, string.Join(" ", diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Message)), diagnostics);
        }

        return definition;
    }

    /// <summary>The definition as schema 2 JSON (stable order, properties sorted by name).</summary>
    public string ToJson()
    {
        var root = new JObject
        {
            ["schemaVersion"] = CurrentSchemaVersion,
            ["shader"] = Builtin ?? Reference(Shader!.Value),
        };
        if (VertexShader is { } vertex)
        {
            root["vertexShader"] = Reference(vertex);
        }

        root["blend"] = Blend;
        if (Cull is not null)
        {
            root["cull"] = Cull;
        }

        if (DepthTest is { } test)
        {
            root["depthTest"] = test;
        }

        if (DepthWrite is { } write)
        {
            root["depthWrite"] = write;
        }

        if (RenderQueue is { } queue)
        {
            root["renderQueue"] = queue;
        }

        root["properties"] = new JObject(Properties.Select(p => new JProperty(p.Key, p.Value.Texture is { } texture
            ? Reference(texture)
            : p.Value.Numbers!.Length == 1 ? new JValue(p.Value.Numbers[0]) : new JArray(p.Value.Numbers.Cast<object>()))));
        return root.ToString(Formatting.Indented);
    }

    private static void ReadProperty(MaterialDefinition definition, JProperty property, Action<string, string> error)
    {
        var path = $"properties.{property.Name}";
        if (!PropertyName().IsMatch(property.Name))
        {
            error(path, "Property names are GLSL identifiers.");
        }
        else if (ReservedUniforms.Contains(property.Name))
        {
            error(path, $"'{property.Name}' is supplied by the engine.");
        }
        else if (property.Value.Type is JTokenType.Integer or JTokenType.Float)
        {
            definition.Properties[property.Name] = MaterialValue.Number(property.Value.Value<float>());
        }
        else if (property.Value is JArray array && array.Count is 1 or 2 or 4 && array.All(v => v.Type is JTokenType.Integer or JTokenType.Float))
        {
            definition.Properties[property.Name] = MaterialValue.Vector([.. array.Select(v => v.Value<float>())]);
        }
        else if (property.Value.Type == JTokenType.String && AssetGuid(property.Value.Value<string>()) is { } texture)
        {
            definition.Properties[property.Name] = MaterialValue.TextureAsset(texture);
        }
        else
        {
            error(path, "A property is a number, an array of 2 or 4 numbers, or a texture \"asset:<guid>\".");
        }
    }

    private static bool? ReadBool(JObject root, string key, Action<string, string> error)
    {
        if (root[key] is not { Type: not JTokenType.Null } token)
        {
            return null;
        }

        if (token.Type == JTokenType.Boolean)
        {
            return token.Value<bool>();
        }

        error(key, $"'{key}' is true or false.");
        return null;
    }

    private static Guid? AssetGuid(string? text) =>
        text is not null && text.StartsWith("asset:", StringComparison.Ordinal) && Guid.TryParse(text.AsSpan(6), out var guid) ? guid : null;

    private static string Reference(Guid guid) => "asset:" + guid.ToString("D", CultureInfo.InvariantCulture);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex PropertyName();
}
