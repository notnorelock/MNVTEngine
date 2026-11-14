using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Versioning;

/// <summary>The machine that ran the build (no user, path or environment data).</summary>
public sealed record HostInfo(string OperatingSystem, string Architecture, string? DotnetSdk, string? MsBuild, string Runtime);

/// <summary>The CI service and its run id, when a CI ran the build (only those two values are recorded).</summary>
public sealed record CiInfo(string Provider, string? RunId);

/// <summary>What a pipeline build produces; fields that do not apply are null.</summary>
public sealed record BuildContext
{
    public string? Configuration { get; init; }

    /// <summary>Runtime identifier, like win-x64.</summary>
    public string? TargetPlatform { get; init; }

    public string? TargetFramework { get; init; }

    /// <summary>"2d" or "3d" for a game.</summary>
    public string? Profile { get; init; }

    /// <summary>"jit" or "aot".</summary>
    public string? ScriptingBackend { get; init; }

    public string? GraphicsBackend { get; init; }

    public IReadOnlyList<string>? Modules { get; init; }
}

/// <summary>
/// The version identity (MAJOR.MINOR.PATCH.BUILD with its prerelease label), the build identity (build id, timestamp,
/// source revision) and the build environment of one logical build. Every assembly and artifact of that build carries
/// the same values. See docs/architecture/versioning.md.
/// </summary>
public sealed record EngineBuildInfo
{
    public const int SchemaVersion = 1;

    public required int Major { get; init; }

    /// <summary>DDMM as a number (301 for 3 January); <see cref="MinorText"/> keeps the four digits.</summary>
    public required int Minor { get; init; }

    public required int Patch { get; init; }

    /// <summary>The build number; 0 for development builds, which do not allocate one.</summary>
    public required int Build { get; init; }

    public required string Channel { get; init; }

    public string? Prerelease { get; init; }

    public required Guid BuildId { get; init; }

    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>"counter", "ci" (MNVT_BUILD_NUMBER), "override" (MNVT_VERSION) or "development".</summary>
    public required string BuildNumberSource { get; init; }

    /// <summary>"engine" or "game:&lt;name&gt;", "packages:&lt;name&gt;", "dlc:&lt;game&gt;/&lt;name&gt;".</summary>
    public string Kind { get; init; } = "engine";

    public bool Reproducible { get; init; }

    public required SourceRevision Source { get; init; }

    public required HostInfo Host { get; init; }

    public CiInfo? Ci { get; init; }

    public BuildContext Context { get; init; } = new();

    public string MinorText => Minor.ToString("D4", CultureInfo.InvariantCulture);

    /// <summary>MAJOR.DDMM.PATCH.BUILD with the four date digits ("1.0301.140.4").</summary>
    public string NumericVersion => $"{Major}.{MinorText}.{Patch}.{Build}";

    /// <summary>The public version: <see cref="NumericVersion"/> with the prerelease label ("1.1411.140.4-dev").</summary>
    public string Version => Prerelease is null ? NumericVersion : $"{NumericVersion}-{Prerelease}";

    /// <summary>Stable within a MAJOR generation, so assemblies of one generation bind to each other ("1.0.0.0").</summary>
    public string AssemblyVersion => $"{Major}.0.0.0";

    /// <summary>The four numeric components ("1.301.140.4"; a version field has no leading zeros).</summary>
    public string FileVersion => $"{Major}.{Minor}.{Patch}.{Build}";

    /// <summary><see cref="Version"/> with source metadata: "+sha.&lt;12 hex&gt;", ".dirty" for a modified tree, or "+source.unknown".</summary>
    public string InformationalVersion => $"{Version}+{(Source.Commit is null ? "source.unknown" : "sha." + Source.ShortCommit)}{(Source.Dirty ? ".dirty" : "")}";

    /// <summary>The version for MSBuild's <c>Version</c> and NuGet: <see cref="FileVersion"/> with the label.</summary>
    public string PackageVersion => Prerelease is null ? FileVersion : $"{FileVersion}-{Prerelease}";

    public bool IsDevelopmentBuild => Build == 0;

    /// <summary>MAJOR.MINOR.PATCH, the scope of a VersionIdentity build counter.</summary>
    public string Identity => $"{Major}.{MinorText}.{Patch}";

    private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
        DateParseHandling = DateParseHandling.DateTimeOffset,
        NullValueHandling = NullValueHandling.Include,
    });

    /// <summary>The complete metadata (computed versions included), as <c>version.json</c> and <c>mnvt version --json</c> show it.</summary>
    public JObject ToJson() => new()
    {
        ["schemaVersion"] = SchemaVersion,
        ["version"] = Version,
        ["numericVersion"] = NumericVersion,
        ["major"] = Major,
        ["minor"] = Minor,
        ["minorText"] = MinorText,
        ["patch"] = Patch,
        ["build"] = Build,
        ["channel"] = Channel,
        ["prerelease"] = Prerelease,
        ["assemblyVersion"] = AssemblyVersion,
        ["fileVersion"] = FileVersion,
        ["informationalVersion"] = InformationalVersion,
        ["buildId"] = BuildId.ToString("D"),
        ["buildTimestampUtc"] = TimestampUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        ["buildNumberSource"] = BuildNumberSource,
        ["kind"] = Kind,
        ["reproducible"] = Reproducible,
        ["source"] = JObject.FromObject(Source, Serializer),
        ["host"] = JObject.FromObject(Host, Serializer),
        ["ci"] = Ci is null ? null : JObject.FromObject(Ci, Serializer),
        ["context"] = JObject.FromObject(Context, Serializer),
    };

    /// <exception cref="InvalidDataException">Not version metadata of this schema.</exception>
    public static EngineBuildInfo FromJson(JObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            if ((int?)json["schemaVersion"] != SchemaVersion)
            {
                throw new InvalidDataException($"Unknown version metadata schema {json["schemaVersion"]}.");
            }

            return new EngineBuildInfo
            {
                Major = (int)Need(json, "major"),
                Minor = (int)Need(json, "minor"),
                Patch = (int)Need(json, "patch"),
                Build = (int)Need(json, "build"),
                Channel = Text(json, "channel"),
                Prerelease = (string?)json["prerelease"],
                BuildId = Guid.Parse(Text(json, "buildId")),
                TimestampUtc = DateTimeOffset.Parse(Text(json, "buildTimestampUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                BuildNumberSource = Text(json, "buildNumberSource"),
                Kind = (string?)json["kind"] ?? "engine",
                Reproducible = (bool?)json["reproducible"] ?? false,
                Source = Need(json, "source").ToObject<SourceRevision>(Serializer)!,
                Host = Need(json, "host").ToObject<HostInfo>(Serializer)!,
                Ci = json["ci"] is JObject ci ? ci.ToObject<CiInfo>(Serializer) : null,
                Context = json["context"] is JObject context ? context.ToObject<BuildContext>(Serializer)! : new BuildContext(),
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidCastException or FormatException or ArgumentException)
        {
            throw new InvalidDataException($"Damaged version metadata: {ex.Message}", ex);
        }
    }

    private static string Text(JObject json, string name) => (string?)Need(json, name) ?? throw new InvalidDataException($"Version metadata without '{name}'.");

    private static JToken Need(JObject json, string name) =>
        json[name] is { Type: not JTokenType.Null } value ? value : throw new InvalidDataException($"Version metadata without '{name}'.");
}
