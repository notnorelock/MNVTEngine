using System.Text.RegularExpressions;
using MNVTEngine.Assets;
using MNVTEngine.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Runtime;

/// <summary>One file of an exported game, relative to the package root, with its SHA-256.</summary>
public sealed record PackagedFile(string Path, long Size, string Sha256);

/// <summary>
/// <c>Content/package.json</c> of an exported game: what was built, how, and a hash of every file in the package.
/// Written by <c>mnvt build</c>; the game host reads it to find the game's C# code, and build validation checks the
/// files against it. Contains no timestamps or machine paths, so identical inputs give an identical manifest.
/// </summary>
public sealed partial class GamePackage
{
    public const string ContentFolder = "Content";
    public const string ManifestFile = "package.json";
    public const string BytecodeFolder = "Bytecode";
    public const string ScriptsFolder = "Scripts";
    public const string BytecodeExtension = ".mnvtb";
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Name { get; set; } = "";

    public string ExecutableName { get; set; } = "";

    public string Profile { get; set; } = "";

    public string Configuration { get; set; } = "";

    public string Target { get; set; } = "";

    public string EngineVersion { get; set; } = "";

    /// <summary>The game's C# code, relative to the Content folder (e.g. "Scripts/MyGame.Scripts.dll"); null without C# scripts.</summary>
    public string? ScriptsAssembly { get; set; }

    /// <summary>Every file of the package except this manifest, sorted by path.</summary>
    public List<PackagedFile> Files { get; set; } = [];

    /// <summary>Bytecode of the asset with this GUID, inside a package's Content folder.</summary>
    public static string BytecodePath(string contentDirectory, Guid asset) =>
        Path.Combine(contentDirectory, BytecodeFolder, asset.ToString("D") + BytecodeExtension);

    /// <summary>True if <paramref name="contentDirectory"/> is the Content folder of an exported game.</summary>
    public static bool IsPackage(string contentDirectory) => File.Exists(Path.Combine(contentDirectory, ManifestFile));

    public static bool IsValidExecutableName(string name) => ExecutableNamePattern().IsMatch(name ?? "");

    /// <summary>Executable name derived from a project name: letters, digits, '_', '-' and '.' are kept; anything else is dropped.</summary>
    public static string ExecutableNameFor(string projectName)
    {
        ArgumentNullException.ThrowIfNull(projectName);
        var name = new string([.. projectName.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')]).TrimStart('-', '.', '_');
        while (name.Length > 0 && char.IsAsciiDigit(name[0]))
        {
            name = name[1..];
        }

        return name.Length > 0 ? name : "Game";
    }

    /// <exception cref="SerializationException">The manifest is missing, malformed, or from a newer engine.</exception>
    public static GamePackage Load(string contentDirectory)
    {
        var file = Path.Combine(contentDirectory, ManifestFile);
        var diagnostics = new List<SerializationDiagnostic>();
        try
        {
            var document = JObject.Parse(File.ReadAllText(file));
            new JsonMigrationPipeline(CurrentSchemaVersion).Migrate(document, diagnostics);
            var package = document.ToObject<GamePackage>(MnvtJson.CreateSerializer()) ?? throw new InvalidDataException("Manifest is empty.");
            if (package.ScriptsAssembly is { } scripts)
            {
                AssetPaths.Resolve(contentDirectory, scripts); // refuses paths outside the package
            }

            return package;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or ArgumentException)
        {
            throw new SerializationException(file, ex.Message, diagnostics, ex);
        }
    }

    public void Save(string contentDirectory)
    {
        Files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        File.WriteAllText(Path.Combine(contentDirectory, ManifestFile), JObject.FromObject(this, MnvtJson.CreateSerializer()).ToString(Formatting.Indented));
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.\-]{0,63}$")]
    private static partial Regex ExecutableNamePattern();
}
