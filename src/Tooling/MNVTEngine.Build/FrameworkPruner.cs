using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Build;

/// <summary>What <see cref="FrameworkPruner"/> removed from a game's private .NET runtime.</summary>
public sealed record FrameworkPruning(IReadOnlyList<string> Removed, long RemovedBytes, int Kept);

/// <summary>
/// Removes the parts of a game's private .NET runtime it cannot reach. A framework assembly stays when the host, a
/// shipped dependency or the game's C# code references it, directly or through other assemblies (assembly metadata,
/// no IL trimming: every kept assembly is the unmodified runtime-pack file). The runtime itself (VM, JIT, host policy,
/// resources), crash-dump support (createdump, DAC) and the assemblies CoreLib loads by name stay. Natives go only
/// when the assembly that calls them went, or when nothing can load them (the alternative GCs, selected only by
/// <c>DOTNET_GCName</c>). The framework's deps.json is rewritten to list exactly what remains, as the host requires.
/// Verification: the validator checks that every reference resolves (MNVB505) and every deps.json entry exists
/// (MNVB504), and <c>--run</c> starts the game. Code that loads framework assemblies by name at run time is not seen:
/// set <c>"pruneFramework": false</c> in the build profile for such games.
/// </summary>
public static class FrameworkPruner
{
    public const string DepsFile = "Microsoft.NETCore.App.deps.json";

    /// <summary>Assemblies the runtime loads by name rather than through a reference (stack traces with line numbers).</summary>
    private static readonly string[] LoadedByName = ["System.Private.CoreLib", "System.Runtime", "System.Diagnostics.StackTrace", "System.Private.Uri"];

    /// <summary>Natives that serve one framework assembly: removed with it.</summary>
    private static readonly Dictionary<string, string> NativeOwners = new(StringComparer.OrdinalIgnoreCase)
    {
        ["System.IO.Compression.Native.dll"] = "System.IO.Compression",
        ["msquic.dll"] = "System.Net.Quic",
    };

    /// <summary>Alternative garbage collectors: loaded only when DOTNET_GCName selects one.</summary>
    private static readonly string[] Unloadable = ["clrgc.dll", "clrgcexp.dll"];

    /// <param name="framework">The shared/Microsoft.NETCore.App/&lt;version&gt; folder of the private runtime.</param>
    /// <param name="roots">Assemblies that ship with the game: the host, Dependencies/Managed and the game's C# code.</param>
    public static FrameworkPruning Prune(string framework, IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var managed = Directory.EnumerateFiles(framework, "*.dll").Where(IsManaged)
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase);
        var facades = new Dictionary<string, Dictionary<string, string>?>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? ForwardsOf(string name)
        {
            if (!facades.TryGetValue(name, out var forwards))
            {
                facades[name] = forwards = managed.TryGetValue(name, out var file) ? Forwards(file) : null;
            }

            return forwards;
        }

        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(LoadedByName.Concat(roots.Where(File.Exists).SelectMany(r => Needs(r, ForwardsOf))));
        while (pending.Count > 0)
        {
            var name = pending.Pop();
            if (managed.TryGetValue(name, out var file) && kept.Add(name) && ForwardsOf(name) is null)
            {
                // A facade's own references are not followed: only the forwarded types its users name count (Needs).
                foreach (var reference in Needs(file, ForwardsOf))
                {
                    pending.Push(reference);
                }
            }
        }

        var removed = managed.Where(p => !kept.Contains(p.Key)).Select(p => p.Value)
            .Concat(Directory.EnumerateFiles(framework).Where(f =>
            {
                var name = Path.GetFileName(f);
                return Unloadable.Contains(name, StringComparer.OrdinalIgnoreCase) || (NativeOwners.TryGetValue(name, out var owner) && !kept.Contains(owner));
            }))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        long bytes = 0;
        foreach (var file in removed)
        {
            bytes += new FileInfo(file).Length;
            File.Delete(file);
        }

        var names = removed.Select(f => Path.GetFileName(f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        RewriteDeps(Path.Combine(framework, DepsFile), names);
        return new FrameworkPruning([.. removed.Select(f => Path.GetFileName(f))], bytes, kept.Count);
    }

    /// <summary>Files deps.json lists that are missing from the folder (the host refuses to start with any).</summary>
    public static IReadOnlyList<string> MissingDeps(string framework)
    {
        var deps = Path.Combine(framework, DepsFile);
        return !File.Exists(deps) ? [] : [.. Entries(JObject.Parse(File.ReadAllText(deps))).Select(e => e.Name).Where(n => !File.Exists(Path.Combine(framework, Path.GetFileName(n))))];
    }

    private static void RewriteDeps(string deps, HashSet<string> removed)
    {
        if (!File.Exists(deps))
        {
            return;
        }

        var document = JObject.Parse(File.ReadAllText(deps));
        foreach (var entry in Entries(document).Where(e => removed.Contains(Path.GetFileName(e.Name))).ToList())
        {
            entry.Remove();
        }

        File.WriteAllText(deps, document.ToString(Formatting.Indented));
    }

    /// <summary>Every file entry of deps.json: targets / &lt;target&gt; / &lt;library&gt; / runtime | native / &lt;file&gt;.</summary>
    private static IEnumerable<JProperty> Entries(JObject deps) =>
        (deps["targets"] as JObject)?.Properties().Select(t => t.Value).OfType<JObject>()
            .SelectMany(t => t.Properties().Select(l => l.Value).OfType<JObject>())
            .SelectMany(l => new[] { l["runtime"], l["native"] }.OfType<JObject>())
            .SelectMany(files => files.Properties()) ?? [];

    private static bool IsManaged(string file)
    {
        using var pe = new PEReader(File.OpenRead(file));
        return pe.HasMetadata;
    }

    /// <summary>
    /// The assemblies an assembly needs: its references, except that through a facade (an assembly that only forwards
    /// types, such as netstandard or mscorlib, which reference most of the framework) only the assemblies of the
    /// forwarded types it names count, besides the facade itself.
    /// </summary>
    private static List<string> Needs(string assembly, Func<string, Dictionary<string, string>?> forwardsOf)
    {
        using var pe = new PEReader(File.OpenRead(assembly));
        if (!pe.HasMetadata)
        {
            return [];
        }

        var metadata = pe.GetMetadataReader();
        var references = metadata.AssemblyReferences.ToDictionary(h => h, h => metadata.GetString(metadata.GetAssemblyReference(h).Name));
        var needs = references.Values.ToList();
        if (!references.Values.Any(r => forwardsOf(r) is not null))
        {
            return needs;
        }

        foreach (var handle in metadata.TypeReferences)
        {
            var type = metadata.GetTypeReference(handle);
            while (type.ResolutionScope.Kind == HandleKind.TypeReference)
            {
                type = metadata.GetTypeReference((TypeReferenceHandle)type.ResolutionScope); // nested: the outermost type is forwarded
            }

            if (type.ResolutionScope.Kind != HandleKind.AssemblyReference)
            {
                continue;
            }

            var name = metadata.GetString(type.Namespace) is { Length: > 0 } ns ? ns + "." + metadata.GetString(type.Name) : metadata.GetString(type.Name);
            var scope = references[(AssemblyReferenceHandle)type.ResolutionScope];
            for (var hops = 0; hops < 8 && forwardsOf(scope) is { } forwards && forwards.TryGetValue(name, out var target); hops++)
            {
                needs.Add(target); // a facade may forward to another facade
                scope = target;
            }
        }

        return needs;
    }

    /// <summary>Type name to target assembly, for a facade (no types of its own, only forwards); null for other assemblies.</summary>
    private static Dictionary<string, string>? Forwards(string assembly)
    {
        using var pe = new PEReader(File.OpenRead(assembly));
        var metadata = pe.GetMetadataReader();
        if (metadata.TypeDefinitions.Count > 1 || metadata.ExportedTypes.Count == 0)
        {
            return null;
        }

        var forwards = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var handle in metadata.ExportedTypes)
        {
            var exported = metadata.GetExportedType(handle);
            if (exported.Implementation.Kind == HandleKind.AssemblyReference)
            {
                var name = metadata.GetString(exported.Namespace) is { Length: > 0 } ns ? ns + "." + metadata.GetString(exported.Name) : metadata.GetString(exported.Name);
                forwards[name] = metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)exported.Implementation).Name);
            }
        }

        return forwards;
    }
}
