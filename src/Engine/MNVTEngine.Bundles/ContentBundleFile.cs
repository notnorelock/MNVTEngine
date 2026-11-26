// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using System.Globalization;
using System.Text;
using MNVTEngine.IO;
using MNVTEngine.IO.Packages;
using MNVTEngine.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Bundles;

/// <summary>A bundle that may not be loaded, with every problem found.</summary>
public sealed class ContentBundleException(string source, IReadOnlyList<string> problems)
    : Exception($"{source} is not a content bundle that may be loaded:\n  " + string.Join("\n  ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>What a bundle says about itself (<c>bundle.json</c>).</summary>
/// <param name="Kind">What the bundle is for, chosen by the game (<c>borealise.world</c>, <c>borealise.avatar</c>).</param>
/// <param name="Root">The prefab the content starts from.</param>
/// <param name="Properties">Short strings the tool that built it adds (SDK version, author).</param>
public sealed record ContentBundleManifest(string Kind, string Name, Guid Root, IReadOnlyDictionary<string, string> Properties);

/// <summary>One asset in a bundle: its path inside the bundle (relative to <c>Assets/</c>), type and import settings.</summary>
public sealed record ContentBundleAsset(Guid Guid, string Path, string Type, string? Importer, JObject Settings);

/// <summary>
/// What a game accepts from bundles. Bundles are untrusted (made by players, downloaded from servers), so the defaults are
/// strict: no code of any kind, a bounded size, and only asset types that are plain data.
/// </summary>
public sealed record ContentBundlePolicy
{
    /// <summary>Asset types without code: images, sounds, fonts, prefabs and plain data.</summary>
    public static readonly IReadOnlySet<string> DataTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        AssetTypes.Texture, AssetTypes.Audio, AssetTypes.Font, AssetTypes.Prefab, AssetTypes.Json, AssetTypes.Text,
        AssetTypes.Animation, AssetTypes.Material, AssetTypes.AnimatorController, AssetTypes.ParticleSystem,
    };

    /// <summary>The kind the bundle must be; null accepts any.</summary>
    public string? Kind { get; init; }

    /// <summary>The most bytes the bundle's files may hold, unpacked.</summary>
    public long MaxBytes { get; init; } = 64L * 1024 * 1024;

    public int MaxAssets { get; init; } = 2048;

    /// <summary>The asset types the bundle may hold. Scripts are never allowed, whatever this says.</summary>
    public IReadOnlySet<string> AllowedAssetTypes { get; init; } = DataTypes;
}

/// <summary>
/// An opened <c>.mnvtbundle</c>: a <c>.mnvtpack</c> package with <c>bundle.json</c>, an asset index (<c>bundle-index.json</c>),
/// the assets under <c>Assets/</c> and their imported forms under <c>Artifacts/&lt;guid&gt;/</c>, and nothing else.
/// <see cref="Open"/> checks it against a <see cref="ContentBundlePolicy"/> and reports every problem at once; a bundle
/// that opens has no code, stays within the limits, and every asset it lists is present. Owns the package.
/// </summary>
public sealed class ContentBundleFile : IDisposable
{
    public const string Extension = ".mnvtbundle";
    public const string ManifestEntry = "bundle.json";
    public const string IndexEntry = "bundle-index.json";
    public const string AssetsFolder = "Assets";
    public const string ArtifactsFolder = "Artifacts";
    public const int SchemaVersion = 1;

    /// <summary>The longest manifest or index read (both are small; a bigger one is refused before parsing).</summary>
    private const int MaxJsonBytes = 4 * 1024 * 1024;

    private readonly Dictionary<Guid, ContentBundleAsset> _assets;

    private ContentBundleFile(MnvtPackage package, ContentBundleManifest manifest, Dictionary<Guid, ContentBundleAsset> assets)
    {
        Package = package;
        Manifest = manifest;
        _assets = assets;
    }

    public MnvtPackage Package { get; }

    public ContentBundleManifest Manifest { get; }

    public IReadOnlyCollection<ContentBundleAsset> Assets => _assets.Values;

    public ContentBundleAsset Root => _assets[Manifest.Root];

    public ContentBundleAsset? Find(Guid guid) => _assets.GetValueOrDefault(guid);

    /// <exception cref="KeyNotFoundException">The bundle has no such asset.</exception>
    /// <exception cref="IOException">The package data is damaged.</exception>
    public byte[] ReadAsset(Guid guid) => Read(Package, AssetsFolder + "/" + (Find(guid) ?? throw new KeyNotFoundException($"The bundle has no asset {guid}.")).Path);

    /// <summary>Opens and checks a bundle file.</summary>
    /// <exception cref="ContentBundleException">Not a bundle the policy accepts (every problem is listed).</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static ContentBundleFile Open(string file, ContentBundlePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        policy ??= new ContentBundlePolicy();
        MnvtPackage package;
        try
        {
            package = MnvtPackage.Open(file); // no keys: bundles are not encrypted; any signature is only informational
        }
        catch (PackageException ex)
        {
            throw new ContentBundleException(Path.GetFileName(file), [ex.Message]);
        }

        try
        {
            var problems = new List<string>();
            var (manifest, assets) = Check(package, policy, problems);
            if (problems.Count > 0)
            {
                throw new ContentBundleException(Path.GetFileName(file), problems);
            }

            return new ContentBundleFile(package, manifest!, assets);
        }
        catch
        {
            package.Dispose();
            throw;
        }
    }

    /// <summary>Writes a bundle: the manifest, the index and each asset's bytes (callers check what goes in; <see cref="Open"/> checks it again).</summary>
    /// <param name="packageGuid">The package's identity; builders derive it from the content, so unchanged bundles are the same file.</param>
    public static void Write(string file, ContentBundleManifest manifest, IReadOnlyList<(ContentBundleAsset Asset, byte[] Data)> assets, Guid packageGuid, Guid buildId,
        IReadOnlyDictionary<string, string>? packageProperties = null)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var options = new PackageWriterOptions(packageGuid, buildId, Properties: packageProperties ?? new Dictionary<string, string> { ["kind"] = "bundle", ["bundleKind"] = manifest.Kind });
        using var writer = new PackageWriter(file, options);
        writer.Add(ManifestEntry, WriteManifest(manifest));
        writer.Add(IndexEntry, WriteIndex(assets.Select(a => a.Asset)));
        foreach (var (asset, data) in assets)
        {
            writer.Add($"{AssetsFolder}/{asset.Path}", data, asset.Guid);
        }

        writer.Complete();
    }

    /// <summary>The manifest as the builder writes it.</summary>
    public static byte[] WriteManifest(ContentBundleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var json = new JObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["kind"] = manifest.Kind,
            ["name"] = manifest.Name,
            ["root"] = manifest.Root.ToString("D"),
            ["properties"] = JObject.FromObject(manifest.Properties),
        };
        return Encoding.UTF8.GetBytes(json.ToString(Formatting.Indented));
    }

    /// <summary>The index as the builder writes it.</summary>
    public static byte[] WriteIndex(IEnumerable<ContentBundleAsset> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var json = new JObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["assets"] = new JArray(assets.OrderBy(a => a.Path, StringComparer.Ordinal).Select(a => new JObject
            {
                ["guid"] = a.Guid.ToString("D"),
                ["path"] = a.Path,
                ["type"] = a.Type,
                ["importer"] = a.Importer,
                ["settings"] = a.Settings,
            })),
        };
        return Encoding.UTF8.GetBytes(json.ToString(Formatting.None));
    }

    public void Dispose() => Package.Dispose();

    private static (ContentBundleManifest? Manifest, Dictionary<Guid, ContentBundleAsset> Assets) Check(MnvtPackage package, ContentBundlePolicy policy, List<string> problems)
    {
        var assets = new Dictionary<Guid, ContentBundleAsset>();
        long bytes = 0;
        foreach (var entry in package.Entries)
        {
            bytes += entry.Length;
            var top = entry.Path.Split('/')[0];
            if (entry.Path is not (ManifestEntry or IndexEntry) && top is not (AssetsFolder or ArtifactsFolder))
            {
                problems.Add($"{entry.Path}: bundles hold only {ManifestEntry}, {IndexEntry}, {AssetsFolder}/ and {ArtifactsFolder}/ (no code)");
            }
        }

        if (bytes > policy.MaxBytes)
        {
            problems.Add($"its files hold {bytes} bytes; at most {policy.MaxBytes} are allowed");
        }

        var manifestJson = ReadJson(package, ManifestEntry, problems);
        var indexJson = ReadJson(package, IndexEntry, problems);
        if (manifestJson is null || indexJson is null)
        {
            return (null, assets);
        }

        var manifest = ReadManifest(manifestJson, policy, problems);
        if (indexJson.Value<int?>("schemaVersion") != SchemaVersion || indexJson["assets"] is not JArray list)
        {
            problems.Add($"{IndexEntry}: unsupported format");
            return (manifest, assets);
        }

        if (list.Count > policy.MaxAssets)
        {
            problems.Add($"{IndexEntry}: {list.Count} assets; at most {policy.MaxAssets} are allowed");
            return (manifest, assets);
        }

        var paths = new HashSet<string>(VirtualPath.Comparer);
        for (var i = 0; i < list.Count; i++)
        {
            if (ReadAsset(list[i] as JObject, $"{IndexEntry}: assets[{i.ToString(CultureInfo.InvariantCulture)}]", policy, package, problems) is { } asset)
            {
                if (!assets.TryAdd(asset.Guid, asset) || !paths.Add(asset.Path))
                {
                    problems.Add($"{IndexEntry}: assets[{i.ToString(CultureInfo.InvariantCulture)}]: GUID or path listed twice");
                }
            }
        }

        foreach (var entry in package.Entries.Where(e => e.Path.StartsWith(ArtifactsFolder + "/", StringComparison.Ordinal)))
        {
            var parts = entry.Path.Split('/');
            if (parts.Length != 3 || !Guid.TryParseExact(parts[1], "D", out var owner) || !assets.ContainsKey(owner))
            {
                problems.Add($"{entry.Path}: an artifact of no asset in the bundle");
            }
        }

        if (manifest is not null && assets.GetValueOrDefault(manifest.Root) is not { Type: AssetTypes.Prefab })
        {
            problems.Add($"{ManifestEntry}: root: not a prefab in the bundle");
        }

        return (manifest, assets);
    }

    private static ContentBundleManifest? ReadManifest(JObject json, ContentBundlePolicy policy, List<string> problems)
    {
        var before = problems.Count;
        if (json.Value<int?>("schemaVersion") is not SchemaVersion)
        {
            problems.Add($"{ManifestEntry}: schemaVersion: {json["schemaVersion"]} is not supported (expected {SchemaVersion})");
        }

        var kind = json.Value<string>("kind");
        if (kind is not { Length: > 0 and <= 64 })
        {
            problems.Add($"{ManifestEntry}: kind: missing or longer than 64 characters");
        }
        else if (policy.Kind is { } expected && !string.Equals(kind, expected, StringComparison.Ordinal))
        {
            problems.Add($"{ManifestEntry}: kind: '{kind}', expected '{expected}'");
        }

        var name = json.Value<string>("name");
        if (name is not { Length: > 0 and <= 128 })
        {
            problems.Add($"{ManifestEntry}: name: missing or longer than 128 characters");
        }

        if (!Guid.TryParseExact(json.Value<string>("root"), "D", out var root))
        {
            problems.Add($"{ManifestEntry}: root: not a GUID");
        }

        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (json["properties"] is JObject list)
        {
            foreach (var property in list.Properties().Take(65))
            {
                if (properties.Count == 64 || property.Name.Length is 0 or > 64 || property.Value.Type != JTokenType.String || property.Value.ToString().Length > 256)
                {
                    problems.Add($"{ManifestEntry}: properties: at most 64 strings of up to 256 characters with names of up to 64");
                    break;
                }

                properties[property.Name] = property.Value.ToString();
            }
        }

        return problems.Count == before ? new ContentBundleManifest(kind!, name!, root, properties) : null;
    }

    private static ContentBundleAsset? ReadAsset(JObject? item, string where, ContentBundlePolicy policy, MnvtPackage package, List<string> problems)
    {
        if (item is null || !Guid.TryParseExact(item.Value<string>("guid"), "D", out var guid) || guid == Guid.Empty)
        {
            problems.Add($"{where}: guid: not a GUID");
            return null;
        }

        if (!VirtualPath.TryNormalize(item.Value<string>("path"), out var path, out var error) || path.Length == 0 || path.Length > 260)
        {
            problems.Add($"{where}: path: {error ?? "empty or too long"}");
            return null;
        }

        var type = item.Value<string>("type") ?? AssetTypes.Unknown;
        if (type == AssetTypes.Script || !policy.AllowedAssetTypes.Contains(type))
        {
            problems.Add($"{where}: {path}: assets of type {type} are not allowed in bundles");
            return null;
        }

        if (!package.Contains(AssetsFolder + "/" + path))
        {
            problems.Add($"{where}: {path}: listed but not in the package");
            return null;
        }

        return new ContentBundleAsset(guid, path, type, item.Value<string>("importer"), item["settings"] as JObject ?? []);
    }

    private static JObject? ReadJson(MnvtPackage package, string entry, List<string> problems)
    {
        if (!package.TryGetEntry(entry, out var info))
        {
            problems.Add($"{entry}: missing");
            return null;
        }

        if (info.Length > MaxJsonBytes)
        {
            problems.Add($"{entry}: larger than {MaxJsonBytes} bytes");
            return null;
        }

        try
        {
            using var reader = new JsonTextReader(new StringReader(Encoding.UTF8.GetString(Read(package, entry)))) { MaxDepth = 32, DateParseHandling = DateParseHandling.None };
            return JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        }
        catch (JsonException ex)
        {
            problems.Add($"{entry}: invalid JSON: {ex.Message}");
            return null;
        }
        catch (IOException ex)
        {
            problems.Add($"{entry}: unreadable: {ex.Message}");
            return null;
        }
    }

    private static byte[] Read(MnvtPackage package, string path)
    {
        using var stream = package.OpenRead(path);
        var data = new byte[stream.Length];
        stream.ReadExactly(data);
        return data;
    }
}
