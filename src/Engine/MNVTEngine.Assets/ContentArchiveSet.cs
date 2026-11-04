namespace MNVTEngine.Assets;

/// <summary>
/// The content of an exported game, split over several <see cref="ContentArchive"/>s (globals, default resources, one per
/// level, shared assets, resources). Reads are routed by entry path to the archive that holds it. Every entry must
/// live in exactly one archive; opening fails on duplicates, so no archive can silently shadow another.
/// </summary>
public sealed class ContentArchiveSet : IDisposable
{
    private readonly List<ContentArchive> _archives;
    private readonly Dictionary<string, ContentArchive> _byEntry = new(StringComparer.OrdinalIgnoreCase);

    private ContentArchiveSet(string directory, List<ContentArchive> archives)
    {
        Directory = directory;
        _archives = archives;
        foreach (var archive in archives)
        {
            foreach (var entry in archive.Entries)
            {
                if (!_byEntry.TryAdd(entry, archive))
                {
                    throw new InvalidDataException(
                        $"Content entry '{entry}' is in both {Path.GetFileName(_byEntry[entry].FilePath)} and {Path.GetFileName(archive.FilePath)}.");
                }
            }
        }
    }

    /// <summary>Folder the archives were opened from (the game's data folder).</summary>
    public string Directory { get; }

    public IReadOnlyList<ContentArchive> Archives => _archives;

    /// <summary>Every entry of every archive, archive by archive in open order.</summary>
    public IEnumerable<string> Entries => _archives.SelectMany(a => a.Entries);

    /// <summary>Opens <paramref name="files"/> (names inside <paramref name="directory"/>) with one key; all or nothing.</summary>
    /// <exception cref="InvalidDataException">An archive is damaged, has the wrong key, or two archives hold the same entry.</exception>
    /// <exception cref="IOException">An archive file is missing or unreadable.</exception>
    public static ContentArchiveSet Open(string directory, IEnumerable<string> files, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(files);
        var archives = new List<ContentArchive>();
        try
        {
            foreach (var file in files)
            {
                if (Path.GetFileName(file) != file || !file.EndsWith(ContentArchive.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"'{file}' is not a content archive name.");
                }

                archives.Add(ContentArchive.Open(Path.Combine(directory, file), key));
            }

            if (archives.Count == 0)
            {
                throw new InvalidDataException("The game lists no content archives.");
            }

            return new ContentArchiveSet(directory, archives);
        }
        catch
        {
            archives.ForEach(a => a.Dispose());
            throw;
        }
    }

    public bool Contains(string path) => _byEntry.ContainsKey(ContentArchive.Normalize(path));

    /// <summary>File name of the archive holding <paramref name="path"/>, or null.</summary>
    public string? ArchiveOf(string path) =>
        _byEntry.TryGetValue(ContentArchive.Normalize(path), out var archive) ? Path.GetFileName(archive.FilePath) : null;

    /// <exception cref="FileNotFoundException">No archive holds the entry.</exception>
    /// <exception cref="InvalidDataException">The entry was modified or damaged.</exception>
    public byte[] Read(string path) =>
        TryRead(path) ?? throw new FileNotFoundException($"'{path}' is not in the game content.", path);

    /// <exception cref="InvalidDataException">The entry was modified or damaged.</exception>
    public byte[]? TryRead(string path) =>
        _byEntry.TryGetValue(ContentArchive.Normalize(path), out var archive) ? archive.TryRead(path) : null;

    public void Dispose()
    {
        foreach (var archive in _archives)
        {
            archive.Dispose();
        }
    }
}
