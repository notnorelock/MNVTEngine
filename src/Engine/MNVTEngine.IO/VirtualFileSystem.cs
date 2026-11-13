using MNVTEngine.IO.Packages;

namespace MNVTEngine.IO;

/// <summary>
/// The engine's file system for game content: an ordered set of mounted sources (packages, folders, memory), looked up
/// by virtual path, highest priority first. Mounting and unmounting are safe while other threads read: lookups work on
/// an immutable snapshot of the mount table, and a source is disposed only after its last open stream is closed.
/// <para>
/// The file system owns what is mounted: <see cref="Unmount"/> and <see cref="Dispose"/> dispose the providers.
/// Package internals (offsets, chunks, keys) never leave the package provider.
/// </para>
/// </summary>
public sealed class VirtualFileSystem : IVirtualFileSystem, IDisposable
{
    private readonly Lock _lock = new();
    private MountEntry[] _mounts = [];
    private long _sequence;
    private bool _disposed;

    public IoStatistics Statistics { get; } = new();

    /// <summary>The mounted sources, highest priority first.</summary>
    public IReadOnlyList<MountInfo> Mounts => [.. Volatile.Read(ref _mounts).Select(m => m.Describe())];

    /// <summary>Mounts <paramref name="provider"/>; the file system owns it from now on.</summary>
    /// <returns>A handle for <see cref="Unmount"/>.</returns>
    /// <exception cref="ArgumentException">Invalid mount point.</exception>
    public MountHandle Mount(IFileSystemProvider provider, MountOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        options ??= new MountOptions();
        var mount = new MountEntry(provider, VirtualPath.Normalize(options.MountPoint), options.Priority, options.Trust, options.Name ?? provider.Name, Interlocked.Increment(ref _sequence));
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Highest priority first; among equals, the most recent mount first.
            _mounts = [.. _mounts.Append(mount).OrderByDescending(m => m.Priority).ThenByDescending(m => m.Sequence)];
        }

        return new MountHandle(mount.Sequence, mount.Name);
    }

    /// <summary>Opens a <c>.mnvtpack</c> file and mounts it.</summary>
    /// <exception cref="PackageException">Not a package, unsupported version, damaged, wrong key or failed signature.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public MountHandle MountPackage(string file, PackageOpenOptions? packageOptions = null, MountOptions? options = null)
    {
        var package = MnvtPackage.Open(file, (packageOptions ?? new PackageOpenOptions()) with { Statistics = Statistics });
        try
        {
            return Mount(new PackageFileSystemProvider(package), options);
        }
        catch
        {
            package.Dispose();
            throw;
        }
    }

    /// <summary>Removes a source; it is disposed once streams opened from it are closed. False when it was not mounted.</summary>
    public bool Unmount(MountHandle handle)
    {
        MountEntry? removed;
        lock (_lock)
        {
            removed = _mounts.FirstOrDefault(m => m.Sequence == handle.Id);
            if (removed is null)
            {
                return false;
            }

            _mounts = [.. _mounts.Where(m => m != removed)];
        }

        removed.Provider.Dispose();
        return true;
    }

    /// <summary>Removes every source with this display name (e.g. "GameData.mnvtpack"). Returns how many were removed.</summary>
    public int Unmount(string name)
    {
        var handles = Volatile.Read(ref _mounts).Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)).Select(m => new MountHandle(m.Sequence, m.Name)).ToList();
        return handles.Count(Unmount);
    }

    public Stream OpenRead(string path)
    {
        Statistics.Opened();
        while (true)
        {
            var (mount, relative, canonical) = Locate(path);
            try
            {
                return mount.Provider.OpenRead(relative);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or FileNotFoundException && !Volatile.Read(ref _mounts).Contains(mount))
            {
                // Unmounted between the lookup and the open: look up again in the current mount table.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Statistics.ReadFailed();
                throw ex is FileNotFoundException ? new FileNotFoundException($"'{canonical}' is not in the game content.", canonical, ex) : ex;
            }
        }
    }

    /// <summary>Opening is cheap (nothing is read until the stream is); the stream's ReadAsync reads asynchronously.</summary>
    public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(OpenRead(path));
    }

    /// <summary>The whole file.</summary>
    /// <exception cref="FileNotFoundException">No mounted source has the file.</exception>
    /// <exception cref="PackageException">Damaged, tampered or undecryptable package data.</exception>
    public byte[] ReadAllBytes(string path)
    {
        using var stream = OpenRead(path);
        return ReadToEnd(stream, path);
    }

    /// <summary>The whole file, or null when no mounted source has it.</summary>
    /// <exception cref="PackageException">Damaged, tampered or undecryptable package data.</exception>
    public byte[]? TryReadAllBytes(string path) => FileExists(path) ? ReadAllBytes(path) : null;

    /// <summary>Reads the whole file without blocking the caller: I/O is asynchronous, verification and decoding run on the thread pool.</summary>
    public async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        Statistics.ReadStarted();
        try
        {
            var stream = await OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var result = new byte[stream.Length];
                await stream.ReadExactlyAsync(result, cancellationToken).ConfigureAwait(false);
                return result;
            }
        }
        finally
        {
            Statistics.ReadFinished();
        }
    }

    public bool FileExists(string path) => TryGetFile(path, out _);

    public bool TryGetFile(string path, out VirtualFileInfo file)
    {
        file = default;
        if (!VirtualPath.TryNormalize(path, out var canonical, out _) || canonical.Length == 0)
        {
            return false;
        }

        foreach (var mount in Volatile.Read(ref _mounts))
        {
            if (mount.TryRelative(canonical) is { } relative && mount.Provider.TryGetFile(relative, out var found))
            {
                file = found with { Path = canonical };
                return true;
            }
        }

        return false;
    }

    public bool DirectoryExists(string path)
    {
        if (!VirtualPath.TryNormalize(path, out var canonical, out _))
        {
            return false;
        }

        foreach (var mount in Volatile.Read(ref _mounts))
        {
            // A mount point is a folder, as is every folder above it.
            if (VirtualPath.IsUnder(mount.MountPoint, canonical) || (mount.TryRelative(canonical) is { } relative && (relative.Length == 0 || mount.Provider.DirectoryExists(relative))))
            {
                return true;
            }
        }

        return false;
    }

    public IEnumerable<string> EnumerateFiles(string directory, bool recursive = true)
    {
        var folder = VirtualPath.Normalize(directory);
        var result = new SortedSet<string>(VirtualPath.Comparer);
        foreach (var mount in Volatile.Read(ref _mounts))
        {
            foreach (var file in mount.Provider.EnumerateFiles())
            {
                if (!mount.Serves(file))
                {
                    continue;
                }

                var full = mount.MountPoint.Length == 0 ? file : mount.MountPoint + "/" + file;
                if (VirtualPath.RelativeTo(full, folder) is { Length: > 0 } inside && (recursive || !inside.Contains('/', StringComparison.Ordinal)))
                {
                    result.Add(full);
                }
            }
        }

        return result;
    }

    public string? FindAsset(Guid assetGuid)
    {
        foreach (var mount in Volatile.Read(ref _mounts))
        {
            if (mount.Provider.FindAsset(assetGuid) is { } path && mount.Serves(path))
            {
                var full = mount.MountPoint.Length == 0 ? path : mount.MountPoint + "/" + path;
                return TryGetFile(full, out var winner) && winner.AssetGuid == assetGuid ? full : null; // shadowed by a higher-priority file
            }
        }

        return null;
    }

    /// <summary>Display name of the source that serves <paramref name="path"/>; null when none does. For diagnostics and tools.</summary>
    public string? SourceOf(string path)
    {
        if (!VirtualPath.TryNormalize(path, out var canonical, out _))
        {
            return null;
        }

        return Volatile.Read(ref _mounts).FirstOrDefault(m => m.TryRelative(canonical) is { } relative && m.Provider.TryGetFile(relative, out _))?.Name;
    }

    public void Dispose()
    {
        MountEntry[] mounts;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            mounts = _mounts;
            _mounts = [];
        }

        foreach (var mount in mounts)
        {
            mount.Provider.Dispose();
        }
    }

    private (MountEntry Mount, string Relative, string Canonical) Locate(string path)
    {
        var canonical = VirtualPath.Normalize(path);
        foreach (var mount in Volatile.Read(ref _mounts))
        {
            if (mount.TryRelative(canonical) is { } relative && mount.Provider.TryGetFile(relative, out _))
            {
                return (mount, relative, canonical);
            }
        }

        Statistics.ReadFailed();
        throw new FileNotFoundException($"'{canonical}' is not in the game content.", canonical);
    }

    private static byte[] ReadToEnd(Stream stream, string path)
    {
        if (stream.Length > Array.MaxLength)
        {
            throw new IOException($"'{path}' is too large to read into memory; stream it with OpenRead.");
        }

        var result = new byte[stream.Length];
        stream.ReadExactly(result);
        return result;
    }

    private sealed record MountEntry(IFileSystemProvider Provider, string MountPoint, int Priority, MountTrust Trust, string Name, long Sequence)
    {
        /// <summary>The provider-relative path of a canonical virtual path, if this mount covers and serves it.</summary>
        public string? TryRelative(string canonical) => VirtualPath.RelativeTo(canonical, MountPoint) is { } relative && Serves(relative) ? relative : null;

        /// <summary>Untrusted mounts never serve code.</summary>
        public bool Serves(string relative)
        {
            if (Trust == MountTrust.Trusted)
            {
                return true;
            }

            var full = MountPoint.Length == 0 ? relative : MountPoint + "/" + relative;
            return !MountOptions.CodeFolders.Any(code => VirtualPath.IsUnder(full, code));
        }

        public MountInfo Describe() => new(Name, MountPoint, Priority, Trust, Provider.Kind, Provider.Size?.Files, Provider.Size?.Bytes);
    }
}

/// <summary>Identifies one mount of a <see cref="VirtualFileSystem"/>.</summary>
public readonly record struct MountHandle(long Id, string Name);
