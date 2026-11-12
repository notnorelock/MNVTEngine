using System.Text;
using MNVTEngine.Graphics;
using MNVTEngine.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Assets;

/// <summary>Input and output of one import.</summary>
public sealed class AssetImportContext(string relativePath, byte[] data, JObject settings)
{
    public string RelativePath { get; } = relativePath;

    /// <summary>The source file on disk (for formats that reference other files), or null when importing from memory.</summary>
    public string? SourceFullPath { get; init; }

    /// <summary>Full path of the project's <c>Assets/</c> folder, or null when importing from memory.</summary>
    public string? AssetsDirectory { get; init; }

    /// <summary>The GUID of another asset by project-relative path (its meta is read or created on demand); null if there is none.</summary>
    public Func<string, Guid?> ResolveAsset { get; init; } = _ => null;

    /// <summary>The file of another asset by GUID, to read its content (add it to <see cref="ImportInputs"/>); null if unknown.</summary>
    public Func<Guid, string?> AssetFile { get; init; } = _ => null;

    /// <summary>
    /// Derived data to keep (e.g. a processed model), by name. Written to the library only when the import succeeds,
    /// so a failed reimport keeps the last good artifacts.
    /// </summary>
    public Dictionary<string, byte[]> Artifacts { get; } = new(StringComparer.Ordinal);

    public byte[] Data { get; } = data;

    /// <summary>Normalized settings (defaults filled in).</summary>
    public JObject Settings { get; } = settings;

    public Dictionary<string, string> Properties { get; } = [];

    public HashSet<Guid> Dependencies { get; } = [];

    /// <summary>
    /// Assets whose content this import read (a glTF's .bin buffers): when one changes the asset is imported again
    /// (importers with <see cref="IAssetImporter.ReimportWhenDependenciesChange"/>). Also listed in <see cref="Dependencies"/>.
    /// </summary>
    public HashSet<Guid> ImportInputs { get; } = [];

    public List<string> Errors { get; } = [];

    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Validates a source file, extracts facts (dimensions, dependencies) and owns its import settings.
/// Bump <see cref="Version"/> whenever import results change, so the database reimports cached assets.
/// </summary>
public interface IAssetImporter
{
    string Name { get; }

    int Version { get; }

    string AssetType { get; }

    /// <summary>Lower-case extensions including the dot.</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>Returns complete settings: known values kept, missing ones defaulted, invalid ones reported and reset.</summary>
    JObject NormalizeSettings(JObject settings, ICollection<string> problems);

    void Import(AssetImportContext context);

    /// <summary>
    /// Reimport when one of the import's inputs (<see cref="AssetImportContext.ImportInputs"/>) is added or changes, and,
    /// when files are added, when the last import reported problems (a missing texture may exist now).
    /// </summary>
    bool ReimportWhenDependenciesChange => false;

    /// <summary>
    /// Slow to import (models): <see cref="AssetDatabase.Refresh(bool)"/> with background imports runs it on a worker
    /// thread. <see cref="Import"/> must then not touch shared state; the context's lookups are thread-safe.
    /// </summary>
    bool ImportsInBackground => false;
}

public static class AssetImporters
{
    /// <summary>
    /// The importers of the asset types every project may have. Modules' importers (scripts, terrain, models) are added by
    /// the build tooling and the editor (<c>MNVTEngine.Build.EngineImporters</c>): games never import.
    /// </summary>
    public static IReadOnlyList<IAssetImporter> CreateDefault() =>
    [
        new TextureImporter(),
        new SceneImporter(AssetTypes.Scene, ".mnvt"),
        new SceneImporter(AssetTypes.Prefab, ".mnvtprefab"),
        new UILayoutImporter(),
        new ModelPlaceholderImporter(),
        new JsonImporter(),
        new AnimationImporter(),
        new AnimatorControllerImporter(),
        new MaterialImporter(),
        new TextImporter("Shader", AssetTypes.Shader, ".glsl", ".vert", ".frag"),
        new TextImporter("Text", AssetTypes.Text, ".txt", ".md"),
        new TextImporter("Web", AssetTypes.Web, ".html", ".htm", ".css", ".js", ".mjs"),
        new MagicImporter("Audio", AssetTypes.Audio, new Dictionary<string, Func<byte[], string?>>
        {
            [".wav"] = d => d.Length >= 12 && Ascii(d, 0, 4) == "RIFF" && Ascii(d, 8, 4) == "WAVE" ? "wav" : null,
            [".ogg"] = d => d.Length >= 4 && Ascii(d, 0, 4) == "OggS" ? "ogg" : null,
        }),
        new MagicImporter("Font", AssetTypes.Font, new Dictionary<string, Func<byte[], string?>>
        {
            [".ttf"] = FontFormat,
            [".otf"] = FontFormat,
        }),
    ];

    /// <summary>Collects every <c>"asset:&lt;guid&gt;"</c> string (with or without a <c>#part</c>) in a JSON document.</summary>
    public static void CollectReferences(JToken token, ISet<Guid> into)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(into);
        var tokens = token is JContainer container ? container.DescendantsAndSelf() : [token];
        foreach (var value in tokens.OfType<JValue>())
        {
            if (value.Type == JTokenType.String && ((string)value.Value!).StartsWith("asset:", StringComparison.Ordinal)
                && Guid.TryParse(((string)value.Value!).AsSpan(6).Length >= 36 ? ((string)value.Value!).AsSpan(6, 36) : [], out var guid))
            {
                into.Add(guid);
            }
        }
    }

    internal static JToken ParseJson(byte[] data) =>
        JToken.Parse(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data), new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });

    private static string Ascii(byte[] data, int offset, int count) => Encoding.ASCII.GetString(data, offset, count);

    private static string? FontFormat(byte[] d) => d.Length < 4 ? null : Ascii(d, 0, 4) switch
    {
        "OTTO" => "opentype-cff",
        "true" or "\0\u0001\0\0" => "truetype",
        "ttcf" => "truetype-collection",
        _ => null,
    };
}

/// <summary>Base for importers without settings.</summary>
public abstract class SimpleImporter(string name, string assetType, params string[] extensions) : IAssetImporter
{
    public string Name { get; } = name;

    public virtual int Version => 1;

    public string AssetType { get; } = assetType;

    public IReadOnlyList<string> Extensions { get; } = extensions;

    public JObject NormalizeSettings(JObject settings, ICollection<string> problems)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach (var property in settings.Properties())
        {
            problems.Add($"Unknown import setting '{property.Name}' removed.");
        }

        return [];
    }

    public abstract void Import(AssetImportContext context);
}

public sealed class TextureImporter : IAssetImporter
{
    public string Name => "Texture";

    /// <summary>2: SVG documents (rasterized with <see cref="Graphics.Svg.SvgRasterizer"/>).</summary>
    public int Version => 2;

    public string AssetType => AssetTypes.Texture;

    public IReadOnlyList<string> Extensions { get; } = [".png", ".jpg", ".jpeg", ".bmp", ".tga", ".svg"];

    public static TextureImportSettings Read(JObject settings) =>
        settings.ToObject<TextureImportSettings>(MnvtJson.CreateSerializer()) ?? new TextureImportSettings();

    private static readonly HashSet<string> KnownSettings =
        [.. typeof(TextureImportSettings).GetProperties().Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..])];

    public JObject NormalizeSettings(JObject settings, ICollection<string> problems)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(problems);
        var known = new JObject();
        foreach (var property in settings.Properties())
        {
            if (KnownSettings.Contains(property.Name))
            {
                known.Add(property.Name, property.Value.DeepClone());
            }
            else
            {
                problems.Add($"Unknown import setting '{property.Name}' removed.");
            }
        }

        TextureImportSettings parsed;
        try
        {
            parsed = Read(known);
        }
        catch (JsonException ex)
        {
            problems.Add($"Invalid texture settings reset to defaults: {ex.Message}");
            parsed = new TextureImportSettings();
        }

        if (!(parsed.PixelsPerUnit > 0f) || !float.IsFinite(parsed.PixelsPerUnit))
        {
            problems.Add($"pixelsPerUnit must be positive; reset to 100.");
            parsed.PixelsPerUnit = 100f;
        }

        if (!(parsed.SvgScale > 0f && parsed.SvgScale <= 64f))
        {
            problems.Add("svgScale must be above 0 and at most 64; reset to 1.");
            parsed.SvgScale = 1f;
        }

        return JObject.FromObject(parsed, MnvtJson.CreateSerializer());
    }

    public void Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            Image image;
            if (Graphics.Svg.SvgRasterizer.IsSvg(context.Data))
            {
                var svg = Graphics.Svg.SvgRasterizer.Render(context.Data, context.RelativePath, Read(context.Settings).SvgScale);
                context.Warnings.AddRange(svg.Warnings);
                context.Properties["format"] = "SVG (rasterized at load)";
                image = svg.Image;
            }
            else
            {
                image = Image.Decode(context.Data, context.RelativePath);
            }

            context.Properties["width"] = image.Width.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Properties["height"] = image.Height.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (InvalidDataException ex)
        {
            context.Errors.Add(ex.Message);
        }
    }
}

/// <summary>Scenes and prefabs: must be readable JSON with a <c>gameObjects</c> array; records referenced assets.</summary>
public sealed class SceneImporter(string assetType, string extension) : SimpleImporter(assetType, assetType, extension)
{
    public override void Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            if (AssetImporters.ParseJson(context.Data) is not JObject { } root || root["gameObjects"] is not JArray objects)
            {
                context.Errors.Add("Not a scene document: expected an object with a 'gameObjects' array.");
                return;
            }

            context.Properties["objects"] = objects.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (AssetType == AssetTypes.Prefab && objects.Count(o => o["parent"] is null or { Type: JTokenType.Null }) != 1)
            {
                context.Errors.Add("A prefab must contain exactly one root object.");
            }

            AssetImporters.CollectReferences(root, context.Dependencies);
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        {
            context.Errors.Add($"Invalid JSON: {ex.Message}");
        }
    }
}

/// <summary>Sprite animation clips: validated with the same parser the runtime uses; the sheet texture is a dependency.</summary>
public sealed class AnimationImporter() : SimpleImporter("Animation", AssetTypes.Animation, SpriteAnimationClip.Extension)
{
    public override void Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var clip = SpriteAnimationClip.Parse(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(context.Data), context.RelativePath);
            context.Dependencies.Add(clip.Texture);
            context.Properties["frames"] = clip.Frames.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Properties["duration"] = clip.Duration.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " s";
        }
        catch (SerializationException ex)
        {
            context.Errors.AddRange(ex.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{d.Path}: {d.Message}").DefaultIfEmpty(ex.Message));
        }
        catch (DecoderFallbackException ex)
        {
            context.Errors.Add($"Not valid UTF-8 text: {ex.Message}");
        }
    }
}

/// <summary>Animator controllers: validated with the runtime's parser (clip names are checked when they play, against the model).</summary>
public sealed class AnimatorControllerImporter() : SimpleImporter("AnimatorController", AssetTypes.AnimatorController, AnimatorControllerDefinition.Extension)
{
    public override void Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var controller = AnimatorControllerDefinition.Parse(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(context.Data), context.RelativePath);
            context.Properties["states"] = controller.States.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Properties["parameters"] = controller.Parameters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Properties["transitions"] = controller.Transitions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (SerializationException ex)
        {
            context.Errors.AddRange(ex.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{d.Path}: {d.Message}").DefaultIfEmpty(ex.Message));
        }
        catch (DecoderFallbackException ex)
        {
            context.Errors.Add($"Not valid UTF-8 text: {ex.Message}");
        }
    }
}

/// <summary>Materials: validated with the runtime's parser; shaders and textures are dependencies. GLSL compiles on first use (it needs a GPU).</summary>
public sealed class MaterialImporter() : SimpleImporter("Material", AssetTypes.Material, MaterialDefinition.Extension)
{
    public override void Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var material = MaterialDefinition.Parse(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(context.Data), context.RelativePath);
            foreach (var dependency in material.Dependencies)
            {
                context.Dependencies.Add(dependency);
            }

            context.Properties["blend"] = material.Blend;
            context.Properties["shader"] = material.Builtin ?? "custom";
        }
        catch (SerializationException ex)
        {
            context.Errors.AddRange(ex.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{d.Path}: {d.Message}").DefaultIfEmpty(ex.Message));
        }
        catch (DecoderFallbackException ex)
        {
            context.Errors.Add($"Not valid UTF-8 text: {ex.Message}");
        }
    }
}

/// <summary>
/// Gives FBX/glTF/GLB files the Model type where the model importer (MNVTEngine.Assets.Models, with Assimp) is not
/// loaded: the runtime's read-only database and exported games. It processes nothing; the model is read from the
/// artifact the real importer wrote (editor, Player and build), and a missing artifact is reported when the model loads.
/// </summary>
public sealed class ModelPlaceholderImporter : IAssetImporter
{
    public string Name => "ModelPlaceholder";

    public int Version => 1;

    public string AssetType => AssetTypes.Model;

    public IReadOnlyList<string> Extensions { get; } = [".fbx", ".gltf", ".glb"];

    /// <summary>Keeps the settings as they are: they belong to the model importer, which validates them.</summary>
    public JObject NormalizeSettings(JObject settings, ICollection<string> problems)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return (JObject)settings.DeepClone();
    }

    public void Import(AssetImportContext context) => ArgumentNullException.ThrowIfNull(context);
}

/// <summary>UI layouts: JSON with a schema version and a root element; references (fonts, images) become dependencies.</summary>
public sealed class UILayoutImporter() : SimpleImporter("UILayout", AssetTypes.UILayout, ".mnvtui")
{
    public override void Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var root = AssetImporters.ParseJson(context.Data);
            if (root is not JObject document || document.Value<int?>("schemaVersion") is null || document["root"] is not JObject)
            {
                context.Errors.Add("A UI layout needs \"schemaVersion\" and a \"root\" element.");
                return;
            }

            AssetImporters.CollectReferences(root, context.Dependencies);
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        {
            context.Errors.Add($"Invalid JSON: {ex.Message}");
        }
    }
}

public sealed class JsonImporter() : SimpleImporter("Json", AssetTypes.Json, ".json")
{
    public override void Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            AssetImporters.CollectReferences(AssetImporters.ParseJson(context.Data), context.Dependencies);
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        {
            context.Errors.Add($"Invalid JSON: {ex.Message}");
        }
    }
}

/// <summary>Text assets (shaders, gameplay scripts): must be valid UTF-8. Compilation of scripts arrives with the script compiler.</summary>
public sealed class TextImporter(string name, string assetType, params string[] extensions) : SimpleImporter(name, assetType, extensions)
{
    public override void Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(context.Data);
            context.Properties["lines"] = (text.Count(c => c == '\n') + (text.Length > 0 ? 1 : 0)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (DecoderFallbackException ex)
        {
            context.Errors.Add($"Not valid UTF-8 text: {ex.Message}");
        }
    }
}

/// <summary>Binary formats validated by their file signature (decoding arrives with the subsystem that plays or renders them).</summary>
public sealed class MagicImporter(string name, string assetType, IReadOnlyDictionary<string, Func<byte[], string?>> formats)
    : SimpleImporter(name, assetType, [.. formats.Keys])
{
    public override void Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var extension = Path.GetExtension(context.RelativePath).ToLowerInvariant();
        if (formats[extension](context.Data) is { } format)
        {
            context.Properties["format"] = format;
        }
        else
        {
            context.Errors.Add($"File content is not a valid {extension} file.");
        }
    }
}
