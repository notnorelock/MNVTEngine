namespace MNVTEngine.IO;

/// <summary>
/// A folder on disk (development: a project's Assets folder, its imported artifacts). Every path is resolved inside
/// the root and checked again after resolution, so no virtual path reaches outside it.
/// Hidden entries (names starting with '.') and <paramref name="excludedExtensions"/> (e.g. ".meta") are not served.
/// </summary>
public sealed class PhysicalFileSystemProvider(string root, string? name = null, IReadOnlyCollection<string>? excludedExtensions = null) : IFileSystemProvider
{
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    private readonly HashSet<string> _excluded = new(excludedExtensions ?? [], StringComparer.OrdinalIgnoreCase);

    public string Root => _root;

    public string Name { get; } = name ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));

    public string Kind => "folder";

    public (int Files, long Bytes)? Size => null;

    public bool TryGetFile(string path, out VirtualFileInfo file)
    {
        file = default;
        if (Resolve(path) is not { } full)
        {
            return false;
        }

        var info = new FileInfo(full);
        if (!info.Exists)
        {
            return false;
        }

        file = new VirtualFileInfo(path, info.Length, Guid.Empty);
        return true;
    }

    public Stream OpenRead(string path)
    {
        var full = Resolve(path) ?? throw new FileNotFoundException($"'{path}' is not in {Name}.", path);
        return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
    }

    public bool DirectoryExists(string path) => Resolve(path) is { } full && Directory.Exists(full);

    public IEnumerable<string> EnumerateFiles()
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        return Directory.EnumerateFiles(_root, "*", new EnumerationOptions { RecurseSubdirectories = true })
            .Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/'))
            .Where(Visible);
    }

    public string? FindAsset(Guid assetGuid) => null;

    public void Dispose()
    {
    }

    /// <summary>The full path of a canonical relative path inside the root; null when invalid, hidden, excluded or outside.</summary>
    private string? Resolve(string path)
    {
        if (!VirtualPath.TryNormalize(path, out var canonical, out _) || canonical.Length == 0 || !Visible(canonical))
        {
            return null;
        }

        // shortcut: links the developer placed inside the folder are followed (development content is the developer's own); packages have none.
        var full = Path.GetFullPath(Path.Join(_root, canonical));
        return full.StartsWith(_root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? full : null;
    }

    private bool Visible(string relative) =>
        !relative.Split('/').Any(s => s.StartsWith('.')) && !_excluded.Contains(Path.GetExtension(relative));
}
