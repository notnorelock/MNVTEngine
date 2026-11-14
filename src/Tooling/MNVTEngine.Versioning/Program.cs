using MNVTEngine.Versioning;
using Newtonsoft.Json;

// mnvt-version: the generator as a command, for eng/Version.proj, scripts/build.cmd and CI.
//   resolve  --root <engine> --out <session folder> [--allocate] [--configuration C] [--sdk V] [--msbuild V] [--kind K]
//   complete --root <engine> --session <session folder> --result succeeded|failed|canceled
//   show     --root <engine> [--json]       what a development build of the current tree would get (writes nothing)
try
{
    if (args.Length == 0)
    {
        return Usage();
    }

    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 1; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            return Usage();
        }

        options[args[i]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
    }

    string Need(string name) => options.TryGetValue(name, out var value) ? value : throw new VersionConfigException($"{args[0]} needs {name}.");
    string? Optional(string name) => options.TryGetValue(name, out var value) && value.Length > 0 ? value : null;
    var root = Path.GetFullPath(Need("--root"));

    switch (args[0])
    {
        case "resolve":
        {
            var output = Path.GetFullPath(Need("--out"));
            var allocate = options.ContainsKey("--allocate");
            var info = VersionResolver.Resolve(new VersionRequest(root)
            {
                Allocate = allocate,
                Kind = Optional("--kind") ?? "engine",
                Context = new BuildContext { Configuration = Optional("--configuration"), TargetFramework = Optional("--framework") },
                DotnetSdk = Optional("--sdk"),
                MsBuild = Optional("--msbuild"),
                Previous = allocate ? null : ReadQuietly(output),
            });
            VersionSession.Write(output, info);
            Console.WriteLine($"MNVTEngine {info.Version} ({info.InformationalVersion}){(info.Source.Problem is { } problem ? " - " + problem : "")}");
            return 0;
        }

        case "complete":
        {
            var session = VersionSession.Read(Need("--session")) ?? throw new VersionConfigException("The session folder has no version.json.");
            var result = Need("--result");
            if (session.BuildNumberSource == "counter")
            {
                new BuildCounter(BuildCounter.DefaultDirectory(root, Environment.GetEnvironmentVariable)).Complete(session.BuildId, result, DateTimeOffset.UtcNow);
            }

            Console.WriteLine($"MNVTEngine {session.Version}: {result}");
            return 0;
        }

        case "show":
        {
            var info = VersionResolver.Resolve(new VersionRequest(root));
            Console.WriteLine(options.ContainsKey("--json") ? info.ToJson().ToString(Formatting.Indented) : $"{info.Version}  {info.InformationalVersion}");
            return 0;
        }

        default:
            return Usage();
    }
}
catch (Exception ex) when (ex is VersionConfigException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"error MNVV001: {ex.Message}");
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("usage: MNVTEngine.Versioning resolve|complete|show --root <engine> [options] (see docs/architecture/versioning.md)");
    return 64;
}

// A damaged previous development session is replaced, not fatal: it only carried the build id and time to keep.
static EngineBuildInfo? ReadQuietly(string directory)
{
    try
    {
        return VersionSession.Read(directory);
    }
    catch (InvalidDataException)
    {
        return null;
    }
}
