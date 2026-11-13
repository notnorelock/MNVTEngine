using System.Collections.Concurrent;

namespace MNVTEngine.IO;

/// <summary>Files held in memory (tests, generated content, development overrides). Thread-safe; files can be added while mounted.</summary>
public sealed class MemoryFileSystemProvider(string name = "memory") : IFileSystemProvider
{
    private readonly ConcurrentDictionary<string, (byte[] Data, Guid Guid)> _files = new(VirtualPath.Comparer);

    public string Name { get; } = name;

    public string Kind => "memory";

    public (int Files, long Bytes)? Size => (_files.Count, _files.Values.Sum(f => (long)f.Data.Length));

    /// <summary>Adds or replaces a file; the bytes are copied.</summary>
    /// <exception cref="ArgumentException">Invalid path.</exception>
    public void Add(string path, ReadOnlySpan<byte> data, Guid assetGuid = default)
    {
        var canonical = VirtualPath.Normalize(path);
        if (canonical.Length == 0)
        {
            throw new ArgumentException("A file needs a name.", nameof(path));
        }

        _files[canonical] = (data.ToArray(), assetGuid);
    }

    public bool Remove(string path) => VirtualPath.TryNormalize(path, out var canonical, out _) && _files.TryRemove(canonical, out _);

    public bool TryGetFile(string path, out VirtualFileInfo file)
    {
        file = default;
        if (!_files.TryGetValue(path, out var entry))
        {
            return false;
        }

        file = new VirtualFileInfo(path, entry.Data.Length, entry.Guid);
        return true;
    }

    public Stream OpenRead(string path) =>
        _files.TryGetValue(path, out var entry) ? new MemoryStream(entry.Data, writable: false) : throw new FileNotFoundException($"'{path}' is not in {Name}.", path);

    public bool DirectoryExists(string path) => _files.Keys.Any(f => f.Length > path.Length && VirtualPath.IsUnder(f, path));

    public IEnumerable<string> EnumerateFiles() => [.. _files.Keys];

    public string? FindAsset(Guid assetGuid) => assetGuid == Guid.Empty ? null : _files.FirstOrDefault(f => f.Value.Guid == assetGuid).Key;

    /// <summary>Nothing to release; the files stay readable by streams already open.</summary>
    public void Dispose()
    {
    }
}
