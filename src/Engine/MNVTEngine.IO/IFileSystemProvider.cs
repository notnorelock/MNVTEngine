namespace MNVTEngine.IO;

/// <summary>
/// One read-only source of files for <see cref="VirtualFileSystem"/>. Paths given to a provider are canonical
/// (<see cref="VirtualPath"/>) and relative to its mount point. Implementations are thread-safe; the file system
/// disposes a provider when it is unmounted.
/// </summary>
public interface IFileSystemProvider : IDisposable
{
    /// <summary>Short display name ("GameData.mnvtpack", "Assets folder").</summary>
    string Name { get; }

    /// <summary>"package", "folder" or "memory": for diagnostics.</summary>
    string Kind { get; }

    bool TryGetFile(string path, out VirtualFileInfo file);

    /// <exception cref="FileNotFoundException">No such file.</exception>
    Stream OpenRead(string path);

    bool DirectoryExists(string path);

    /// <summary>Every file of the source, canonical paths.</summary>
    IEnumerable<string> EnumerateFiles();

    /// <summary>The path of a packaged asset by GUID; null when the source has no such asset (folders never do).</summary>
    string? FindAsset(Guid assetGuid);

    /// <summary>Number of files and their total size, for diagnostics; null when unknown without a scan (folders).</summary>
    (int Files, long Bytes)? Size { get; }
}
