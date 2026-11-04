using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using MNVTEngine.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Assets;

/// <summary>
/// GUID-based index of a project's Assets folder. Every asset file has a sidecar <c>&lt;file&gt;.meta</c> holding its GUID and
/// import settings; the GUID is the asset's identity, independent of its path. In editing mode the database creates
/// missing metas, imports changed files (by size/time, then content hash, settings and importer version) and caches
/// results in <c>Library/AssetDatabase.json</c>. Read-only mode never writes and only reads metas. Packaged mode
/// (exported games) reads the index and the asset data from the game's encrypted archives (<see cref="ContentArchiveSet"/>).
/// </summary>
public sealed class AssetDatabase : IAssetLookup
{
    public const string MetaExtension = ".meta";

    /// <summary>Archive entry with the asset index of an exported game.</summary>
    public const string IndexEntry = "assets.json";

    /// <summary>Archive folder holding the assets of an exported game.</summary>
    public const string AssetsEntryFolder = "Assets/";
    private const int MetaSchemaVersion = 1;
    private const int CacheSchemaVersion = 1;

    private readonly string _assetsDirectory;
    private readonly string? _cachePath;
    private readonly Dictionary<string, IAssetImporter> _importers = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<Guid, AssetRecord> _records = [];
    private Dictionary<Guid, JObject> _settings = [];
    private Dictionary<string, Guid> _byPath = new(AssetPaths.Comparer);
    private List<SerializationDiagnostic> _diagnostics = [];
    private ContentArchiveSet? _archive;

    private AssetDatabase(string assetsDirectory, string? libraryDirectory, IEnumerable<IAssetImporter> importers)
    {
        _assetsDirectory = assetsDirectory.Length > 0 ? Path.GetFullPath(assetsDirectory) : "";
        _cachePath = libraryDirectory is null ? null : Path.Combine(libraryDirectory, "AssetDatabase.json");
        foreach (var importer in importers)
        {
            foreach (var extension in importer.Extensions)
            {
                _importers.Add(extension, importer);
            }
        }
    }

    public bool IsReadOnly => _cachePath is null;

    public string AssetsDirectory => _assetsDirectory;

    public IReadOnlyCollection<AssetRecord> Assets => _records.Values;

    /// <summary>Problems found by the last <see cref="Refresh"/>: missing/orphan/invalid metas, duplicates, failed imports, broken references.</summary>
    public IReadOnlyList<SerializationDiagnostic> Diagnostics => _diagnostics;

    /// <summary>Raised after assets were added, reimported, moved or removed.</summary>
    public event Action<IReadOnlyCollection<Guid>>? AssetsChanged;

    /// <summary>Editing mode: creates metas, imports, and caches results in <paramref name="libraryDirectory"/>.</summary>
    public static AssetDatabase OpenForEditing(string assetsDirectory, string libraryDirectory, IEnumerable<IAssetImporter>? importers = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryDirectory);
        var database = new AssetDatabase(assetsDirectory, libraryDirectory, importers ?? AssetImporters.CreateDefault());
        database.LoadCache();
        database.Refresh();
        return database;
    }

    /// <summary>Read-only mode for standalone games: indexes GUIDs and settings from metas, writes nothing.</summary>
    public static AssetDatabase OpenReadOnly(string assetsDirectory, IEnumerable<IAssetImporter>? importers = null)
    {
        var database = new AssetDatabase(assetsDirectory, null, importers ?? AssetImporters.CreateDefault());
        database.Refresh();
        return database;
    }

    /// <summary>Packaged mode: the asset index and data come from <paramref name="archive"/> (written by <c>mnvt build</c>).</summary>
    /// <exception cref="InvalidDataException">The index is missing, damaged or from a newer engine.</exception>
    public static AssetDatabase OpenPackaged(ContentArchiveSet archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var database = new AssetDatabase("", null, []) { _archive = archive };
        JObject index;
        try
        {
            index = JObject.Parse(System.Text.Encoding.UTF8.GetString(archive.Read(IndexEntry)));
        }
        catch (Exception ex) when (ex is JsonException or FileNotFoundException)
        {
            throw new InvalidDataException($"The game's asset index is unreadable: {ex.Message}", ex);
        }

        if (index.Value<int?>("schemaVersion") != MetaSchemaVersion || index["assets"] is not JArray assets)
        {
            throw new InvalidDataException("The game's asset index has an unsupported format.");
        }

        foreach (var item in assets.OfType<JObject>())
        {
            var record = new AssetRecord
            {
                Guid = Guid.Parse(item.Value<string>("guid")!),
                Path = item.Value<string>("path")!,
                Type = item.Value<string>("type") ?? AssetTypes.Unknown,
                Importer = item.Value<string>("importer"),
                Status = AssetImportStatus.Imported,
            };
            record.Path = AssetPaths.Canonical(record.Path); // refuses absolute or escaping paths
            database._records[record.Guid] = record;
            database._settings[record.Guid] = item["settings"] as JObject ?? [];
        }

        database._byPath = database._records.Values.ToDictionary(r => r.Path, r => r.Guid, AssetPaths.Comparer);
        return database;
    }

    /// <summary>The index an exported game opens with <see cref="OpenPackaged"/>: GUID, path, type and settings of every asset.</summary>
    /// <param name="archiveOf">Archive file each asset is written to (informational: reads are routed by entry); null to omit.</param>
    public JObject ExportIndex(Func<Guid, string?>? archiveOf = null) => new()
    {
        ["schemaVersion"] = MetaSchemaVersion,
        ["assets"] = new JArray(_records.Values.OrderBy(r => r.Path, StringComparer.Ordinal).Select(r => new JObject
        {
            ["guid"] = r.Guid.ToString("D"),
            ["path"] = r.Path,
            ["type"] = r.Type,
            ["importer"] = r.Importer,
            ["settings"] = GetSettings(r.Guid),
            ["archive"] = archiveOf?.Invoke(r.Guid),
        })),
    };

    /// <summary>True for exported games, whose assets live in content archives.</summary>
    public bool IsPackaged => _archive is not null;

    /// <exception cref="KeyNotFoundException">Unknown asset.</exception>
    /// <exception cref="IOException">The file or archive entry cannot be read.</exception>
    /// <exception cref="InvalidDataException">The archive entry was modified or is damaged.</exception>
    public byte[] ReadAllBytes(Guid guid) => TryGetPath(guid, out var path)
        ? ReadAllBytes(path)
        : throw new KeyNotFoundException($"Asset {guid} is not in the database.");

    /// <summary>Reads a file by its path relative to Assets (also files without a GUID, in development).</summary>
    /// <exception cref="ArgumentException">The path is absolute or escapes the Assets folder.</exception>
    /// <exception cref="IOException">The file or archive entry cannot be read.</exception>
    /// <exception cref="InvalidDataException">The archive entry was modified or is damaged.</exception>
    public byte[] ReadAllBytes(string relativePath)
    {
        if (_archive is null)
        {
            return File.ReadAllBytes(AssetPaths.Resolve(_assetsDirectory, relativePath));
        }

        return _archive.Read(AssetsEntryFolder + AssetPaths.Canonical(relativePath));
    }

    /// <summary>Archive entry with the build-time compiled form of an asset (e.g. script bytecode).</summary>
    public static string CompiledEntry(Guid guid) => $"Bytecode/{guid:D}.mnvtb";

    /// <summary>The asset's build-time compiled form in exported games; null in development (compile the source).</summary>
    /// <exception cref="FileNotFoundException">Packaged game without a compiled form of this asset.</exception>
    /// <exception cref="InvalidDataException">The archive entry was modified or is damaged.</exception>
    public byte[]? ReadCompiled(Guid guid) => _archive?.Read(CompiledEntry(guid));

    public bool TryGetGuid(string relativePath, out Guid guid)
    {
        guid = Guid.Empty;
        try
        {
            return _byPath.TryGetValue(AssetPaths.Normalize(_assetsDirectory, relativePath), out guid);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public bool TryGetPath(Guid guid, [NotNullWhen(true)] out string? relativePath)
    {
        relativePath = _records.TryGetValue(guid, out var record) ? record.Path : null;
        return relativePath is not null;
    }

    public string? GetAssetType(Guid guid) => _records.GetValueOrDefault(guid)?.Type;

    public AssetRecord? Get(Guid guid) => _records.GetValueOrDefault(guid);

    /// <exception cref="InvalidOperationException">Packaged games have no asset files; use <see cref="ReadAllBytes(Guid)"/>.</exception>
    public string FullPath(Guid guid) => _archive is not null
        ? throw new InvalidOperationException("Exported games have no asset files; read assets with ReadAllBytes.")
        : TryGetPath(guid, out var path)
        ? AssetPaths.Resolve(_assetsDirectory, path)
        : throw new KeyNotFoundException($"Asset {guid} is not in the database.");

    public JObject GetSettings(Guid guid) => (JObject)(_settings.GetValueOrDefault(guid) ?? []).DeepClone();

    public TextureImportSettings GetTextureSettings(Guid guid) =>
        _settings.TryGetValue(guid, out var settings) ? TextureImporter.Read(settings) : new TextureImportSettings();

    /// <summary>Assets that reference <paramref name="guid"/> directly.</summary>
    public IReadOnlyList<AssetRecord> GetDependents(Guid guid) =>
        [.. _records.Values.Where(r => r.Dependencies.Contains(guid)).OrderBy(r => r.Path, AssetPaths.Comparer)];

    /// <summary>The roots plus everything they reference, transitively (what a build must include).</summary>
    public IReadOnlySet<Guid> CollectDependencies(IEnumerable<Guid> roots)
    {
        var result = new HashSet<Guid>();
        var stack = new Stack<Guid>(roots);
        while (stack.TryPop(out var guid))
        {
            if (result.Add(guid) && _records.TryGetValue(guid, out var record))
            {
                foreach (var dependency in record.Dependencies)
                {
                    stack.Push(dependency);
                }
            }
        }

        return result;
    }

    /// <summary>Rescans the Assets folder. Unchanged files are not re-read; changed ones are reimported.</summary>
    public AssetRefreshResult Refresh()
    {
        if (_archive is not null)
        {
            return new AssetRefreshResult([], [], [], []); // packaged content never changes
        }

        var diagnostics = new List<SerializationDiagnostic>();
        var previous = _records;
        var records = new Dictionary<Guid, AssetRecord>();
        var settingsByGuid = new Dictionary<Guid, JObject>();
        var added = new List<Guid>();
        var reimported = new List<Guid>();
        var moved = new List<Guid>();

        foreach (var file in EnumerateAssetFiles())
        {
            var relative = AssetPaths.ToRelative(_assetsDirectory, file);
            var importer = _importers.GetValueOrDefault(Path.GetExtension(file));
            if (ReadOrCreateMeta(file, relative, importer, diagnostics) is not (var guid, var settings))
            {
                continue;
            }

            if (records.TryGetValue(guid, out var clash))
            {
                // Copied file+meta pair: the copy whose path the cache does not know gets a new identity.
                if (IsReadOnly)
                {
                    diagnostics.Add(Error(relative, $"Duplicate GUID {guid} (also used by '{clash.Path}'); asset skipped."));
                    continue;
                }

                guid = Guid.NewGuid();
                WriteMeta(file + MetaExtension, guid, settings);
                diagnostics.Add(Warning(relative, $"Duplicate GUID (also used by '{clash.Path}'); assigned new GUID {guid}."));
            }

            var info = new FileInfo(file);
            var record = previous.GetValueOrDefault(guid);
            var settingsHash = Hash(settings.ToString(Formatting.None));
            if (record is null)
            {
                added.Add(guid);
            }
            else if (!AssetPaths.Comparer.Equals(record.Path, relative))
            {
                moved.Add(guid);
            }

            if (IsReadOnly)
            {
                record = new AssetRecord { Guid = guid, Path = relative, Type = importer?.AssetType ?? AssetTypes.Unknown, Importer = importer?.Name, Size = info.Length };
            }
            else if (record is null || !IsUpToDate(record, info, importer, settingsHash))
            {
                var data = File.ReadAllBytes(file);
                var sourceHash = Hash(data);
                if (record is not null && record.SourceHash == sourceHash && record.SettingsHash == settingsHash && record.Importer == importer?.Name && record.ImporterVersion == (importer?.Version ?? 0))
                {
                    record.Size = info.Length; // touched but identical: no reimport
                    record.LastWriteUtc = info.LastWriteTimeUtc;
                }
                else
                {
                    record = Import(guid, relative, importer, data, settings, settingsHash, sourceHash, info);
                    if (!added.Contains(guid))
                    {
                        reimported.Add(guid);
                    }
                }
            }

            record.Path = relative;
            records[guid] = record;
            settingsByGuid[guid] = settings;
        }

        _records = records;
        _settings = settingsByGuid;
        _byPath = records.Values.ToDictionary(r => r.Path, r => r.Guid, AssetPaths.Comparer);
        ReportProblems(diagnostics);
        _diagnostics = diagnostics;
        SaveCache();

        var result = new AssetRefreshResult(added, reimported, moved, [.. previous.Keys.Where(g => !records.ContainsKey(g))]);
        if (result.HasChanges)
        {
            AssetsChanged?.Invoke([.. result.All]);
        }

        return result;
    }

    /// <summary>Writes new import settings to the asset's meta and reimports it.</summary>
    /// <returns>Problems found while normalizing the settings.</returns>
    public IReadOnlyList<string> UpdateSettings(Guid guid, JObject settings)
    {
        EnsureWritable();
        var record = Get(guid) ?? throw new KeyNotFoundException($"Asset {guid} is not in the database.");
        var problems = new List<string>();
        var importer = record.Importer is null ? null : _importers.Values.First(i => i.Name == record.Importer);
        var normalized = importer?.NormalizeSettings(settings, problems) ?? [];
        WriteMeta(FullPath(guid) + MetaExtension, guid, normalized);
        Refresh();
        return problems;
    }

    /// <summary>Forces a reimport of one asset even if nothing changed.</summary>
    public void Reimport(Guid guid)
    {
        EnsureWritable();
        if (_records.TryGetValue(guid, out var record))
        {
            record.SourceHash = "";
            record.Size = -1;
            Refresh();
        }
    }

    /// <summary>Moves or renames an asset together with its meta; its GUID (and every reference to it) is unchanged.</summary>
    /// <exception cref="IOException">The destination exists.</exception>
    public void Move(Guid guid, string newRelativePath)
    {
        EnsureWritable();
        var source = FullPath(guid);
        var destination = AssetPaths.Resolve(_assetsDirectory, newRelativePath);
        if (File.Exists(destination) || File.Exists(destination + MetaExtension))
        {
            throw new IOException($"'{newRelativePath}' already exists.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(source, destination);
        File.Move(source + MetaExtension, destination + MetaExtension);
        Refresh();
    }

    /// <summary>Deletes an asset file and its meta. References to it become dangling; check <see cref="GetDependents"/> first.</summary>
    public void Delete(Guid guid)
    {
        EnsureWritable();
        var path = FullPath(guid);
        File.Delete(path);
        File.Delete(path + MetaExtension);
        Refresh();
    }

    /// <summary>Creates or replaces a text asset atomically and returns its GUID.</summary>
    public Guid WriteText(string relativePath, string content)
    {
        EnsureWritable();
        var full = AssetPaths.Resolve(_assetsDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, full, overwrite: true);
        Refresh();
        return TryGetGuid(relativePath, out var guid) ? guid : throw new InvalidOperationException($"'{relativePath}' was written but is not indexed.");
    }

    private IEnumerable<string> EnumerateAssetFiles()
    {
        if (!Directory.Exists(_assetsDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(_assetsDirectory, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(MetaExtension, StringComparison.OrdinalIgnoreCase)
                && !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(f).StartsWith('.'))
            .Order(StringComparer.Ordinal);
    }

    private (Guid Guid, JObject Settings)? ReadOrCreateMeta(string file, string relative, IAssetImporter? importer, List<SerializationDiagnostic> diagnostics)
    {
        var metaPath = file + MetaExtension;
        if (!File.Exists(metaPath))
        {
            if (IsReadOnly)
            {
                diagnostics.Add(Warning(relative, "No .meta file; the asset has no GUID and cannot be referenced."));
                return null;
            }

            var created = importer?.NormalizeSettings([], []) ?? [];
            var guid = Guid.NewGuid();
            WriteMeta(metaPath, guid, created);
            return (guid, created);
        }

        JObject meta;
        try
        {
            meta = JObject.Parse(File.ReadAllText(metaPath));
        }
        catch (JsonException ex)
        {
            // Never overwrite: the GUID inside may be recoverable by hand, and references depend on it.
            diagnostics.Add(Error(relative + MetaExtension, $"Unreadable meta file; asset skipped until fixed: {ex.Message}"));
            return null;
        }

        if (!Guid.TryParse(meta.Value<string>("guid"), out var id) || id == Guid.Empty)
        {
            diagnostics.Add(Error(relative + MetaExtension, "Meta file has no valid 'guid'; asset skipped until fixed."));
            return null;
        }

        var problems = new List<string>();
        var stored = meta["settings"] as JObject ?? [];
        var settings = importer?.NormalizeSettings(stored, problems) ?? stored;
        foreach (var problem in problems)
        {
            diagnostics.Add(Warning(relative + MetaExtension, problem));
        }

        if (!IsReadOnly && (!JToken.DeepEquals(settings, stored) || meta.Value<int?>("schemaVersion") != MetaSchemaVersion))
        {
            WriteMeta(metaPath, id, settings); // complete defaults so metas stay self-describing
        }

        return (id, settings);
    }

    private static void WriteMeta(string path, Guid guid, JObject settings)
    {
        var meta = new JObject
        {
            ["schemaVersion"] = MetaSchemaVersion,
            ["guid"] = guid.ToString(),
            ["settings"] = settings,
        };
        File.WriteAllText(path, meta.ToString(Formatting.Indented) + Environment.NewLine);
    }

    private static bool IsUpToDate(AssetRecord record, FileInfo info, IAssetImporter? importer, string settingsHash) =>
        record.Size == info.Length && record.LastWriteUtc == info.LastWriteTimeUtc && record.SettingsHash == settingsHash
        && record.Importer == importer?.Name && record.ImporterVersion == (importer?.Version ?? 0);

    private static AssetRecord Import(Guid guid, string relative, IAssetImporter? importer, byte[] data, JObject settings, string settingsHash, string sourceHash, FileInfo info)
    {
        var record = new AssetRecord
        {
            Guid = guid,
            Path = relative,
            Type = importer?.AssetType ?? AssetTypes.Unknown,
            Importer = importer?.Name,
            ImporterVersion = importer?.Version ?? 0,
            SourceHash = sourceHash,
            SettingsHash = settingsHash,
            Size = info.Length,
            LastWriteUtc = info.LastWriteTimeUtc,
            Status = AssetImportStatus.NotImported,
        };

        if (importer is null)
        {
            return record;
        }

        var context = new AssetImportContext(relative, data, settings);
        importer.Import(context);
        record.Status = context.Errors.Count == 0 ? AssetImportStatus.Imported : AssetImportStatus.Failed;
        record.Messages = [.. context.Errors.Select(e => "error: " + e), .. context.Warnings.Select(w => "warning: " + w)];
        record.Dependencies = [.. context.Dependencies.Where(d => d != guid)];
        record.Properties = context.Properties;
        return record;
    }

    private void ReportProblems(List<SerializationDiagnostic> diagnostics)
    {
        foreach (var record in _records.Values.OrderBy(r => r.Path, StringComparer.Ordinal))
        {
            if (record.Status == AssetImportStatus.Failed)
            {
                diagnostics.Add(Error(record.Path, $"Import failed: {string.Join("; ", record.Messages)}"));
            }

            foreach (var missing in record.Dependencies.Where(d => !_records.ContainsKey(d)))
            {
                diagnostics.Add(Warning(record.Path, $"References missing asset {missing}."));
            }
        }

        if (Directory.Exists(_assetsDirectory))
        {
            foreach (var meta in Directory.EnumerateFiles(_assetsDirectory, "*" + MetaExtension, SearchOption.AllDirectories))
            {
                if (!File.Exists(meta[..^MetaExtension.Length]))
                {
                    diagnostics.Add(Warning(AssetPaths.ToRelative(_assetsDirectory, meta), "Orphan meta file: its asset no longer exists."));
                }
            }
        }
    }

    private void LoadCache()
    {
        if (_cachePath is null || !File.Exists(_cachePath))
        {
            return;
        }

        try
        {
            var cache = JObject.Parse(File.ReadAllText(_cachePath));
            if (cache.Value<int?>("schemaVersion") == CacheSchemaVersion && cache["assets"] is JArray assets)
            {
                _records = assets.ToObject<List<AssetRecord>>(MnvtJson.CreateSerializer())!.ToDictionary(r => r.Guid);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or ArgumentException)
        {
            // The cache is derived data: drop it and reimport everything.
            _records = [];
            _diagnostics.Add(Warning("Library/AssetDatabase.json", $"Import cache unreadable, rebuilding: {ex.Message}"));
        }
    }

    private void SaveCache()
    {
        if (_cachePath is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        var cache = new JObject
        {
            ["schemaVersion"] = CacheSchemaVersion,
            ["assets"] = JArray.FromObject(_records.Values.OrderBy(r => r.Path, StringComparer.Ordinal), MnvtJson.CreateSerializer()),
        };
        File.WriteAllText(_cachePath, cache.ToString(Formatting.Indented));
    }

    private void EnsureWritable()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("The asset database is read-only.");
        }
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static string Hash(string text) => Hash(System.Text.Encoding.UTF8.GetBytes(text));

    private static SerializationDiagnostic Warning(string path, string message) => new(DiagnosticSeverity.Warning, path, message);

    private static SerializationDiagnostic Error(string path, string message) => new(DiagnosticSeverity.Error, path, message);
}
