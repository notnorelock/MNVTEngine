using MNVTEngine.Graphics;
using Newtonsoft.Json;

namespace MNVTEngine.Assets;

public enum AssetImportStatus
{
    /// <summary>No importer ran: unknown file type, or a read-only database (standalone game).</summary>
    NotImported,
    Imported,
    Failed,

    /// <summary>Importing on a background thread; the last good artifacts stay in use until it finishes.</summary>
    Importing,
}

/// <summary>What the database knows about one asset. Persisted in the editor's import cache.</summary>
public sealed class AssetRecord
{
    public Guid Guid { get; set; }

    /// <summary>Path relative to Assets, forward slashes.</summary>
    public string Path { get; set; } = "";

    /// <summary>See <see cref="Serialization.AssetTypes"/>.</summary>
    public string Type { get; set; } = Serialization.AssetTypes.Unknown;

    public string? Importer { get; set; }

    public int ImporterVersion { get; set; }

    /// <summary>SHA-256 of the file contents at last import.</summary>
    public string SourceHash { get; set; } = "";

    public long Size { get; set; }

    public DateTime LastWriteUtc { get; set; }

    /// <summary>SHA-256 of the normalized import settings at last import.</summary>
    public string SettingsHash { get; set; } = "";

    public AssetImportStatus Status { get; set; }

    /// <summary>Other assets this one references (by <c>asset:</c> GUID).</summary>
    public List<Guid> Dependencies { get; set; } = [];

    /// <summary>Assets whose content the last import read; a change to one reimports this asset (see <c>AssetImportContext.ImportInputs</c>).</summary>
    public List<Guid> ImportInputs { get; set; } = [];

    /// <summary>Errors and warnings from the last import.</summary>
    public List<string> Messages { get; set; } = [];

    /// <summary>Importer facts such as texture width and height.</summary>
    public Dictionary<string, string> Properties { get; set; } = [];

    [JsonIgnore]
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>Import settings of texture assets, stored in the asset's .meta file.</summary>
public sealed class TextureImportSettings
{
    public TextureFilter Filter { get; set; } = TextureFilter.Linear;

    public TextureWrap Wrap { get; set; } = TextureWrap.Clamp;

    public bool GenerateMipmaps { get; set; }

    /// <summary>Texels per world unit; sprites created from the texture get size = pixels / PixelsPerUnit.</summary>
    public float PixelsPerUnit { get; set; } = 100f;

    /// <summary>SVG textures: pixels per SVG unit when rasterized (2 renders a 100 x 100 document at 200 x 200).</summary>
    public float SvgScale { get; set; } = 1f;

    public SamplerState ToSampler() => new(Filter, Wrap, GenerateMipmaps);
}

public sealed record AssetRefreshResult(
    IReadOnlyList<Guid> Added,
    IReadOnlyList<Guid> Reimported,
    IReadOnlyList<Guid> Moved,
    IReadOnlyList<Guid> Removed)
{
    public bool HasChanges => Added.Count + Reimported.Count + Moved.Count + Removed.Count > 0;

    public IEnumerable<Guid> All => Added.Concat(Reimported).Concat(Moved).Concat(Removed);
}
