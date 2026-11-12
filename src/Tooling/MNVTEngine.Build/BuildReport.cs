using System.Globalization;
using System.Text;
using MNVTEngine.Modules;
using MNVTEngine.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Build;

/// <summary>What a build report is made from. <see cref="PackageDirectory"/> is null for <c>mnvt build analyze</c>.</summary>
public sealed record BuildReportInput(GameProject Project, BuildProfile Profile, ContentResult Content, bool DevTools, IReadOnlyList<BuildDiagnostic> Diagnostics)
{
    /// <summary>The staged or exported game; null when nothing was packaged (analysis).</summary>
    public string? PackageDirectory { get; init; }

    public FrameworkPruning? Pruning { get; init; }

    /// <summary>How the game's C# runs; see docs/architecture/il2cpp-aot-pipeline.md.</summary>
    public string ScriptingBackend { get; init; } = "jit";

    /// <summary>Bytes the browser host and CEF added to the package; null without the browser module or before packaging.</summary>
    public long? BrowserHostBytes { get; init; }
}

/// <summary>
/// <c>build-report.json</c>: what a game contains and why. Modules with their reasons and evidence, assemblies, native
/// libraries, shaders and assets with sizes and owning modules, what was left out and why, the dynamic dependencies the
/// analysis could not see, sizes per part, and the build's diagnostics. Written next to the package
/// (<c>&lt;output&gt;.build-report.json</c>, outside the game folder) and printed by <c>mnvt build analyze</c>.
/// </summary>
public static class BuildReport
{
    public const int SchemaVersion = 1;

    public static string PathFor(string outputDirectory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory)) + ".build-report.json";

    public static JObject Create(BuildReportInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var catalog = ModuleCatalog.Builtin;
        var resolution = input.Content.Modules ?? throw new ArgumentException("The content compile did not resolve modules.", nameof(input));
        var usage = input.Content.Usage!;
        var package = input.PackageDirectory;

        // Assemblies and natives: what the package holds or, for an analysis, what the modules bring (sizes of this engine build).
        var managed = package is null
            ? resolution.Modules.SelectMany(m => m.RuntimeAssemblies).Distinct(StringComparer.Ordinal).Select(a => (Name: a, Size: SizeOf(Path.Combine(AppContext.BaseDirectory, a + ".dll"))))
            : Directory.EnumerateFiles(package, "*.dll").Concat(Directory.EnumerateFiles(Path.Combine(package, GamePackage.ManagedFolder), "*.dll"))
                .Select(f => (Name: Path.GetFileNameWithoutExtension(f), Size: (long?)new FileInfo(f).Length));
        var managedList = managed.OrderBy(a => a.Name, StringComparer.Ordinal).ToList();
        var natives = package is null
            ? resolution.Modules.SelectMany(m => m.NativeLibraries).Select(n => (Name: n, Size: NativeSize(n)))
            : Directory.EnumerateFiles(Path.Combine(package, GamePackage.NativeFolder)).Select(f => (Name: Path.GetFileName(f), Size: (long?)new FileInfo(f).Length));
        var nativeList = natives.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var excluded = resolution.Excluded.Where(e => !(input.DevTools && e.Module.Id == "devtools")).ToList();

        return new JObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["project"] = input.Project.Name,
            ["profile"] = resolution.Profile.Id(),
            ["buildProfile"] = input.Profile.Name,
            ["configuration"] = input.Profile.Configuration,
            ["target"] = input.Profile.Target,
            ["engineVersion"] = GamePackager.EngineVersion,
            ["scriptingBackend"] = new JObject
            {
                ["id"] = input.ScriptingBackend,
                ["description"] = input.ScriptingBackend == "aot"
                    ? "NativeAOT: engine and game C# compiled ahead of time into one native executable (RyuJIT code generation, no IL, no JIT, no .NET runtime). Not IL2CPP: no C++ is generated."
                    : "CoreCLR with the JIT: engine and game C# ship as IL assemblies and are compiled at run time.",
            },
            ["devTools"] = input.DevTools,
            ["browser"] = resolution.Modules.Any(m => m.Id == "browser")
                ? new JObject
                {
                    ["host"] = "MNVTBrowserHost.exe",
                    ["chromium"] = GamePackager.ChromiumFolder,
                    ["engine"] = "Chromium via CEF and CefSharp.OffScreen (out of process; the host runs on CoreCLR from Dependencies/Runtime, also in AOT games)",
                    ["rendering"] = "CPU off-screen rendering: dirty rectangles through shared memory into textures; no GPU zero-copy",
                    ["platforms"] = new JArray("win-x64"),
                    ["locales"] = new JArray("en-US"),
                    ["size"] = input.BrowserHostBytes,
                    ["licenses"] = "CEF and CefSharp: BSD 3-clause; Chromium: its bundled third-party licenses (THIRD-PARTY-NOTICES.md). No proprietary codecs (H.264, AAC).",
                }
                : null,
            ["modules"] = new JObject
            {
                ["enabled"] = new JArray(resolution.Modules.Select(m => new JObject
                {
                    ["id"] = m.Id,
                    ["inclusion"] = Inclusion(resolution.Reasons[m.Id]),
                    ["reason"] = resolution.Reasons[m.Id],
                    ["evidence"] = new JArray(usage.Modules.GetValueOrDefault(m.Id) ?? []),
                })),
                ["excluded"] = new JArray(excluded.Select(e => new JObject { ["id"] = e.Module.Id, ["reason"] = e.Reason })),
            },
            ["assemblies"] = new JObject
            {
                ["included"] = new JArray(managedList.Select(a => new JObject { ["name"] = a.Name, ["size"] = a.Size, ["module"] = Owner(catalog, a.Name, input.Project) })),
                ["excluded"] = new JArray(excluded.SelectMany(e => e.Module.RuntimeAssemblies.Where(a => !managedList.Any(m => m.Name == a))
                    .Select(a => new JObject { ["name"] = a, ["module"] = e.Module.Id, ["reason"] = e.Reason }))),
            },
            ["nativeLibraries"] = new JObject
            {
                ["included"] = new JArray(nativeList.Select(n => new JObject { ["name"] = n.Name, ["size"] = n.Size, ["module"] = catalog.Modules.FirstOrDefault(m => m.NativeLibraries.Contains(n.Name, StringComparer.OrdinalIgnoreCase))?.Id })),
                ["excluded"] = new JArray(excluded.SelectMany(e => e.Module.NativeLibraries.Where(n => !nativeList.Any(i => string.Equals(i.Name, n, StringComparison.OrdinalIgnoreCase)))
                    .Select(n => new JObject { ["name"] = n, ["module"] = e.Module.Id, ["reason"] = e.Reason }))),
            },
            ["framework"] = new JObject
            {
                ["pruned"] = input.Pruning is not null,
                ["keptAssemblies"] = input.Pruning?.Kept,
                ["removedBytes"] = input.Pruning?.RemovedBytes,
                ["removed"] = new JArray(input.Pruning?.Removed ?? []),
                ["trimming"] = "none: whole assemblies only, no IL trimming",
            },
            ["shaders"] = new JObject
            {
                ["stages"] = input.Content.Shaders,
                ["sources"] = new JArray(input.Content.ShaderSources),
            },
            ["assets"] = new JObject
            {
                ["included"] = new JArray(usage.Included.Select(a => new JObject { ["path"] = a.Path, ["type"] = a.Type, ["size"] = a.Size, ["module"] = catalog.ModuleOfAssetType(a.Type)?.Id, ["reason"] = a.Reason })),
                ["stripped"] = new JArray(usage.Stripped.Select(a => new JObject { ["path"] = a.Path, ["type"] = a.Type, ["size"] = a.Size, ["reason"] = a.Reason })),
            },
            ["dynamicDependencies"] = new JArray(usage.Notes),
            ["sizes"] = package is null ? null : Sizes(package),
            ["diagnostics"] = new JArray(input.Diagnostics.Where(d => d.Severity != BuildSeverity.Info).Select(d => new JObject
            {
                ["severity"] = d.Severity.ToString().ToLowerInvariant(),
                ["code"] = d.Code,
                ["message"] = d.Message,
                ["file"] = d.File,
            })),
        };
    }

    public static void Write(JObject report, string path) => File.WriteAllText(path, report.ToString(Formatting.Indented) + "\n");

    /// <summary>The report as text: why each module, assembly and asset is in or out, largest first where size matters.</summary>
    public static string Describe(JObject report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{report["project"]} ({report["profile"]}), {report["buildProfile"]}: {report["configuration"]} {report["target"]}, scripting backend {report["scriptingBackend"]!["id"]}, dev tools {((bool)report["devTools"]! ? "on" : "off")}\n");
        text.Append("\nModules\n");
        foreach (var module in report["modules"]!["enabled"]!)
        {
            text.Append(CultureInfo.InvariantCulture, $"  + {module["id"],-11} {module["inclusion"],-10} {module["reason"]}\n");
        }

        foreach (var module in report["modules"]!["excluded"]!)
        {
            text.Append(CultureInfo.InvariantCulture, $"  - {module["id"],-11} {module["reason"]}\n");
        }

        Section(text, "Assemblies", report["assemblies"]!["included"]!, a => $"{a["name"]} ({a["module"]})");
        Section(text, "Native libraries", report["nativeLibraries"]!["included"]!, n => $"{n["name"]} ({n["module"] ?? "-"})");
        var excludedFiles = report["assemblies"]!["excluded"]!.Concat(report["nativeLibraries"]!["excluded"]!).ToList();
        if (excludedFiles.Count > 0)
        {
            text.Append("\nLeft out\n");
            foreach (var file in excludedFiles)
            {
                text.Append(CultureInfo.InvariantCulture, $"  {file["name"]} ({file["module"]}: {file["reason"]})\n");
            }
        }

        text.Append(CultureInfo.InvariantCulture, $"\nShaders: {report["shaders"]!["stages"]} stages from {string.Join(", ", report["shaders"]!["sources"]!.Select(s => (string?)s))}\n");
        Section(text, "Assets", report["assets"]!["included"]!, a => $"{a["path"]} ({a["reason"]})");
        var stripped = report["assets"]!["stripped"]!.ToList();
        text.Append(CultureInfo.InvariantCulture, $"\nStripped assets ({stripped.Count}, {Size(stripped.Sum(a => (long?)a["size"] ?? 0))})\n");
        foreach (var asset in stripped)
        {
            text.Append(CultureInfo.InvariantCulture, $"  {asset["path"]}\n");
        }

        foreach (var note in report["dynamicDependencies"]!)
        {
            text.Append("  note: ").Append((string?)note).Append('\n');
        }

        if (report["sizes"] is JObject sizes)
        {
            text.Append(CultureInfo.InvariantCulture, $"\nSize: {Size((long)sizes["total"]!)} = framework {Size((long)sizes["framework"]!)} + managed {Size((long)sizes["managed"]!)} + native {Size((long)sizes["native"]!)} + content {Size((long)sizes["content"]!)}\n");
        }

        if (report["framework"]!["pruned"]!.Value<bool>())
        {
            text.Append(CultureInfo.InvariantCulture, $"Framework: {report["framework"]!["removed"]!.Count()} unreachable files removed ({Size((long)report["framework"]!["removedBytes"]!)}), {report["framework"]!["keptAssemblies"]} assemblies kept\n");
        }

        return text.ToString();
    }

    private static void Section(StringBuilder text, string title, JToken items, Func<JToken, string> describe)
    {
        var list = items.OrderByDescending(i => (long?)i["size"] ?? 0).ToList();
        var total = list.Sum(i => (long?)i["size"] ?? 0);
        text.Append(CultureInfo.InvariantCulture, $"\n{title} ({list.Count}{(total > 0 ? ", " + Size(total) : "")})\n");
        foreach (var item in list)
        {
            text.Append(CultureInfo.InvariantCulture, $"  {((long?)item["size"] is { } size ? Size(size) : "?"),10}  {describe(item)}\n");
        }
    }

    private static JObject Sizes(string package)
    {
        long Folder(string relative) => Directory.Exists(Path.Combine(package, relative)) ? Directory.EnumerateFiles(Path.Combine(package, relative), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;
        var total = Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        var framework = Folder(GamePackage.RuntimeFolder);
        var native = Folder(GamePackage.NativeFolder);
        var content = Directory.EnumerateDirectories(package, "*_Data").Sum(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length));
        return new JObject
        {
            ["total"] = total,
            ["framework"] = framework,
            ["managed"] = total - framework - native - content, // Dependencies/Managed plus the host in the root
            ["native"] = native,
            ["content"] = content,
        };
    }

    private static string Inclusion(string reason) =>
        reason == "always" ? "always"
        : reason == "enabled in the project" ? "explicit"
        : reason.StartsWith("used: ", StringComparison.Ordinal) ? "automatic"
        : reason.StartsWith("required by ", StringComparison.Ordinal) ? "dependency"
        : reason.StartsWith("with ", StringComparison.Ordinal) ? "automatic"
        : "available";

    private static string Owner(ModuleCatalog catalog, string assembly, GameProject project) =>
        catalog.ModuleOfAssembly(assembly)?.Id
        ?? (assembly.StartsWith("MNVTEngine.", StringComparison.Ordinal) || assembly == project.Name || assembly.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) ? "host" : "dependency");

    private static long? SizeOf(string file) => File.Exists(file) ? new FileInfo(file).Length : null;

    /// <summary>A native library of this engine build: next to the tool, or in its runtimes/&lt;rid&gt;/native folder.</summary>
    private static long? NativeSize(string name) =>
        SizeOf(Path.Combine(AppContext.BaseDirectory, name))
        ?? (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "runtimes"))
            ? Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "runtimes"), name, SearchOption.AllDirectories).Select(f => (long?)new FileInfo(f).Length).FirstOrDefault()
            : null);

    private static string Size(long bytes) => bytes >= 1024 * 1024
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.0} MB")
        : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0} KB");
}
