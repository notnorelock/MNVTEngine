using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Serialization;

/// <summary>
/// A sprite material (<c>.mnvtmat</c>): which shader, how it blends, and uniform values.
/// <code>
/// { "schemaVersion": 1, "shader": "asset:&lt;guid of a .frag/.glsl fragment shader&gt;", "blend": "Additive",
///   "uniforms": { "uTint": [1, 0.5, 0.2, 1], "uSpeed": 2 } }
/// </code>
/// <c>"shader": "builtin:sprite"</c> (the default) is the standard sprite shader. A custom shader is a fragment stage
/// for the sprite vertex shader: it receives <c>vTexCoord</c>, <c>vColor</c>, <c>uTexture</c>, and may declare
/// <c>uniform float uTime</c> (seconds, supplied by the renderer). Uniforms are 1, 2 or 4 floats.
/// </summary>
public sealed partial class MaterialDefinition
{
    public const string Extension = ".mnvtmat";
    public const string BuiltinSprite = "builtin:sprite";
    public const int CurrentSchemaVersion = 1;

    public static readonly IReadOnlyList<string> BlendModes = ["Opaque", "Alpha", "Premultiplied", "Additive", "Multiply"];

    /// <summary>Supplied by the engine; materials may not set them.</summary>
    public static readonly IReadOnlyList<string> ReservedUniforms = ["uViewProjection", "uTexture", "uTime"];

    private MaterialDefinition(Guid? shader, string blend, IReadOnlyDictionary<string, float[]> uniforms)
    {
        Shader = shader;
        Blend = blend;
        Uniforms = uniforms;
    }

    /// <summary>GUID of the fragment shader asset; null for the built-in sprite shader.</summary>
    public Guid? Shader { get; }

    /// <summary>One of <see cref="BlendModes"/>.</summary>
    public string Blend { get; }

    public IReadOnlyDictionary<string, float[]> Uniforms { get; }

    /// <exception cref="SerializationException">Malformed or invalid; <see cref="SerializationException.Diagnostics"/> lists every problem.</exception>
    public static MaterialDefinition Parse(string json, string sourceName)
    {
        var diagnostics = new List<SerializationDiagnostic>();
        void Error(string path, string message) => diagnostics.Add(new SerializationDiagnostic(DiagnosticSeverity.Error, path, message));

        JObject root;
        try
        {
            root = JObject.Parse(json);
            new JsonMigrationPipeline(CurrentSchemaVersion).Migrate(root, diagnostics);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new SerializationException(sourceName, ex.Message, diagnostics, ex);
        }

        Guid? shader = null;
        var shaderText = root.Value<string>("shader") ?? BuiltinSprite;
        if (shaderText != BuiltinSprite)
        {
            if (shaderText.StartsWith("asset:", StringComparison.Ordinal) && Guid.TryParse(shaderText.AsSpan(6), out var guid))
            {
                shader = guid;
            }
            else
            {
                Error("shader", $"Expected \"{BuiltinSprite}\" or a shader asset reference \"asset:<guid>\".");
            }
        }

        var blend = root.Value<string>("blend") ?? "Alpha";
        if (!BlendModes.Contains(blend))
        {
            Error("blend", $"Unknown blend mode '{blend}'; use one of {string.Join(", ", BlendModes)}.");
        }

        var uniforms = new Dictionary<string, float[]>(StringComparer.Ordinal);
        if (root["uniforms"] is JObject values)
        {
            foreach (var property in values.Properties())
            {
                var path = $"uniforms.{property.Name}";
                if (!UniformName().IsMatch(property.Name))
                {
                    Error(path, "Uniform names are GLSL identifiers.");
                }
                else if (ReservedUniforms.Contains(property.Name))
                {
                    Error(path, $"'{property.Name}' is supplied by the engine.");
                }
                else if (property.Value.Type is JTokenType.Integer or JTokenType.Float)
                {
                    uniforms[property.Name] = [property.Value.Value<float>()];
                }
                else if (property.Value is JArray array && array.Count is 1 or 2 or 4 && array.All(v => v.Type is JTokenType.Integer or JTokenType.Float))
                {
                    uniforms[property.Name] = [.. array.Select(v => v.Value<float>())];
                }
                else
                {
                    Error(path, "A uniform is a number or an array of 1, 2 or 4 numbers.");
                }
            }
        }
        else if (root["uniforms"] is { Type: not JTokenType.Null })
        {
            Error("uniforms", "'uniforms' must be an object.");
        }

        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            throw new SerializationException(sourceName, string.Join(" ", diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Message)), diagnostics);
        }

        return new MaterialDefinition(shader, blend, uniforms);
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex UniformName();
}
