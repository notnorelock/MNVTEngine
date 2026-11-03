using System.Reflection;
using System.Runtime.Loader;

namespace MNVTEngine.GameAssemblies;

/// <summary>
/// Collectible load context for one generation of game code. Game assemblies are loaded from bytes, so the files on
/// disk stay writable for the next build; in exported games the bytes come from the encrypted content archive. Any
/// assembly the host provides (engine, framework, shared packages) always resolves to the host's copy: game code must
/// see the same <see cref="Component"/> type as the engine, and must not replace engine assemblies with its own versions.
/// </summary>
public sealed class GameAssemblyLoadContext : AssemblyLoadContext
{
    /// <summary>Where exported games keep engine and package assemblies (same as <c>GamePackage.ManagedFolder</c>).</summary>
    public const string ManagedDependenciesFolder = "Dependencies/Managed";

    private static readonly Lazy<HashSet<string>> PlatformAssemblies = new(() =>
        [.. ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()]);

    private readonly Func<string, byte[]?> _readFile;

    /// <summary>Game code in a folder (development builds).</summary>
    public GameAssemblyLoadContext(string name, string directory)
        : this(name, file => File.Exists(Path.Combine(directory, file)) ? File.ReadAllBytes(Path.Combine(directory, file)) : null) =>
        Directory = directory;

    /// <summary>Game code from any source, e.g. a content archive. <paramref name="readFile"/> returns a file's bytes by file name, or null.</summary>
    public GameAssemblyLoadContext(string name, Func<string, byte[]?> readFile)
        : base(name, isCollectible: true) => _readFile = readFile;

    /// <summary>The folder game code comes from; null when it comes from an archive.</summary>
    public string? Directory { get; }

    /// <summary>True if the host owns an assembly with this name (already loaded, part of the framework, or shipped with the host).</summary>
    public static bool IsSharedWithHost(AssemblyName assemblyName)
    {
        ArgumentNullException.ThrowIfNull(assemblyName);
        var simpleName = assemblyName.Name;
        return simpleName is null
            || Default.Assemblies.Any(a => string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
            || PlatformAssemblies.Value.Contains(simpleName)
            || File.Exists(Path.Combine(AppContext.BaseDirectory, simpleName + ".dll"))
            || File.Exists(Path.Combine(AppContext.BaseDirectory, ManagedDependenciesFolder, simpleName + ".dll"));
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

    /// <summary>Loads an assembly (and its .pdb if present) by file name from this context's source.</summary>
    /// <exception cref="FileNotFoundException">The source has no such file.</exception>
    public Assembly LoadByFileName(string fileName)
    {
        var bytes = _readFile(fileName) ?? throw new FileNotFoundException($"Game code '{fileName}' not found.", fileName);
        using var assembly = new MemoryStream(bytes);
        if (_readFile(Path.ChangeExtension(fileName, ".pdb")) is not { } pdb)
        {
            return LoadFromStream(assembly);
        }

        using var symbols = new MemoryStream(pdb);
        return LoadFromStream(assembly, symbols);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsSharedWithHost(assemblyName))
        {
            return null; // fall back to the default context
        }

        var fileName = assemblyName.Name + ".dll";
        return _readFile(fileName) is not null ? LoadByFileName(fileName) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        // shortcut: native libraries of game code only load from a folder; archived game code cannot bring its own. Add archive extraction when a game needs one.
        if (Directory is null)
        {
            return 0;
        }

        var candidate = Path.Combine(Directory, unmanagedDllName);
        return File.Exists(candidate) ? LoadUnmanagedDllFromPath(candidate) : 0;
    }
}
