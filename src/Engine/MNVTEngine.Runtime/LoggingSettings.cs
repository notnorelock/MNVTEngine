using Microsoft.Extensions.Logging;
using MNVTEngine.Core.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace MNVTEngine.Runtime;

/// <summary>
/// Log levels per logger name, stored as JSON: <c>Settings/logging.json</c> in a project, or <c>logging.json</c> next to
/// an exported game's executable.
/// <code>
/// { "default": "Information", "callSites": true,
///   "levels": { "MNVTEngine.Physics2D": "Debug", "Game.Player": "Trace", "Scripts": "Warning" } }
/// </code>
/// </summary>
public sealed class LoggingSettings
{
    public const string ProjectFile = "Settings/logging.json";

    [JsonConverter(typeof(StringEnumConverter))]
    public LogLevel Default { get; set; } = LogLevel.Information;

    /// <summary>Record namespace/class/method for ILogger messages (a stack walk per message).</summary>
    public bool CallSites { get; set; } = true;

    [JsonProperty(ItemConverterType = typeof(StringEnumConverter), ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<string, LogLevel> Levels { get; set; } = [];

    public static string PathFor(GameProject project) => Path.Combine((project ?? throw new ArgumentNullException(nameof(project))).RootDirectory, ProjectFile);

    /// <summary>Reads the file; a missing file gives the defaults.</summary>
    /// <exception cref="SerializationException">The file is not valid logging settings.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static LoggingSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new LoggingSettings();
        }

        try
        {
            return JsonConvert.DeserializeObject<LoggingSettings>(File.ReadAllText(path)) ?? new LoggingSettings();
        }
        catch (JsonException ex)
        {
            throw new Serialization.SerializationException(path, $"not valid logging settings: {ex.Message}", [], ex);
        }
    }

    public static LoggingSettings From(LogHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return new LoggingSettings
        {
            Default = hub.Levels.Default,
            CallSites = hub.CaptureCallSites,
            Levels = new Dictionary<string, LogLevel>(hub.Levels.Rules, StringComparer.OrdinalIgnoreCase),
        };
    }

    public void ApplyTo(LogHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        hub.CaptureCallSites = CallSites;
        hub.Levels.Default = Default;
        hub.Levels.Clear();
        foreach (var (name, level) in Levels)
        {
            hub.Levels.Set(name, level);
        }
    }

    /// <exception cref="IOException">The file cannot be written.</exception>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented, new JsonSerializerSettings
        {
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver { NamingStrategy = new Newtonsoft.Json.Serialization.CamelCaseNamingStrategy { ProcessDictionaryKeys = false } },
        }));
    }
}
