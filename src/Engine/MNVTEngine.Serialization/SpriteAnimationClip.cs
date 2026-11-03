using System.Globalization;
using MNVTEngine.Mathematics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Serialization;

/// <summary>
/// A sprite animation (<c>.mnvtanim</c>): frames cut from a sprite sheet laid out as a grid, played at a fixed rate.
/// Shared by the asset importer (validation, dependencies) and the runtime animator.
/// <code>
/// { "schemaVersion": 1, "texture": "asset:&lt;guid&gt;", "frameWidth": 16, "frameHeight": 16, "columns": 4,
///   "frames": [0, 1, 2, 3], "frameRate": 10, "loop": true }
/// </code>
/// Frame <c>n</c> of the sheet is at column <c>n % columns</c>, row <c>n / columns</c>, counted from the image's top-left.
/// </summary>
public sealed class SpriteAnimationClip
{
    public const string Extension = ".mnvtanim";
    public const int CurrentSchemaVersion = 1;
    private const string AssetPrefix = "asset:";

    private SpriteAnimationClip(Guid texture, int frameWidth, int frameHeight, int columns, int[] frames, float frameRate, bool loop)
    {
        Texture = texture;
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
        Columns = columns;
        Frames = frames;
        FrameRate = frameRate;
        Loop = loop;
    }

    /// <summary>GUID of the sprite-sheet texture.</summary>
    public Guid Texture { get; }

    public int FrameWidth { get; }

    public int FrameHeight { get; }

    public int Columns { get; }

    /// <summary>Sheet indices in playback order (may repeat).</summary>
    public IReadOnlyList<int> Frames { get; }

    /// <summary>Frames per second.</summary>
    public float FrameRate { get; }

    public bool Loop { get; }

    public float Duration => Frames.Count / FrameRate;

    /// <summary>Texel rectangle of the <paramref name="index"/>-th frame of the clip.</summary>
    public Rect FrameRect(int index)
    {
        var sheetIndex = Frames[index];
        return new Rect(sheetIndex % Columns * FrameWidth, sheetIndex / Columns * FrameHeight, FrameWidth, FrameHeight);
    }

    /// <summary>Clip frame shown <paramref name="time"/> seconds after the start (clamped to the last frame when not looping).</summary>
    public int FrameAt(float time)
    {
        var index = (int)MathF.Floor(Math.Max(0f, time) * FrameRate);
        return Loop ? index % Frames.Count : Math.Min(index, Frames.Count - 1);
    }

    /// <exception cref="SerializationException">Malformed or invalid clip; <see cref="SerializationException.Diagnostics"/> lists every problem.</exception>
    public static SpriteAnimationClip Parse(string json, string sourceName)
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

        var texture = Guid.Empty;
        if (root.Value<string>("texture") is not { } reference
            || !reference.StartsWith(AssetPrefix, StringComparison.Ordinal)
            || !Guid.TryParse(reference.AsSpan(AssetPrefix.Length), out texture))
        {
            Error("texture", $"Expected a texture reference \"{AssetPrefix}<guid>\".");
        }

        int Positive(string name, int fallback)
        {
            var value = root[name] is { Type: JTokenType.Integer } token ? token.Value<int>() : fallback;
            if (value <= 0)
            {
                Error(name, $"'{name}' must be a positive whole number.");
            }

            return value;
        }

        var width = Positive("frameWidth", 0);
        var height = Positive("frameHeight", 0);
        var columns = Positive("columns", 1);
        var rate = root["frameRate"] is { Type: JTokenType.Integer or JTokenType.Float } r ? r.Value<float>() : 10f;
        if (!(rate > 0f && rate <= 240f))
        {
            Error("frameRate", $"'frameRate' must be in (0, 240], got {rate.ToString(CultureInfo.InvariantCulture)}.");
        }

        var frames = new List<int>();
        if (root["frames"] is JArray list && list.Count > 0)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] is { Type: JTokenType.Integer } frame && frame.Value<int>() >= 0)
                {
                    frames.Add(frame.Value<int>());
                }
                else
                {
                    Error($"frames[{i}]", "Frames are sheet indices: whole numbers >= 0.");
                }
            }
        }
        else
        {
            Error("frames", "'frames' must be a non-empty array of sheet indices.");
        }

        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            throw new SerializationException(sourceName, string.Join(" ", diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Message)), diagnostics);
        }

        return new SpriteAnimationClip(texture, width, height, columns, [.. frames], rate, root.Value<bool?>("loop") ?? true);
    }
}
