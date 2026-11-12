using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using MNVTEngine.Assets;
using MNVTEngine.Modules;
using MNVTEngine.Runtime;
using MNVTEngine.Serialization;
using ModuleDefinition = MNVTEngine.Modules.ModuleDefinition;

namespace MNVTEngine.Build;

/// <summary>An asset the build keeps, or strips, and why.</summary>
public sealed record AssetDecision(Guid Guid, string Path, string Type, string Reason);

/// <summary>What a project uses: the assets it ships, the ones it does not, and the evidence for each module.</summary>
public sealed class ProjectUsage
{
    internal ProjectUsage(IReadOnlyList<AssetDecision> included, IReadOnlyList<AssetDecision> stripped, IReadOnlyDictionary<string, IReadOnlyList<string>> modules, IReadOnlyList<string> notes)
    {
        (Included, Stripped, Modules, Notes) = (included, stripped, modules, notes);
    }

    /// <summary>Assets the game ships, with what keeps each one (ordered by path).</summary>
    public IReadOnlyList<AssetDecision> Included { get; }

    /// <summary>Assets nothing uses: not shipped (ordered by path).</summary>
    public IReadOnlyList<AssetDecision> Stripped { get; }

    /// <summary>Evidence of use per module id, for <see cref="ModuleResolver"/>.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Modules { get; }

    /// <summary>What the analysis could not decide statically, and how to keep such content.</summary>
    public IReadOnlyList<string> Notes { get; }

    public bool Includes(Guid guid) => Included.Any(a => a.Guid == guid);
}

/// <summary>
/// Build-time usage analysis. The asset graph starts at the startup scene, the assets the project settings name, assets
/// marked <c>"alwaysInclude"</c> in their .meta, and assets whose path or <c>asset:</c> reference appears as a string in
/// the C# scripts or the mission scripts (scenes loaded by name, prefabs spawned by path); it follows every reference
/// the importers recorded (scene to prefab to material to shader and texture). Module evidence comes from what the
/// reachable content contains: components in scenes and prefabs, asset types, the engine types the C# scripts use, and
/// the natives the mission scripts call. Content loaded by a path computed at run time cannot be seen: mark it
/// <c>"alwaysInclude"</c>.
/// </summary>
public static partial class UsageAnalyzer
{
    private const int EvidencePerModule = 5;

    public static ProjectUsage Analyze(GameProject project, AssetDatabase assets, IReadOnlyDictionary<Guid, IReadOnlyCollection<Guid>> dependencies, string? scriptsAssembly, ModuleCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(dependencies);
        catalog ??= ModuleCatalog.Builtin;
        var reasons = new Dictionary<Guid, string>();
        var queue = new Queue<Guid>();
        var notes = new List<string>();

        void Keep(Guid guid, string reason)
        {
            if (assets.Get(guid) is not null && reasons.TryAdd(guid, reason))
            {
                queue.Enqueue(guid);
            }
        }

        Guid? ByPath(string path) => path.Length > 0 && assets.TryGetGuid(path, out var guid) ? guid : null;

        if (ByPath(project.StartupScene) is { } startup)
        {
            Keep(startup, "startup scene");
        }

        foreach (var (setting, path) in new[] { ("window.icon", project.Window.Icon), ("splashScreen.logo", project.SplashScreen.Logo), ("splashScreen.backgroundImage", project.SplashScreen.BackgroundImage), ("loadingScreen.logo", project.LoadingScreen.Logo), ("loadingScreen.font", project.LoadingScreen.Font) })
        {
            if (path is { Length: > 0 } && ByPath(path) is { } guid)
            {
                Keep(guid, $"project setting {setting}");
            }
        }

        foreach (var record in assets.Assets.Where(a => assets.IsAlwaysIncluded(a.Guid)))
        {
            Keep(record.Guid, "always include (.meta)");
        }

        // Strings in code: scenes loaded by name, prefabs and other content addressed by path or asset reference.
        var codeStrings = scriptsAssembly is not null && File.Exists(scriptsAssembly) ? UserStrings(scriptsAssembly) : [];
        foreach (var text in codeStrings)
        {
            if (Referenced(assets, text) is { } guid)
            {
                Keep(guid, $"named in the C# scripts (\"{text}\")");
            }
        }

        // Follow references; mission scripts reached on the way may name more content (scene.load("...")).
        var scanned = new HashSet<Guid>();
        while (queue.Count > 0)
        {
            var guid = queue.Dequeue();
            var record = assets.Get(guid)!;
            foreach (var dependency in dependencies.GetValueOrDefault(guid) ?? [])
            {
                Keep(dependency, $"referenced by {record.Path}");
            }

            if (record.Type == AssetTypes.Script && scanned.Add(guid))
            {
                foreach (Match literal in StringLiteral().Matches(ReadText(assets, guid)))
                {
                    if (Referenced(assets, Regex.Unescape(literal.Groups[1].Value)) is { } named)
                    {
                        Keep(named, $"named in {record.Path}");
                    }
                }
            }
        }

        var included = reasons.Select(p => (Record: assets.Get(p.Key)!, Reason: p.Value)).OrderBy(p => p.Record.Path, StringComparer.Ordinal)
            .Select(p => new AssetDecision(p.Record.Guid, p.Record.Path, p.Record.Type, p.Reason)).ToList();
        var stripped = assets.Assets.Where(a => !reasons.ContainsKey(a.Guid)).OrderBy(a => a.Path, StringComparer.Ordinal)
            .Select(a => new AssetDecision(a.Guid, a.Path, a.Type, "nothing the build analyses uses it")).ToList();
        foreach (var dynamicKind in stripped.Where(a => a.Type is AssetTypes.Scene or AssetTypes.Prefab or AssetTypes.Audio).Take(10))
        {
            notes.Add($"{dynamicKind.Path} is not referenced and not shipped. If the game loads it by a path computed at run time, set \"alwaysInclude\": true in its .meta.");
        }

        return new ProjectUsage(included, stripped, ModuleEvidence(project, assets, included, scriptsAssembly, catalog), notes);
    }

    private static Dictionary<string, IReadOnlyList<string>> ModuleEvidence(GameProject project, AssetDatabase assets, List<AssetDecision> included, string? scriptsAssembly, ModuleCatalog catalog)
    {
        var evidence = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void Add(ModuleDefinition? module, string what)
        {
            if (module is not null && module.Activation != ModuleActivation.Always)
            {
                if (!evidence.TryGetValue(module.Id, out var list))
                {
                    evidence[module.Id] = list = new SortedSet<string>(StringComparer.Ordinal);
                }

                list.Add(what);
            }
        }

        var paths = included.Select(a => a.Path).ToHashSet(AssetPaths.Comparer);
        foreach (var component in ProjectProfileMigration.Components(project).Where(c => paths.Contains(c.Source)))
        {
            Add(catalog.ModuleOfComponent(component.ComponentType), $"{ShortName(component.ComponentType)} in {component.Source}");
        }

        foreach (var asset in included)
        {
            Add(catalog.ModuleOfAssetType(asset.Type), $"{asset.Type} asset {asset.Path}");
            if (asset.Type == AssetTypes.Script && MissionNativeModule().IsMatch(ReadText(assets, asset.Guid)))
            {
                Add(catalog.ModuleOfAssembly("MNVTEngine.Audio"), $"audio.* calls in {asset.Path}");
            }
        }

        if (scriptsAssembly is not null && File.Exists(scriptsAssembly))
        {
            foreach (var (assembly, type) in EngineTypeReferences(scriptsAssembly))
            {
                Add(catalog.ModuleOfAssembly(assembly), $"C# scripts use {type} ({assembly})");
            }
        }

        return evidence.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)[.. p.Value.Take(EvidencePerModule)], StringComparer.Ordinal);
    }

    /// <summary>An asset named by a string: its path (with or without extension) or an <c>asset:</c> reference.</summary>
    private static Guid? Referenced(AssetDatabase assets, string text)
    {
        if (text.StartsWith("asset:", StringComparison.Ordinal) && AssetRef.TryParse(text, out var reference) && assets.Get(reference.Guid) is not null)
        {
            return reference.Guid;
        }

        if (text.Length < 3 || text.Length > 260 || text.Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        if (assets.TryGetGuid(text, out var exact))
        {
            return exact;
        }

        // Scenes and prefabs are often named without their extension.
        return assets.Assets.FirstOrDefault(a => a.Type is AssetTypes.Scene or AssetTypes.Prefab
            && string.Equals(Path.ChangeExtension(a.Path, null), text, StringComparison.OrdinalIgnoreCase))?.Guid;
    }

    private static string ReadText(AssetDatabase assets, Guid guid) => System.Text.Encoding.UTF8.GetString(assets.ReadAllBytes(guid));

    /// <summary>Every string literal of an assembly's IL (the #US heap), read without loading it.</summary>
    private static List<string> UserStrings(string assembly)
    {
        using var stream = File.OpenRead(assembly);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var strings = new List<string>();
        var handle = MetadataTokens.UserStringHandle(1);
        while (!handle.IsNil)
        {
            strings.Add(metadata.GetUserString(handle));
            handle = metadata.GetNextHandle(handle);
        }

        return strings;
    }

    /// <summary>Engine types the scripts assembly references, with their assembly.</summary>
    private static List<(string Assembly, string Type)> EngineTypeReferences(string assembly)
    {
        using var stream = File.OpenRead(assembly);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var result = new List<(string, string)>();
        foreach (var handle in metadata.TypeReferences)
        {
            var type = metadata.GetTypeReference(handle);
            if (type.ResolutionScope.Kind == HandleKind.AssemblyReference)
            {
                var name = metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name);
                if (name.StartsWith("MNVTEngine.", StringComparison.Ordinal))
                {
                    result.Add((name, metadata.GetString(type.Name)));
                }
            }
        }

        return result;
    }

    private static string ShortName(string componentType) => componentType.StartsWith("MNVTEngine.", StringComparison.Ordinal) ? componentType["MNVTEngine.".Length..] : componentType;

    [GeneratedRegex("\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"\baudio\.(play|stop|set_volume|mute)\s*\(")]
    private static partial Regex MissionNativeModule();
}
