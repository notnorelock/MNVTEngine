using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
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
/// <para>
/// <see cref="Refresh(bool)"/> with <c>background</c> imports assets of slow importers (<see cref="IAssetImporter.ImportsInBackground"/>,
/// models) on a worker thread: they show <see cref="AssetImportStatus.Importing"/> and keep their last good artifacts
/// until <see cref="CompleteBackgroundImports"/>, called on the owning thread, applies the results. Everything else
/// about the database stays single-threaded.
/// </para>
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
    private readonly string? _libraryDirectory;
    private readonly Dictionary<string, IAssetImporter> _importers = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<Guid, AssetRecord> _records = [];
    private HashSet<Guid> _alwaysIncluded = [];
    private Dictionary<Guid, JObject> _settings = [];
    private Dictionary<string, Guid> _byPath = new(AssetPaths.Comparer);
    private List<SerializationDiagnostic> _diagnostics = [];
    private ContentArchiveSet? _archive;

    // Background imports: the latest scheduled import per asset; results of older ones are dropped.
    private readonly Lock _backgroundLock = new();
    private readonly Dictionary<Guid, PendingImport> _pending = [];
    private readonly List<CompletedImport> _completed = [];
    private Task _worker = Task.CompletedTask;
    private int _importVersion;

    private AssetDatabase(string assetsDirectory, string? libraryDirectory, IEnumerable<IAssetImporter> importers)
    {
        _assetsDirectory = assetsDirectory.Length > 0 ? Path.GetFullPath(assetsDirectory) : "";
        _cachePath = libraryDirectory is null ? null : Path.Combine(libraryDirectory, "AssetDatabase.json");
        _libraryDirectory = libraryDirectory is null ? null : Path.GetFullPath(libraryDirectory);
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
    /// <param name="artifactsLibrary">A library folder whose artifacts (imported models) may be read; nothing is written.</param>
    public static AssetDatabase OpenReadOnly(string assetsDirectory, IEnumerable<IAssetImporter>? importers = null, string? artifactsLibrary = null)
    {
        var database = new AssetDatabase(assetsDirectory, null, importers ?? AssetImporters.CreateDefault()) { _readOnlyLibrary = artifactsLibrary is null ? null : Path.GetFullPath(artifactsLibrary) };
        database.Refresh();
        return database;
    }

    private string? _readOnlyLibrary;

    /// <summary>Archive entry (and library-relative path) of an asset's artifact.</summary>
    public static string ArtifactEntry(Guid guid, string name) => $"Artifacts/{guid:D}/{name}";

    /// <summary>
    /// An artifact the asset's importer produced (e.g. <c>model</c>): from the archive in exported games, from the library
    /// in development. Null when there is none (not imported yet, or the last import failed before ever succeeding).
    /// </summary>
    public byte[]? ReadArtifact(Guid guid, string name)
    {
        if (_archive is not null)
        {
            var entry = ArtifactEntry(guid, name);
            return _archive.Contains(entry) ? _archive.Read(entry) : null;
        }

        var library = _libraryDirectory ?? _readOnlyLibrary;
        if (library is null)
        {
            return null;
        }

        var file = Path.Combine(library, ArtifactEntry(guid, name));
        return File.Exists(file) ? File.ReadAllBytes(file) : null;
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
    /// <param name="include">The assets the game ships (a build strips unused ones); null for every asset.</param>
    public JObject ExportIndex(Func<Guid, string?>? archiveOf = null, Func<Guid, bool>? include = null) => new()
    {
        ["schemaVersion"] = MetaSchemaVersion,
        ["assets"] = new JArray(_records.Values.Where(r => include?.Invoke(r.Guid) ?? true).OrderBy(r => r.Path, StringComparer.Ordinal).Select(r => new JObject
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
    /// <summary>Assets being imported in the background.</summary>
    public int PendingImportCount
    {
        get
        {
            lock (_backgroundLock)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>Scans the Assets folder and imports what changed (see the type's remarks for <paramref name="background"/>).</summary>
    /// <param name="background">Import assets of slow importers on a worker thread; apply them with <see cref="CompleteBackgroundImports"/>.</param>
    public AssetRefreshResult Refresh(bool background = false)
    {
        if (_archive is not null)
        {
            return new AssetRefreshResult([], [], [], []); // packaged content never changes
        }

        var jobs = new List<ImportJob>();

        var diagnostics = new List<SerializationDiagnostic>();
        var previous = _records;
        var records = new Dictionary<Guid, AssetRecord>();
        var alwaysIncluded = new HashSet<Guid>();
        var settingsByGuid = new Dictionary<Guid, JObject>();
        var added = new List<Guid>();
        var reimported = new List<Guid>();
        var moved = new List<Guid>();

        Guid? Resolve(string relativePath)
        {
            if (records.Values.FirstOrDefault(r => AssetPaths.Comparer.Equals(r.Path, relativePath)) is { } known)
            {
                return known.Guid;
            }

            string full;
            try
            {
                full = AssetPaths.Resolve(_assetsDirectory, relativePath);
            }
            catch (ArgumentException)
            {
                return null;
            }

            return File.Exists(full) && ReadOrCreateMeta(full, relativePath, _importers.GetValueOrDefault(Path.GetExtension(full)), diagnostics) is (var guid, _, _) ? guid : null;
        }

        foreach (var file in EnumerateAssetFiles())
        {
            var relative = AssetPaths.ToRelative(_assetsDirectory, file);
            var importer = _importers.GetValueOrDefault(Path.GetExtension(file));
            if (ReadOrCreateMeta(file, relative, importer, diagnostics) is not (var guid, var settings, var alwaysInclude))
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
                WriteMeta(file + MetaExtension, guid, settings, alwaysInclude);
                diagnostics.Add(Warning(relative, $"Duplicate GUID (also used by '{clash.Path}'); assigned new GUID {guid}."));
            }

            if (alwaysInclude)
            {
                alwaysIncluded.Add(guid);
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
            else if (record is not null && IsPending(guid, info, settingsHash))
            {
                // Being imported in the background from this very file and settings: keep waiting for it.
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
                else if (background && importer is { ImportsInBackground: true })
                {
                    record = Defer(jobs, record, new ImportJob(guid, relative, importer, data, settings, settingsHash, sourceHash, info, file));
                }
                else
                {
                    record = Import(guid, relative, importer, data, settings, settingsHash, sourceHash, info, file, Resolve);
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

        // Assets whose import inputs were added or reimported (a glTF's .bin) import again, and so do assets whose last
        // import reported problems when files were added (a texture that was missing may exist now).
        if (!IsReadOnly)
        {
            var changed = added.Concat(reimported).ToHashSet();
            foreach (var record in records.Values.ToList())
            {
                if (_importers.GetValueOrDefault(Path.GetExtension(record.Path)) is { ReimportWhenDependenciesChange: true } importer
                    && !changed.Contains(record.Guid) && (record.ImportInputs.Any(changed.Contains) || (added.Count > 0 && record.Messages.Count > 0)))
                {
                    var file = AssetPaths.Resolve(_assetsDirectory, record.Path);
                    var data = File.ReadAllBytes(file);
                    var settings = settingsByGuid[record.Guid];
                    var job = new ImportJob(record.Guid, record.Path, importer, data, settings, Hash(settings.ToString(Formatting.None)), Hash(data), new FileInfo(file), file);
                    if (background && importer.ImportsInBackground)
                    {
                        records[record.Guid] = Defer(jobs, record, job);
                        continue;
                    }

                    records[record.Guid] = Import(job.Guid, job.Relative, importer, data, settings, job.SettingsHash, job.SourceHash, job.Info, file, Resolve);
                    reimported.Add(record.Guid);
                }
            }

            foreach (var removed in previous.Keys.Where(g => !records.ContainsKey(g)))
            {
                DeleteArtifacts(removed);
            }
        }

        _records = records;
        _alwaysIncluded = alwaysIncluded;
        _settings = settingsByGuid;
        _byPath = records.Values.ToDictionary(r => r.Path, r => r.Guid, AssetPaths.Comparer);
        ReportProblems(diagnostics);
        _diagnostics = diagnostics;
        SaveCache();
        Schedule(jobs);

        var result = new AssetRefreshResult(added, reimported, moved, [.. previous.Keys.Where(g => !records.ContainsKey(g))]);
        if (result.HasChanges)
        {
            AssetsChanged?.Invoke([.. result.All]);
        }

        return result;
    }

    /// <summary>
    /// Applies the background imports that finished: writes their artifacts, updates their records and raises
    /// <see cref="AssetsChanged"/>. Call it on the thread that owns the database (the editor calls it every frame).
    /// </summary>
    /// <returns>The assets whose import finished.</returns>
    public IReadOnlyList<Guid> CompleteBackgroundImports()
    {
        List<CompletedImport> done;
        lock (_backgroundLock)
        {
            if (_completed.Count == 0)
            {
                return [];
            }

            done = [.. _completed];
            _completed.Clear();
        }

        var changed = new List<Guid>();
        foreach (var result in done)
        {
            lock (_backgroundLock)
            {
                if (!_pending.TryGetValue(result.Guid, out var pending) || pending.Version != result.Version)
                {
                    continue; // superseded by a later import
                }

                _pending.Remove(result.Guid);
            }

            if (!_records.TryGetValue(result.Guid, out var current))
            {
                continue; // deleted meanwhile
            }

            if (result.Record.Status == AssetImportStatus.Imported)
            {
                WriteArtifacts(result.Guid, result.Artifacts);
            }

            result.Record.Path = current.Path;
            _records[result.Guid] = result.Record;
            changed.Add(result.Guid);
        }

        if (changed.Count > 0)
        {
            SaveCache();
            AssetsChanged?.Invoke(changed);
        }

        return changed;
    }

    /// <summary>Waits for the background imports to finish and applies them (tools and tests).</summary>
    /// <returns>False when they did not finish within <paramref name="timeout"/>.</returns>
    public bool WaitForBackgroundImports(TimeSpan timeout)
    {
        Task worker;
        lock (_backgroundLock)
        {
            worker = _worker;
        }

        var finished = worker.Wait(timeout);
        CompleteBackgroundImports();
        return finished;
    }

    private bool IsPending(Guid guid, FileInfo info, string settingsHash)
    {
        lock (_backgroundLock)
        {
            return _pending.TryGetValue(guid, out var pending) && pending.Size == info.Length && pending.LastWriteUtc == info.LastWriteTimeUtc && pending.SettingsHash == settingsHash;
        }
    }

    /// <summary>
    /// The record an asset has while it imports in the background: the last one (type, dependencies, facts) marked
    /// Importing, and never up to date, so a cache saved meanwhile imports it again on the next open.
    /// </summary>
    private static AssetRecord Defer(List<ImportJob> jobs, AssetRecord? last, ImportJob job)
    {
        jobs.Add(job);
        return new AssetRecord
        {
            Guid = job.Guid,
            Path = job.Relative,
            Type = job.Importer.AssetType,
            Importer = job.Importer.Name,
            ImporterVersion = -1,
            SourceHash = "",
            SettingsHash = job.SettingsHash,
            Size = -1,
            Status = AssetImportStatus.Importing,
            Messages = last?.Messages ?? [],
            Dependencies = last?.Dependencies ?? [],
            ImportInputs = last?.ImportInputs ?? [],
            Properties = last?.Properties ?? [],
        };
    }

    /// <summary>Queues jobs on the worker (one at a time); they resolve other assets from a snapshot, never the live database.</summary>
    private void Schedule(List<ImportJob> jobs)
    {
        if (jobs.Count == 0)
        {
            return;
        }

        var byPath = new Dictionary<string, Guid>(_byPath, AssetPaths.Comparer);
        var files = _records.Values.ToDictionary(r => r.Guid, r => Path.Combine(_assetsDirectory, r.Path.Replace('/', Path.DirectorySeparatorChar)));
        lock (_backgroundLock)
        {
            foreach (var job in jobs)
            {
                var version = ++_importVersion;
                _pending[job.Guid] = new PendingImport(version, job.Info.Length, job.Info.LastWriteTimeUtc, job.SettingsHash);
                _worker = _worker.ContinueWith(_ => RunInBackground(job, version, byPath, files), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }
    }

    private void RunInBackground(ImportJob job, int version, Dictionary<string, Guid> byPath, Dictionary<Guid, string> files)
    {
        AssetRecord record;
        Dictionary<string, byte[]> artifacts;
        try
        {
            (record, artifacts) = RunImporter(job.Guid, job.Relative, job.Importer, job.Data, job.Settings, job.SettingsHash, job.SourceHash, job.Info, job.File,
                path => byPath.TryGetValue(path, out var guid) ? guid : null, guid => files.GetValueOrDefault(guid));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // On the worker nobody else would see it: the asset fails with the reason, as an import error would.
            record = new AssetRecord
            {
                Guid = job.Guid, Path = job.Relative, Type = job.Importer.AssetType, Importer = job.Importer.Name, ImporterVersion = job.Importer.Version,
                SourceHash = job.SourceHash, SettingsHash = job.SettingsHash, Size = job.Info.Length, LastWriteUtc = job.Info.LastWriteTimeUtc,
                Status = AssetImportStatus.Failed, Messages = [$"error: the {job.Importer.Name} importer failed: {ex.GetType().Name}: {ex.Message}"],
            };
            artifacts = [];
        }

        lock (_backgroundLock)
        {
            _completed.Add(new CompletedImport(job.Guid, version, record, artifacts));
        }
    }

    private sealed record ImportJob(Guid Guid, string Relative, IAssetImporter Importer, byte[] Data, JObject Settings, string SettingsHash, string SourceHash, FileInfo Info, string File);

    private sealed record PendingImport(int Version, long Size, DateTime LastWriteUtc, string SettingsHash);

    private sealed record CompletedImport(Guid Guid, int Version, AssetRecord Record, Dictionary<string, byte[]> Artifacts);

    /// <summary>Writes new import settings to the asset's meta and reimports it.</summary>
    /// <summary>
    /// Whether builds keep the asset even when nothing the build analyses references it (<c>"alwaysInclude": true</c> in
    /// its .meta): content the game loads by a path or GUID computed at run time.
    /// </summary>
    public bool IsAlwaysIncluded(Guid guid) => _alwaysIncluded.Contains(guid);

    /// <summary>Sets <see cref="IsAlwaysIncluded"/> in the asset's .meta.</summary>
    public void SetAlwaysInclude(Guid guid, bool alwaysInclude)
    {
        EnsureWritable();
        var path = FullPath(guid) + MetaExtension;
        var meta = JObject.Parse(File.ReadAllText(path));
        WriteMeta(path, guid, meta["settings"] as JObject ?? [], alwaysInclude);
        Refresh();
    }

    /// <returns>Problems found while normalizing the settings.</returns>
    public IReadOnlyList<string> UpdateSettings(Guid guid, JObject settings)
    {
        EnsureWritable();
        var record = Get(guid) ?? throw new KeyNotFoundException($"Asset {guid} is not in the database.");
        var problems = new List<string>();
        var importer = record.Importer is null ? null : _importers.Values.First(i => i.Name == record.Importer);
        var normalized = importer?.NormalizeSettings(settings, problems) ?? [];
        WriteMeta(FullPath(guid) + MetaExtension, guid, normalized, IsAlwaysIncluded(guid));
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
    public Guid WriteText(string relativePath, string content) =>
        WriteFiles(new Dictionary<string, byte[]> { [relativePath] = new UTF8Encoding(false).GetBytes(content) })[relativePath];

    /// <summary>
    /// Creates or replaces several assets (each atomically), then refreshes once, and returns their GUIDs by path.
    /// </summary>
    public IReadOnlyDictionary<string, Guid> WriteFiles(IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        EnsureWritable();
        foreach (var (relativePath, bytes) in files)
        {
            var full = AssetPaths.Resolve(_assetsDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var temp = full + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, full, overwrite: true);
        }

        Refresh();
        return files.Keys.ToDictionary(p => p, p => TryGetGuid(p, out var guid) ? guid : throw new InvalidOperationException($"'{p}' was written but is not indexed."));
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

    private (Guid Guid, JObject Settings, bool AlwaysInclude)? ReadOrCreateMeta(string file, string relative, IAssetImporter? importer, List<SerializationDiagnostic> diagnostics)
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
            return (guid, created, false);
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

        var alwaysInclude = meta.Value<bool?>("alwaysInclude") ?? false;
        if (!IsReadOnly && (!JToken.DeepEquals(settings, stored) || meta.Value<int?>("schemaVersion") != MetaSchemaVersion))
        {
            WriteMeta(metaPath, id, settings, alwaysInclude); // complete defaults so metas stay self-describing
        }

        return (id, settings, alwaysInclude);
    }

    private static void WriteMeta(string path, Guid guid, JObject settings, bool alwaysInclude = false)
    {
        var meta = new JObject
        {
            ["schemaVersion"] = MetaSchemaVersion,
            ["guid"] = guid.ToString(),
            ["settings"] = settings,
        };
        if (alwaysInclude)
        {
            meta["alwaysInclude"] = true; // kept in builds even when nothing references it (assets loaded by path at run time)
        }

        File.WriteAllText(path, meta.ToString(Formatting.Indented) + Environment.NewLine);
    }

    private static bool IsUpToDate(AssetRecord record, FileInfo info, IAssetImporter? importer, string settingsHash) =>
        record.Size == info.Length && record.LastWriteUtc == info.LastWriteTimeUtc && record.SettingsHash == settingsHash
        && record.Importer == importer?.Name && record.ImporterVersion == (importer?.Version ?? 0);

    /// <summary>Imports now, replacing any background import of the asset, and writes the artifacts.</summary>
    private AssetRecord Import(Guid guid, string relative, IAssetImporter? importer, byte[] data, JObject settings, string settingsHash, string sourceHash, FileInfo info, string file, Func<string, Guid?> resolve)
    {
        lock (_backgroundLock)
        {
            _pending.Remove(guid);
        }

        var (record, artifacts) = RunImporter(guid, relative, importer, data, settings, settingsHash, sourceHash, info, file, resolve,
            other => _records.TryGetValue(other, out var r) && _archive is null ? Path.Combine(_assetsDirectory, r.Path.Replace('/', Path.DirectorySeparatorChar)) : null);
        if (record.Status == AssetImportStatus.Imported && !IsReadOnly)
        {
            WriteArtifacts(guid, artifacts);
        }

        return record;
    }

    /// <summary>Runs the importer: the new record and the artifacts to write (nothing is written here).</summary>
    private (AssetRecord Record, Dictionary<string, byte[]> Artifacts) RunImporter(Guid guid, string relative, IAssetImporter? importer, byte[] data, JObject settings, string settingsHash, string sourceHash, FileInfo info, string file, Func<string, Guid?> resolve, Func<Guid, string?> assetFile)
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
            return (record, []);
        }

        var context = new AssetImportContext(relative, data, settings)
        {
            SourceFullPath = file,
            AssetsDirectory = _assetsDirectory,
            ResolveAsset = resolve,
            AssetFile = assetFile,
        };
        importer.Import(context);
        record.Status = context.Errors.Count == 0 ? AssetImportStatus.Imported : AssetImportStatus.Failed;
        record.Messages = [.. context.Errors.Select(e => "error: " + e), .. context.Warnings.Select(w => "warning: " + w)];
        record.Dependencies = [.. context.Dependencies.Union(context.ImportInputs).Where(d => d != guid)];
        record.ImportInputs = [.. context.ImportInputs.Where(d => d != guid)];
        record.Properties = context.Properties;
        return (record, context.Artifacts);
    }

    /// <summary>Replaces an asset's artifacts: each file is written next to its target and moved over it.</summary>
    private void WriteArtifacts(Guid guid, Dictionary<string, byte[]> artifacts)
    {
        if (_libraryDirectory is null || artifacts.Count == 0)
        {
            return;
        }

        foreach (var (name, bytes) in artifacts)
        {
            var path = Path.Combine(_libraryDirectory, ArtifactEntry(guid, name));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var staging = path + ".staging";
            File.WriteAllBytes(staging, bytes);
            File.Move(staging, path, overwrite: true);
        }
    }

    private void DeleteArtifacts(Guid guid)
    {
        if (_libraryDirectory is not null && Path.Combine(_libraryDirectory, "Artifacts", guid.ToString("D")) is var folder && Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
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
