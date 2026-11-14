using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Versioning;

/// <summary>An invalid <c>eng/version.json</c>, or an invalid version override.</summary>
public sealed class VersionConfigException(string message) : Exception(message)
{
    public VersionConfigException()
        : this("Invalid version configuration.")
    {
    }

    public VersionConfigException(string message, Exception inner)
        : this(message + " " + inner?.Message)
    {
    }
}

/// <summary>
/// The one authoritative version configuration, <c>eng/version.json</c>. MAJOR is set here by hand; MINOR, PATCH and
/// BUILD are generated (see <see cref="VersionResolver"/>).
/// </summary>
public sealed partial record VersionConfig
{
    public const string RelativePath = "eng/version.json";

    /// <summary>Largest value of a numeric version component (AssemblyVersion allows 65534, FileVersion 65535).</summary>
    public const int MaxComponent = 65534;

    public static readonly IReadOnlyList<string> Channels = ["development", "nightly", "preview", "beta", "release"];

    public int Major { get; init; } = 1;

    /// <summary>"DateDDMM": the build date as day and month (14 November = 1411).</summary>
    public string MinorStrategy { get; init; } = "DateDDMM";

    /// <summary>"GitCommitCount" (<c>git rev-list --count HEAD</c>) or "GitFirstParentCount" (<c>--first-parent</c>).</summary>
    public string PatchStrategy { get; init; } = "GitCommitCount";

    /// <summary>"Incremental": a counter in the build state, unless CI supplies the number.</summary>
    public string BuildStrategy { get; init; } = "Incremental";

    /// <summary>Time zone of the MINOR date: "UTC" or a system time zone id (IANA or Windows).</summary>
    public string DateTimezone { get; init; } = "UTC";

    /// <summary>"VersionIdentity" (one counter per MAJOR.MINOR.PATCH) or "Global" (one counter for every build).</summary>
    public string BuildCounterScope { get; init; } = "VersionIdentity";

    /// <summary>Release channel of pipeline builds; one of <see cref="Channels"/>.</summary>
    public string Channel { get; init; } = "development";

    /// <summary>Prerelease label (e.g. "beta.1"); null for the channel's default label.</summary>
    public string? Prerelease { get; init; }

    /// <summary>Without Git metadata (source archive, shallow clone): "Fallback" (PATCH 0, marked) or "Error".</summary>
    public string MissingGit { get; init; } = "Fallback";

    public static string PathIn(string engineRoot) => Path.Combine(engineRoot, "eng", "version.json");

    /// <exception cref="VersionConfigException">The file is missing or invalid.</exception>
    public static VersionConfig Load(string engineRoot)
    {
        var path = PathIn(engineRoot);
        if (!File.Exists(path))
        {
            throw new VersionConfigException($"Version configuration not found: {path}.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    /// <exception cref="VersionConfigException">Malformed JSON, unknown or mistyped settings, or invalid values (all listed).</exception>
    public static VersionConfig Parse(string json, string source = RelativePath)
    {
        JObject root;
        try
        {
            root = JToken.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }) as JObject
                ?? throw new VersionConfigException($"{source}: the configuration must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new VersionConfigException($"{source}: {ex.Message}");
        }

        var errors = new List<string>();
        var config = new VersionConfig();
        foreach (var property in root.Properties())
        {
            string? Text() => property.Value.Type == JTokenType.String ? (string?)property.Value : Wrong("a string");
            string? Wrong(string expected)
            {
                errors.Add($"'{property.Name}' must be {expected}.");
                return null;
            }

            switch (property.Name)
            {
                case "$schema" or "comment":
                    break;
                case "major":
                    if (property.Value.Type == JTokenType.Integer && (long)property.Value is >= 0 and <= MaxComponent)
                    {
                        config = config with { Major = (int)(long)property.Value };
                    }
                    else
                    {
                        Wrong($"an integer from 0 to {MaxComponent}");
                    }

                    break;
                case "minorStrategy":
                    config = config with { MinorStrategy = Text() ?? "" };
                    break;
                case "patchStrategy":
                    config = config with { PatchStrategy = Text() ?? "" };
                    break;
                case "buildStrategy":
                    config = config with { BuildStrategy = Text() ?? "" };
                    break;
                case "dateTimezone":
                    config = config with { DateTimezone = Text() ?? "" };
                    break;
                case "buildCounterScope":
                    config = config with { BuildCounterScope = Text() ?? "" };
                    break;
                case "channel":
                    config = config with { Channel = Text() ?? "" };
                    break;
                case "prerelease":
                    config = config with { Prerelease = property.Value.Type == JTokenType.Null ? null : Text() ?? "" };
                    break;
                case "missingGit":
                    config = config with { MissingGit = Text() ?? "" };
                    break;
                default:
                    errors.Add($"Unknown setting '{property.Name}'.");
                    break;
            }
        }

        errors.AddRange(config.Problems());
        return errors.Count == 0 ? config : throw new VersionConfigException($"{source} is invalid:\n  - {string.Join("\n  - ", errors.Distinct(StringComparer.Ordinal))}");
    }

    /// <summary>Every problem of this configuration; empty when it is valid.</summary>
    public IReadOnlyList<string> Problems()
    {
        var errors = new List<string>();
        if (Major is < 0 or > MaxComponent)
        {
            errors.Add($"'major' must be an integer from 0 to {MaxComponent}.");
        }

        Check(errors, "minorStrategy", MinorStrategy, "DateDDMM");
        Check(errors, "patchStrategy", PatchStrategy, "GitCommitCount", "GitFirstParentCount");
        Check(errors, "buildStrategy", BuildStrategy, "Incremental");
        Check(errors, "buildCounterScope", BuildCounterScope, "VersionIdentity", "Global");
        Check(errors, "channel", Channel, [.. Channels]);
        Check(errors, "missingGit", MissingGit, "Fallback", "Error");
        if (Prerelease is not null && !IsValidLabel(Prerelease))
        {
            errors.Add($"'prerelease' must be dot-separated letters, digits and hyphens (like \"beta.1\"), not \"{Prerelease}\".");
        }

        try
        {
            _ = TimeZone();
        }
        catch (VersionConfigException ex)
        {
            errors.Add(ex.Message);
        }

        return errors;
    }

    /// <exception cref="VersionConfigException">Unknown time zone.</exception>
    public TimeZoneInfo TimeZone()
    {
        if (string.Equals(DateTimezone, "UTC", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(DateTimezone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            throw new VersionConfigException($"'dateTimezone' \"{DateTimezone}\" is not a known time zone (use \"UTC\" or a system time zone id).");
        }
    }

    /// <summary>The prerelease label of a channel when none is configured: none for "release".</summary>
    public static string? ChannelLabel(string channel) => channel switch
    {
        "development" => "dev",
        "release" => null,
        _ => channel,
    };

    /// <summary>A SemVer prerelease label: dot-separated identifiers of letters, digits and hyphens, at most 64 characters.</summary>
    public static bool IsValidLabel(string label) => label.Length is > 0 and <= 64 && LabelPattern().IsMatch(label);

    private static void Check(List<string> errors, string name, string value, params string[] allowed)
    {
        if (!allowed.Contains(value, StringComparer.Ordinal))
        {
            errors.Add($"'{name}' must be {string.Join(" or ", allowed.Select(a => $"\"{a}\""))}, not \"{value}\".");
        }
    }

    [GeneratedRegex("^[0-9A-Za-z-]+(\\.[0-9A-Za-z-]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelPattern();
}
