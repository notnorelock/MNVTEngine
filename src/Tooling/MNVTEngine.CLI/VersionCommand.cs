using MNVTEngine.Build;
using MNVTEngine.Versioning;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.CLI;

/// <summary><c>mnvt version</c>: this engine build's version and metadata, and with --verbose the next build's inputs.</summary>
public static class VersionCommand
{
    public static int Run(CommandLine command, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(output);
        var metadata = JObject.Parse(EngineVersion.MetadataJson);
        if (command.Has("--json"))
        {
            output.WriteLine(metadata.ToString(Formatting.Indented));
            return Cli.Ok;
        }

        output.Write(EngineVersion.Describe());
        if (!command.Has("--verbose"))
        {
            return Cli.Ok;
        }

        output.WriteLine($"{"Assembly:",-15}{EngineVersion.NumericVersion} file, {metadata["assemblyVersion"]} assembly, {EngineVersion.InformationalVersion} informational");
        if (metadata["host"] is JObject host)
        {
            output.WriteLine($"{"Built on:",-15}{host["operatingSystem"]} ({host["architecture"]}), .NET SDK {host["dotnetSdk"] ?? "unknown"}, MSBuild {host["msBuild"] ?? "unknown"}");
        }

        if (metadata["ci"] is JObject ci)
        {
            output.WriteLine($"{"CI:",-15}{ci["provider"]} run {ci["runId"] ?? "unknown"}");
        }

        output.WriteLine();
        string root;
        try
        {
            root = EngineLocation.FindRoot();
        }
        catch (DirectoryNotFoundException ex)
        {
            output.WriteLine($"Next build: no engine source tree ({ex.Message})");
            return Cli.Ok;
        }

        output.WriteLine($"Next build of {root}");
        try
        {
            var config = VersionConfig.Load(root);
            output.WriteLine($"  {VersionConfig.RelativePath}: major {config.Major}, {config.MinorStrategy} ({config.DateTimezone}), {config.PatchStrategy}, {config.BuildStrategy} build numbers per {config.BuildCounterScope}, channel {config.Channel}");
            var next = VersionResolver.Resolve(new VersionRequest(root)); // a development resolve: allocates nothing
            var source = next.Source;
            var label = config.Prerelease ?? VersionConfig.ChannelLabel(config.Channel);
            output.WriteLine($"  Version:      {next.Major}.{next.MinorText}.{next.Patch}.<build>{(label is null ? "" : "-" + label)}");
            output.WriteLine(source.Commit is null
                ? $"  Git:          unavailable: {source.Problem}"
                : $"  Git:          {source.Commit} on {source.Branch ?? "(detached HEAD)"}, {source.CommitCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "uncounted"} commits{(source.Dirty ? ", modified working tree" : "")}{(source.Shallow ? ", shallow clone" : "")}");
            if (source.Problem is { } problem && source.Commit is not null)
            {
                output.WriteLine($"                {problem}");
            }

            var counter = new BuildCounter(BuildCounter.DefaultDirectory(root, Environment.GetEnvironmentVariable));
            var attempts = counter.Attempts();
            var last = attempts.Count == 0 ? null : attempts[^1];
            output.WriteLine($"  Build state:  {counter.StateDirectory}{(last is null ? " (no builds yet)" : $", last build {last.Version} ({last.Result}, {last.Kind})")}");
        }
        catch (Exception ex) when (ex is VersionConfigException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            output.WriteLine($"  error: {ex.Message}");
            return Cli.Failed;
        }

        return Cli.Ok;
    }
}
