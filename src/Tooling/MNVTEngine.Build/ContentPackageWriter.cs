using System.Security.Cryptography;
using MNVTEngine.IO.Packages;
using MNVTEngine.Runtime;

namespace MNVTEngine.Build;

/// <param name="BuildId">Every package carries it; their GUIDs and key salts derive from it, so identical builds give identical packages.</param>
/// <param name="Key">Encrypts the packages; null writes them unencrypted (debugging).</param>
/// <param name="Signer">Signs the packages; null leaves them unsigned (tests only: games refuse unsigned packages).</param>
/// <param name="CompressionByExtension">Per file type (".wav") compression, overriding <paramref name="Compression"/>.</param>
/// <param name="Properties">Public package properties (name, target, profile, engine version).</param>
public sealed record ContentPackageOptions(
    Guid BuildId,
    PackageEncryptionKey? Key,
    PackageSigningKey? Signer,
    PackageCompression Compression = PackageCompression.Balanced,
    IReadOnlyDictionary<string, PackageCompression>? CompressionByExtension = null,
    IReadOnlyDictionary<string, string>? Properties = null);

/// <summary>
/// Writes a game's content packages into one folder: opens each on first use, and always creates the globals, default
/// resources and level packages (shared assets and resources only when they have content), so the package list is
/// stable for a given project. <see cref="Complete"/> finishes every package and returns their identities.
/// </summary>
public sealed class ContentPackageWriter : IDisposable
{
    private static readonly string[] KindOrder = [ContentPackageKinds.Globals, ContentPackageKinds.DefaultResources, ContentPackageKinds.Level, ContentPackageKinds.SharedAssets, ContentPackageKinds.Resources];

    private readonly string _directory;
    private readonly ContentPackageOptions _options;
    private readonly Dictionary<string, (PackageWriter Writer, ContentPackage Package)> _open = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private List<ContentPackage>? _completed;

    public ContentPackageWriter(string directory, ContentPackageOptions options)
    {
        _directory = directory;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Open(new ContentPackage(GamePackage.GlobalsPackage, ContentPackageKinds.Globals));
        Open(new ContentPackage(GamePackage.DefaultResourcesPackage, ContentPackageKinds.DefaultResources));
    }

    /// <summary>Packages in a fixed order (globals, default resources, levels, shared assets, resources) with entry counts and identities; set by <see cref="Complete"/>.</summary>
    public IReadOnlyList<ContentPackage> Packages => _completed ?? throw new InvalidOperationException("Complete the packages first.");

    /// <summary>What each package's writer reported, in <see cref="Packages"/> order; set by <see cref="Complete"/>.</summary>
    public IReadOnlyList<PackageWriteResult> Results { get; private set; } = [];

    /// <summary>Creates one package per level (from <see cref="ContentLayout.Levels"/>), even for a scene with no own assets.</summary>
    public void DeclareLevels(IEnumerable<ContentPackage> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        foreach (var level in levels)
        {
            Open(level);
        }
    }

    /// <summary>File paths of the packages written so far.</summary>
    public IEnumerable<string> Files => _order.Select(f => Path.Combine(_directory, f));

    public PackageWriter Globals => _open[GamePackage.GlobalsPackage].Writer;

    public PackageWriter DefaultResources => _open[GamePackage.DefaultResourcesPackage].Writer;

    /// <summary>The writer of a package named by <see cref="ContentLayout.PackageOf"/>; shared assets and resources open on demand.</summary>
    public PackageWriter For(string file)
    {
        if (_open.TryGetValue(file, out var open))
        {
            return open.Writer;
        }

        var kind = file == GamePackage.SharedAssetsPackage ? ContentPackageKinds.SharedAssets
            : file == GamePackage.ResourcesPackage ? ContentPackageKinds.Resources
            : throw new ArgumentException($"'{file}' is not a package of this layout.", nameof(file));
        return Open(new ContentPackage(file, kind));
    }

    /// <summary>Writes every package's index, signature and header.</summary>
    public IReadOnlyList<ContentPackage> Complete()
    {
        var results = new Dictionary<string, PackageWriteResult>(StringComparer.Ordinal);
        foreach (var file in _order)
        {
            results[file] = _open[file].Writer.Complete();
        }

        _completed = [.. _order
            .Select(f => _open[f].Package with { Entries = results[f].Files, Guid = results[f].PackageGuid, HeaderSha256 = results[f].HeaderSha256 })
            .OrderBy(p => Array.IndexOf(KindOrder, p.Kind))];
        Results = [.. _completed.Select(p => results[p.File])];
        return _completed;
    }

    public void Dispose()
    {
        foreach (var file in _order)
        {
            _open[file].Writer.Dispose();
        }
    }

    /// <summary>A package's GUID: derived from the build and the file name, so identical builds give identical packages.</summary>
    public static Guid PackageGuidFor(Guid buildId, string file)
    {
        var hash = SHA256.HashData([.. buildId.ToByteArray(), .. System.Text.Encoding.UTF8.GetBytes(file.ToUpperInvariant())]);
        return new Guid(hash.AsSpan(0, 16));
    }

    private PackageWriter Open(ContentPackage package)
    {
        var properties = new Dictionary<string, string>(_options.Properties ?? new Dictionary<string, string>(), StringComparer.Ordinal)
        {
            ["kind"] = package.Kind, // no scene or file names: those stay in the encrypted index
        };

        var overrides = _options.CompressionByExtension is { } byExtension ? new Dictionary<string, PackageCompression>(byExtension, StringComparer.OrdinalIgnoreCase) : null;
        var writer = new PackageWriter(Path.Combine(_directory, package.File), new PackageWriterOptions(
            PackageGuidFor(_options.BuildId, package.File),
            _options.BuildId,
            _options.Compression,
            CompressionFor: overrides is null ? null : path => overrides.TryGetValue(Path.GetExtension(path), out var compression) ? compression : null,
            EncryptionKey: _options.Key,
            SigningKey: _options.Signer,
            Properties: properties));
        _open.Add(package.File, (writer, package));
        _order.Add(package.File);
        return writer;
    }
}
