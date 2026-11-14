using MNVTEngine.IO;
using MNVTEngine.IO.Packages;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Runtime;

public enum ExtraContentStatus
{
    Mounted,

    /// <summary>DLC encrypted with a key the game does not have yet (a server provides it; see <see cref="GameContent.MountDlc"/>).</summary>
    WaitingForKey,

    Failed,
}

/// <summary>What happened to one extra content package; <paramref name="Mod"/> is a mod's manifest, when it has one.</summary>
public sealed record ExtraContentResult(string Name, ExtraContentStatus Status, string? Detail, ModManifest? Mod = null);

/// <summary>
/// <c>mod.json</c> at the root of a mod package: who made it and which mods it needs. <paramref name="Name"/> is what
/// other mods' <paramref name="Requires"/> refer to (the package file name without extension when there is no manifest).
/// </summary>
public sealed record ModManifest(string Name, string? Version, string? Author, string? Description, IReadOnlyList<string> Requires)
{
    public const string FileName = "mod.json";

    private const int MaxBytes = 64 * 1024;

    /// <summary>The manifest of <paramref name="package"/>, or null when it has none.</summary>
    /// <exception cref="InvalidDataException">The manifest is not valid (mod content is untrusted).</exception>
    public static ModManifest? Read(MnvtPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!package.TryGetEntry(FileName, out var entry))
        {
            return null;
        }

        if (entry.Length > MaxBytes)
        {
            throw new InvalidDataException($"{FileName} is larger than {MaxBytes / 1024} KiB.");
        }

        using var stream = package.OpenRead(FileName);
        using var reader = new JsonTextReader(new StreamReader(stream)) { MaxDepth = 8 };
        try
        {
            if (JToken.ReadFrom(reader) is not JObject json)
            {
                throw new InvalidDataException($"{FileName} is not a JSON object.");
            }

            var name = Text(json, "name") ?? throw new InvalidDataException($"{FileName} has no \"name\".");
            var requires = json["requires"] switch
            {
                null => [],
                JArray list when list.All(r => r.Type == JTokenType.String && Valid((string)r!)) => list.Select(r => (string)r!).ToArray(),
                _ => throw new InvalidDataException($"{FileName}: \"requires\" must be a list of mod names."),
            };
            return new ModManifest(name, Text(json, "version"), Text(json, "author"), Text(json, "description"), requires);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{FileName} is not valid JSON: {ex.Message}", ex);
        }

        static string? Text(JObject json, string key) => json[key] switch
        {
            null => null,
            { Type: JTokenType.String } value when Valid((string)value!) => (string)value!,
            _ => throw new InvalidDataException($"{FileName}: \"{key}\" must be a string of 1 to 256 characters."),
        };

        static bool Valid(string text) => text.Length is > 0 and <= 256;
    }
}

/// <summary>
/// The content of an exported game: its official packages, mounted in a <see cref="VirtualFileSystem"/> as trusted
/// sources. Every file must live in exactly one official package; opening fails on duplicates, so no package can
/// silently shadow another. Further content (DLC, mods) can be mounted on <see cref="Files"/> with its own priority and trust.
/// </summary>
public sealed class GameContent : IDisposable
{
    private readonly List<MnvtPackage> _packages;

    private GameContent(string directory, VirtualFileSystem files, List<MnvtPackage> packages, PackageOpenOptions options)
    {
        Directory = directory;
        Files = files;
        _packages = packages;
        _options = options;
    }

    private readonly PackageOpenOptions _options;

    /// <summary>Folder of official extra content inside the data folder.</summary>
    public const string DlcFolder = "DLC";

    /// <summary>Folder of user content (mods) next to the executable.</summary>
    public const string ModsFolder = "Mods";

    /// <summary>Priority of DLC mounts (above the game, below mods).</summary>
    public const int DlcPriority = 10;

    /// <summary>Priority of mods (above everything).</summary>
    public const int ModPriority = 100;

    /// <summary>
    /// Optional file in the Mods folder: package file names, one per line, in load order (a later mod overrides an earlier
    /// one); <c>#</c> starts a comment. With it, only the listed mods load; without it, every package loads by file name.
    /// </summary>
    public const string ModLoadOrderFile = "load-order.txt";

    /// <summary>
    /// Mounts extra content the configuration allows: <c>&lt;data&gt;/DLC/*.mnvtpack</c> (trusted: signed with the game's
    /// key, encrypted with a key the game has) and <c>&lt;game&gt;/Mods/*.mnvtpack</c> (untrusted: never serves code;
    /// unencrypted). A package that cannot be mounted is skipped and reported; DLC encrypted with a key the game does not
    /// have yet waits for <see cref="MountDlc"/> (server-provided keys).
    /// </summary>
    /// <returns>One line per package: mounted, waiting for its key, or why it was skipped.</returns>
    public IReadOnlyList<ExtraContentResult> MountExtraContent(ContentSettings settings, string gameDirectory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var results = new List<ExtraContentResult>();
        if (settings.Dlc)
        {
            foreach (var file in PackagesIn(Path.Combine(Directory, DlcFolder)))
            {
                results.Add(Mount(file, _options, new MountOptions(Priority: DlcPriority, Trust: MountTrust.Trusted, Name: $"{DlcFolder}/{Path.GetFileName(file)}"), dlc: true));
            }
        }

        if (settings.Mods)
        {
            var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in ModsInLoadOrder(Path.Combine(gameDirectory, ModsFolder), results))
            {
                results.Add(MountMod(file, loaded));
            }
        }

        return results;

        static IEnumerable<string> PackagesIn(string folder) =>
            System.IO.Directory.Exists(folder) ? System.IO.Directory.EnumerateFiles(folder, "*" + GamePackage.PackageExtension).Order(StringComparer.OrdinalIgnoreCase) : [];
    }

    /// <summary>
    /// Mounts a DLC package with a key the game did not ship with (provided by the game's server after a purchase or
    /// login). It must still be signed with the game's key. The asset database merges its index on its next refresh
    /// (<see cref="Assets.AssetDatabase.MergeExtraIndexes"/>).
    /// </summary>
    /// <param name="fileName">A file name inside <c>&lt;data&gt;/DLC</c>.</param>
    public ExtraContentResult MountDlc(string fileName, PackageEncryptionKey key)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(key);
        if (Path.GetFileName(fileName) != fileName || !fileName.EndsWith(GamePackage.PackageExtension, StringComparison.OrdinalIgnoreCase))
        {
            return new ExtraContentResult(fileName, ExtraContentStatus.Failed, "Not a package file name inside the DLC folder.");
        }

        var name = $"{DlcFolder}/{fileName}";
        if (Files.Mounts.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return new ExtraContentResult(name, ExtraContentStatus.Mounted, "Already mounted.");
        }

        var keys = new PackageKeyRing(key);
        foreach (var id in _options.Keys?.Ids ?? [])
        {
            if (_options.Keys!.TryGet(id, out var existing))
            {
                keys.Add(existing);
            }
        }

        var result = Mount(Path.Combine(Directory, DlcFolder, fileName), _options with { Keys = keys }, new MountOptions(Priority: DlcPriority, Trust: MountTrust.Trusted, Name: name), dlc: true);
        return result.Status == ExtraContentStatus.WaitingForKey
            ? result with { Status = ExtraContentStatus.Failed, Detail = "The provided key is not this package's key." }
            : result;
    }

    /// <summary>The mod packages to load, in order; problems with <see cref="ModLoadOrderFile"/> entries go to <paramref name="results"/>.</summary>
    private static List<string> ModsInLoadOrder(string folder, List<ExtraContentResult> results)
    {
        var listFile = Path.Combine(folder, ModLoadOrderFile);
        if (!File.Exists(listFile))
        {
            return System.IO.Directory.Exists(folder) ? [.. System.IO.Directory.EnumerateFiles(folder, "*" + GamePackage.PackageExtension).Order(StringComparer.OrdinalIgnoreCase)] : [];
        }

        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadLines(listFile))
        {
            var hash = raw.IndexOf('#', StringComparison.Ordinal);
            var line = (hash >= 0 ? raw[..hash] : raw).Trim();
            if (line.Length == 0 || !seen.Add(line))
            {
                continue;
            }

            var name = $"{ModsFolder}/{line}";
            if (Path.GetFileName(line) != line || !line.EndsWith(GamePackage.PackageExtension, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new ExtraContentResult(name, ExtraContentStatus.Failed, $"{ModLoadOrderFile} lists something that is not a package file name in the Mods folder."));
            }
            else if (!File.Exists(Path.Combine(folder, line)))
            {
                results.Add(new ExtraContentResult(name, ExtraContentStatus.Failed, $"Listed in {ModLoadOrderFile}, but the file is missing."));
            }
            else
            {
                files.Add(Path.Combine(folder, line));
            }
        }

        return files;
    }

    /// <summary>A mod: no keys (it must be readable without the game's), any or no signature, never code; the mods it requires must be loaded before it.</summary>
    private ExtraContentResult MountMod(string file, HashSet<string> loaded)
    {
        var name = $"{ModsFolder}/{Path.GetFileName(file)}";
        MnvtPackage package;
        try
        {
            package = MnvtPackage.Open(file, new PackageOpenOptions { Statistics = Files.Statistics });
        }
        catch (IOException ex)
        {
            return new ExtraContentResult(name, ExtraContentStatus.Failed, ex.Message);
        }

        try
        {
            var manifest = ModManifest.Read(package);
            if (manifest?.Requires.FirstOrDefault(r => !loaded.Contains(r)) is { } missing)
            {
                package.Dispose();
                return new ExtraContentResult(name, ExtraContentStatus.Failed, $"Needs the mod '{missing}', which is not loaded before it.", manifest);
            }

            // Every mod has the same priority and a later mount wins, so the load order decides overrides.
            Files.Mount(new PackageFileSystemProvider(package), new MountOptions(Priority: ModPriority, Trust: MountTrust.Untrusted, Name: name));
            loaded.Add(manifest?.Name ?? Path.GetFileNameWithoutExtension(file));
            return new ExtraContentResult(name, ExtraContentStatus.Mounted, null, manifest);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            package.Dispose();
            return new ExtraContentResult(name, ExtraContentStatus.Failed, ex.Message);
        }
    }

    private ExtraContentResult Mount(string file, PackageOpenOptions options, MountOptions mount, bool dlc)
    {
        try
        {
            Files.MountPackage(file, options, mount);
            return new ExtraContentResult(mount.Name!, ExtraContentStatus.Mounted, null);
        }
        catch (PackageException ex) when (dlc && ex.Error == PackageError.MissingKey)
        {
            return new ExtraContentResult(mount.Name!, ExtraContentStatus.WaitingForKey, ex.Message);
        }
        catch (IOException ex)
        {
            return new ExtraContentResult(mount.Name!, ExtraContentStatus.Failed, ex.Message);
        }
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

            return packages.Count > 0 ? new GameContent(directory, vfs, packages, options) : throw new PackageException(PackageError.Malformed, "The game lists no content packages.");
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
