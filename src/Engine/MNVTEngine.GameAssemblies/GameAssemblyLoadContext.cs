using System.Reflection;
using System.Runtime.Loader;

namespace MNVTEngine.GameAssemblies;

/// <summary>
/// Collectible load context for one generation of game code. Game assemblies are loaded from bytes, so the files on
/// disk stay writable for the next build. Any assembly the host provides (engine, framework, shared packages) always
/// resolves to the host's copy: game code must see the same <see cref="Component"/> type as the engine, and must not
/// replace engine assemblies with its own versions.
/// </summary>
public sealed class GameAssemblyLoadContext(string name, string directory) : AssemblyLoadContext(name, isCollectible: true)
{
    private static readonly Lazy<HashSet<string>> PlatformAssemblies = new(() =>
        [.. ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()]);

    public string Directory { get; } = directory;

    /// <summary>True if the host owns an assembly with this name (already loaded, part of the framework, or next to the host).</summary>
    public static bool IsSharedWithHost(AssemblyName assemblyName)
    {
        ArgumentNullException.ThrowIfNull(assemblyName);
        var simpleName = assemblyName.Name;
        return simpleName is null
            || Default.Assemblies.Any(a => string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
            || PlatformAssemblies.Value.Contains(simpleName)
            || File.Exists(Path.Combine(AppContext.BaseDirectory, simpleName + ".dll"));
    }

    /// <summary>Loads an assembly (and its .pdb, for stack traces with line numbers) without locking the files.</summary>
    public Assembly LoadFromFileBytes(string path)
    {
        using var assembly = new MemoryStream(File.ReadAllBytes(path));
        var pdbPath = Path.ChangeExtension(path, ".pdb");
        if (!File.Exists(pdbPath))
        {
            return LoadFromStream(assembly);
        }

        using var symbols = new MemoryStream(File.ReadAllBytes(pdbPath));
        return LoadFromStream(assembly, symbols);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsSharedWithHost(assemblyName))
        {
            return null; // fall back to the default context
        }

        var candidate = Path.Combine(Directory, assemblyName.Name + ".dll");
        return File.Exists(candidate) ? LoadFromFileBytes(candidate) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var candidate = Path.Combine(Directory, unmanagedDllName);
        return File.Exists(candidate) ? LoadUnmanagedDllFromPath(candidate) : 0;
    }
}
