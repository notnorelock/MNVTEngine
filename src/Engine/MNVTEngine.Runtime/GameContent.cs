using MNVTEngine.IO;
using MNVTEngine.IO.Packages;

namespace MNVTEngine.Runtime;

/// <summary>
/// The content of an exported game: its official packages, mounted in a <see cref="VirtualFileSystem"/> as trusted
/// sources. Every file must live in exactly one official package; opening fails on duplicates, so no package can
/// silently shadow another. Further content (DLC, mods) can be mounted on <see cref="Files"/> with its own priority and trust.
/// </summary>
public sealed class GameContent : IDisposable
{
    private readonly List<MnvtPackage> _packages;

    private GameContent(string directory, VirtualFileSystem files, List<MnvtPackage> packages)
    {
        Directory = directory;
        Files = files;
        _packages = packages;
    }

    /// <summary>Folder the packages were opened from (the game's data folder).</summary>
    public string Directory { get; }

    public VirtualFileSystem Files { get; }

    /// <summary>The official packages, in mount order.</summary>
    public IReadOnlyList<MnvtPackage> Packages => _packages;

    /// <summary>Every file of every package.</summary>
    public IEnumerable<string> Entries => Files.EnumerateFiles("");

    /// <summary>Opens <paramref name="files"/> (names inside <paramref name="directory"/>); all or nothing.</summary>
    /// <exception cref="PackageException">A package is damaged, has the wrong key or signature, or two packages hold the same file.</exception>
    /// <exception cref="IOException">A package file is missing or unreadable.</exception>
    public static GameContent Open(string directory, IEnumerable<string> files, PackageOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(options);
        var vfs = new VirtualFileSystem();
        var packages = new List<MnvtPackage>();
        var owners = new Dictionary<string, string>(VirtualPath.Comparer);
        try
        {
            foreach (var file in files)
            {
                if (Path.GetFileName(file) != file || !file.EndsWith(GamePackage.PackageExtension, StringComparison.OrdinalIgnoreCase))
                {
                    throw new PackageException(PackageError.Malformed, $"'{file}' is not a package file name.");
                }

                var package = MnvtPackage.Open(Path.Combine(directory, file), options with { Statistics = vfs.Statistics });
                vfs.Mount(new PackageFileSystemProvider(package), new MountOptions(Name: file));
                packages.Add(package);
                foreach (var entry in package.Entries)
                {
                    if (!owners.TryAdd(entry.Path, file))
                    {
                        throw new PackageException(PackageError.Malformed, $"'{entry.Path}' is in both {owners[entry.Path]} and {file}.");
                    }
                }
            }

            return packages.Count > 0 ? new GameContent(directory, vfs, packages) : throw new PackageException(PackageError.Malformed, "The game lists no content packages.");
        }
        catch
        {
            vfs.Dispose();
            throw;
        }
    }

    public bool Contains(string path) => Files.FileExists(path);

    /// <summary>File name of the package holding <paramref name="path"/>, or null.</summary>
    public string? PackageOf(string path) => Files.SourceOf(path);

    /// <exception cref="FileNotFoundException">No package holds the file.</exception>
    /// <exception cref="PackageException">The file's data was modified or is damaged.</exception>
    public byte[] Read(string path) => Files.ReadAllBytes(path);

    /// <exception cref="PackageException">The file's data was modified or is damaged.</exception>
    public byte[]? TryRead(string path) => Files.TryReadAllBytes(path);

    public void Dispose() => Files.Dispose();
}
