namespace MNVTEngine.IO.Packages;

/// <summary>Serves the files of one <see cref="MnvtPackage"/> to a <see cref="VirtualFileSystem"/>; owns the package.</summary>
public sealed class PackageFileSystemProvider : IFileSystemProvider
{
    private readonly HashSet<string> _directories = new(VirtualPath.Comparer);

    public PackageFileSystemProvider(MnvtPackage package)
    {
        Package = package ?? throw new ArgumentNullException(nameof(package));
        long bytes = 0;
        foreach (var entry in package.Entries)
        {
            bytes += entry.Length;
            for (var folder = VirtualPath.GetDirectory(entry.Path); folder.Length > 0 && _directories.Add(folder); folder = VirtualPath.GetDirectory(folder))
            {
            }
        }

        Size = (package.Count, bytes);
    }

    public MnvtPackage Package { get; }

    public string Name => Path.GetFileName(Package.FilePath);

    public string Kind => "package";

    public (int Files, long Bytes)? Size { get; }

    public bool TryGetFile(string path, out VirtualFileInfo file)
    {
        file = default;
        if (!Package.TryGetLength(path, out var length, out var guid))
        {
            return false;
        }

        file = new VirtualFileInfo(path, length, guid);
        return true;
    }

    public Stream OpenRead(string path) => Package.OpenRead(path);

    public bool DirectoryExists(string path) => _directories.Contains(path);

    public IEnumerable<string> EnumerateFiles() => Package.Entries.Select(e => e.Path);

    public string? FindAsset(Guid assetGuid) => Package.FindAsset(assetGuid);

    public void Dispose() => Package.Dispose();
}
