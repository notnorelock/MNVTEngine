namespace MNVTEngine.IO;

/// <summary>A file seen through the virtual file system: its canonical path, size and, for packaged assets, its GUID.</summary>
public readonly record struct VirtualFileInfo(string Path, long Length, Guid AssetGuid)
{
    public bool HasAssetGuid => AssetGuid != Guid.Empty;
}

/// <summary>
/// Read access to game content by virtual path (<see cref="VirtualPath"/>), wherever it lives: packages, folders or
/// memory. Implementations are thread-safe. Streams are read-only and owned by the caller.
/// </summary>
public interface IVirtualFileSystem
{
    /// <exception cref="FileNotFoundException">No mounted source has the file.</exception>
    /// <exception cref="ArgumentException">Invalid or escaping path.</exception>
    /// <exception cref="PackageException">The file's package data is damaged, tampered with or cannot be decrypted.</exception>
    Stream OpenRead(string path);

    /// <inheritdoc cref="OpenRead"/>
    ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>False for invalid paths.</summary>
    bool FileExists(string path);

    bool DirectoryExists(string path);

    bool TryGetFile(string path, out VirtualFileInfo file);

    /// <summary>Files under <paramref name="directory"/> ("" for all), canonical paths, each once (the highest-priority source wins), sorted.</summary>
    IEnumerable<string> EnumerateFiles(string directory, bool recursive = true);

    /// <summary>The virtual path of a packaged asset by its GUID; null when no mounted package has it.</summary>
    string? FindAsset(Guid assetGuid);
}

/// <summary>How far a mounted source is trusted.</summary>
public enum MountTrust
{
    /// <summary>The game's own content: everything it holds is served.</summary>
    Trusted,

    /// <summary>
    /// External content (mods, user content packs): never serves executable code (paths under <see cref="MountOptions.CodeFolders"/>),
    /// so loading game code from the file system cannot pick up anything an untrusted package provides.
    /// </summary>
    Untrusted,
}

/// <param name="MountPoint">Virtual folder the source appears under ("" for the root).</param>
/// <param name="Priority">Higher wins when several sources have the same path; equal priorities: the later mount wins.</param>
/// <param name="Name">Shown in diagnostics; defaults to the provider's name.</param>
public sealed record MountOptions(string MountPoint = "", int Priority = 0, MountTrust Trust = MountTrust.Trusted, string? Name = null)
{
    /// <summary>Virtual folders with executable code (the game's C# assemblies), hidden in untrusted mounts.</summary>
    public static IReadOnlyList<string> CodeFolders { get; } = ["Scripts"];
}

/// <summary>A source the file system has mounted. File count and size are null where unknown without a scan (folders).</summary>
public sealed record MountInfo(string Name, string MountPoint, int Priority, MountTrust Trust, string Kind, int? FileCount, long? Bytes);
