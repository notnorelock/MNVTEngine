using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MNVTEngine.Versioning;

/// <summary>How to resolve a version.</summary>
/// <param name="EngineRoot">The engine tree: <c>eng/version.json</c> and the Git work tree.</param>
public sealed record VersionRequest(string EngineRoot)
{
    /// <summary>
    /// True for a pipeline build (<c>mnvt build</c>, <c>scripts/build.cmd</c>, CI): allocates a build number. False for
    /// a development build (<c>dotnet build</c>): BUILD 0, no allocation, and the identity stays the same until the source
    /// changes, so incremental builds stay incremental.
    /// </summary>
    public bool Allocate { get; init; }

    public string Kind { get; init; } = "engine";

    public BuildContext Context { get; init; } = new();

    public Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;

    /// <summary>The current time (tests fix it); the build timestamp unless overridden.</summary>
    public DateTimeOffset? Now { get; init; }

    public string? StateDirectory { get; init; }

    /// <summary>The .NET SDK version when the caller knows it (MSBuild does); else <c>dotnet --version</c> is asked.</summary>
    public string? DotnetSdk { get; init; }

    public string? MsBuild { get; init; }

    /// <summary>A development build's previous identity: its build id and timestamp are kept while the identity is unchanged.</summary>
    public EngineBuildInfo? Previous { get; init; }
}

/// <summary>
/// Resolves the version of one logical build from <c>eng/version.json</c>, the Git work tree, the build counter and
/// the overrides (environment variables, see docs/architecture/versioning.md):
/// <list type="table">
/// <item><term>MNVT_VERSION</term><description>all four components, "1.1411.140.4" (release automation)</description></item>
/// <item><term>MNVT_BUILD_NUMBER</term><description>authoritative BUILD from CI; the local counter is not used</description></item>
/// <item><term>MNVT_PATCH</term><description>PATCH where Git cannot count (shallow clone, source archive)</description></item>
/// <item><term>MNVT_BUILD_TIMESTAMP / SOURCE_DATE_EPOCH</term><description>the build time (ISO 8601 / Unix seconds); MINOR follows it</description></item>
/// <item><term>MNVT_BUILD_ID</term><description>the build id (a GUID)</description></item>
/// <item><term>MNVT_CHANNEL / MNVT_PRERELEASE</term><description>release channel and prerelease label</description></item>
/// <item><term>MNVT_REPRODUCIBLE</term><description>"true": fixed inputs only; the build id is derived from the identity</description></item>
/// </list>
/// </summary>
public static class VersionResolver
{
    /// <exception cref="VersionConfigException">Invalid configuration or override, or missing Git metadata with <c>missingGit: Error</c>.</exception>
    public static EngineBuildInfo Resolve(VersionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var env = request.Environment;
        var config = VersionConfig.Load(request.EngineRoot);
        var reproducible = Flag(env, "MNVT_REPRODUCIBLE");

        var timestamp = Timestamp(env) ?? (reproducible
            ? throw new VersionConfigException("MNVT_REPRODUCIBLE needs a fixed time: set MNVT_BUILD_TIMESTAMP or SOURCE_DATE_EPOCH.")
            : request.Now ?? DateTimeOffset.UtcNow);
        timestamp = new DateTimeOffset(timestamp.UtcTicks - (timestamp.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);

        var source = GitSource.Read(request.EngineRoot, config.PatchStrategy == "GitFirstParentCount", env);
        var patchOverride = Number(env, "MNVT_PATCH");
        if (source.CommitCount is null && patchOverride is null && config.MissingGit == "Error")
        {
            throw new VersionConfigException($"PATCH needs the Git commit count: {source.Problem} Set MNVT_PATCH, or \"missingGit\": \"Fallback\" in {VersionConfig.RelativePath}.");
        }

        var major = config.Major;
        var minor = VersionDate.Parse(VersionDate.Ddmm(timestamp, config.TimeZone()));
        var patch = patchOverride ?? source.CommitCount ?? 0;
        if (patch > VersionConfig.MaxComponent)
        {
            throw new VersionConfigException($"PATCH {patch} exceeds {VersionConfig.MaxComponent}; raise MAJOR or use GitFirstParentCount.");
        }

        var channel = env("MNVT_CHANNEL") is { Length: > 0 } c ? c : request.Allocate ? config.Channel : "development";
        if (!VersionConfig.Channels.Contains(channel, StringComparer.Ordinal))
        {
            throw new VersionConfigException($"MNVT_CHANNEL must be one of {string.Join(", ", VersionConfig.Channels)}, not \"{channel}\".");
        }

        var prerelease = env("MNVT_PRERELEASE") is { Length: > 0 } p ? p : config.Prerelease ?? VersionConfig.ChannelLabel(channel);
        if (prerelease is not null && !VersionConfig.IsValidLabel(prerelease))
        {
            throw new VersionConfigException($"Prerelease label \"{prerelease}\" is not dot-separated letters, digits and hyphens.");
        }

        int? build = Number(env, "MNVT_BUILD_NUMBER");
        var numberSource = build is null ? request.Allocate ? "counter" : "development" : "ci";
        if (env("MNVT_VERSION") is { Length: > 0 } fixedVersion)
        {
            (major, minor, patch, build) = ParseVersion(fixedVersion);
            numberSource = "override";
        }

        if (reproducible && build is null && request.Allocate)
        {
            throw new VersionConfigException("MNVT_REPRODUCIBLE needs a fixed build number: set MNVT_BUILD_NUMBER or MNVT_VERSION.");
        }

        var info = new EngineBuildInfo
        {
            Major = major,
            Minor = minor,
            Patch = patch,
            Build = build ?? 0,
            Channel = channel,
            Prerelease = prerelease,
            BuildId = Guid.Empty,
            TimestampUtc = timestamp,
            BuildNumberSource = numberSource,
            Kind = request.Kind,
            Reproducible = reproducible,
            Source = patchOverride is not null && source.CommitCount is null ? source with { Problem = source.Problem + " PATCH from MNVT_PATCH." } : source,
            Host = Host(request),
            Ci = Ci(env),
            Context = request.Context,
        };

        // Build id: given; derived from the identity (reproducible builds, and development builds, whose identity is
        // their source state); or a new one per pipeline build.
        var buildId = env("MNVT_BUILD_ID") is { Length: > 0 } id
            ? Guid.TryParse(id, out var parsed) ? parsed : throw new VersionConfigException($"MNVT_BUILD_ID \"{id}\" is not a GUID.")
            : reproducible || !request.Allocate ? Derived(info) : Guid.NewGuid();
        info = info with { BuildId = buildId };

        if (!request.Allocate && request.Previous is { } previous && previous.BuildId == buildId && Timestamp(env) is null)
        {
            info = info with { TimestampUtc = previous.TimestampUtc }; // same source state: same development build
        }

        if (request.Allocate && build is null)
        {
            var counter = new BuildCounter(request.StateDirectory ?? BuildCounter.DefaultDirectory(request.EngineRoot, env));
            var scope = config.BuildCounterScope == "Global" ? "global" : info.Identity;
            var number = counter.Allocate(scope, buildId, request.Kind, n => (info with { Build = n }).Version, DateTimeOffset.UtcNow);
            info = info with { Build = number };
        }

        return info;
    }

    /// <summary>"MAJOR.DDMM.PATCH.BUILD" (DDMM with or without its leading zero).</summary>
    /// <exception cref="VersionConfigException">Not four valid components.</exception>
    public static (int Major, int Minor, int Patch, int Build) ParseVersion(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = text.Split('.');
        if (parts.Length != 4 || !parts.All(p => p.Length is > 0 and <= 5 && p.All(char.IsAsciiDigit)))
        {
            throw new VersionConfigException($"MNVT_VERSION \"{text}\" is not MAJOR.DDMM.PATCH.BUILD.");
        }

        var numbers = parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        if (numbers.Any(n => n > VersionConfig.MaxComponent) || numbers[3] == 0)
        {
            throw new VersionConfigException($"MNVT_VERSION \"{text}\": components go up to {VersionConfig.MaxComponent}, and BUILD starts at 1.");
        }

        try
        {
            return (numbers[0], VersionDate.Parse(numbers[1].ToString("D4", CultureInfo.InvariantCulture)), numbers[2], numbers[3]);
        }
        catch (FormatException ex)
        {
            throw new VersionConfigException($"MNVT_VERSION \"{text}\": {ex.Message}");
        }
    }

    private static Guid Derived(EngineBuildInfo info)
    {
        var key = $"{info.Version}|{info.Source.Commit}|{info.Source.Dirty}|{info.Kind}|{info.Context.Configuration}|{info.Context.TargetPlatform}|{info.Context.Profile}|{info.Context.ScriptingBackend}|{(info.Reproducible ? info.TimestampUtc.ToUnixTimeSeconds() : 0)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50); // RFC 9562 layout: version 5 (name-based), variant 10
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    private static DateTimeOffset? Timestamp(Func<string, string?> env)
    {
        if (env("MNVT_BUILD_TIMESTAMP") is { Length: > 0 } text)
        {
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
                ? time : throw new VersionConfigException($"MNVT_BUILD_TIMESTAMP \"{text}\" is not an ISO 8601 time.");
        }

        if (env("SOURCE_DATE_EPOCH") is { Length: > 0 } epoch)
        {
            return long.TryParse(epoch, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds <= 253402300799
                ? DateTimeOffset.FromUnixTimeSeconds(seconds) : throw new VersionConfigException($"SOURCE_DATE_EPOCH \"{epoch}\" is not Unix seconds.");
        }

        return null;
    }

    private static int? Number(Func<string, string?> env, string name) => env(name) is not { Length: > 0 } text ? null
        : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= VersionConfig.MaxComponent ? n
        : throw new VersionConfigException($"{name} \"{text}\" must be a whole number from 1 to {VersionConfig.MaxComponent}.");

    private static bool Flag(Func<string, string?> env, string name) => env(name) is { } value && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    private static HostInfo Host(VersionRequest request) => new(
        RuntimeInformation.OSDescription.Trim(),
        RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        request.DotnetSdk is { Length: > 0 } sdk ? sdk : DotnetSdkVersion(),
        request.MsBuild is { Length: > 0 } msbuild ? msbuild : null,
        RuntimeInformation.FrameworkDescription);

    /// <summary>The CI that runs the build, from the variables each CI documents; only its name and run id are kept.</summary>
    private static CiInfo? Ci(Func<string, string?> env)
    {
        if (env("MNVT_CI_PROVIDER") is { Length: > 0 } provider)
        {
            return new CiInfo(provider, env("MNVT_CI_RUN_ID"));
        }

        if (Flag(env, "GITHUB_ACTIONS"))
        {
            return new CiInfo("github-actions", env("GITHUB_RUN_ID") is { } run ? $"{run}.{env("GITHUB_RUN_ATTEMPT") ?? "1"}" : null);
        }

        return Flag(env, "TF_BUILD") ? new CiInfo("azure-pipelines", env("BUILD_BUILDID"))
            : Flag(env, "GITLAB_CI") ? new CiInfo("gitlab", env("CI_PIPELINE_ID"))
            : env("JENKINS_URL") is { Length: > 0 } ? new CiInfo("jenkins", env("BUILD_NUMBER"))
            : env("TEAMCITY_VERSION") is { Length: > 0 } ? new CiInfo("teamcity", env("BUILD_NUMBER"))
            : Flag(env, "CI") ? new CiInfo("ci", null)
            : null;
    }

    private static string? DotnetSdkVersion()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            var output = process.StandardOutput.ReadToEnd().Trim();
            return process.WaitForExit(TimeSpan.FromSeconds(20)) && process.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // no SDK on PATH: the metadata says so (null)
        }
    }
}
