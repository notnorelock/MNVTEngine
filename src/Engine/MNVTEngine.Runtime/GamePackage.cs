using System.Text.RegularExpressions;
using MNVTEngine.Assets;
using MNVTEngine.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Runtime;

/// <summary>One file of an exported game, relative to the package root, with its SHA-256.</summary>
public sealed record PackagedFile(string Path, long Size, string Sha256);

/// <summary>
/// Layout of an exported game and its manifest, <c>&lt;Game&gt;_Data/package.json</c>: what was built, how, and a hash of
/// every file in the game folder. Written by <c>mnvt build</c>; the game host reads it to find the game's C# code, and
/// build validation checks the files against it. Contains no timestamps or machine paths, so identical inputs give an
/// identical manifest.
/// <code>
/// Game.exe, Game.dll, Game.runtimeconfig.json
/// Dependencies/Managed   engine and package assemblies
/// Dependencies/Native    native libraries (glfw3.dll)
/// Dependencies/Runtime   private .NET runtime (host/fxr, shared/Microsoft.NETCore.App)
/// Game_Data/content.mnvtpak  encrypted content: assets, asset index, bytecode, C# game code
/// Game_Data/package.json
/// </code>
/// </summary>
public sealed partial class GamePackage
{
    public const string ManifestFile = "package.json";
    public const string ArchiveFile = "content" + ContentArchive.Extension;
    public const string ManagedFolder = "Dependencies/Managed";
    public const string NativeFolder = "Dependencies/Native";
    public const string RuntimeFolder = "Dependencies/Runtime";
    public const string DataFolderSuffix = "_Data";
    public const string ScriptsEntryFolder = "Scripts/";

    /// <summary>Assembly metadata key of the game host that carries the content key.</summary>
    public const string ContentKeyMetadata = "MNVTEngine.ContentKey";

    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Name { get; set; } = "";

    public string ExecutableName { get; set; } = "";

    public string Profile { get; set; } = "";

    public string Configuration { get; set; } = "";

    public string Target { get; set; } = "";

    public string EngineVersion { get; set; } = "";

    /// <summary>True when the in-game debug UI (and Dear ImGui) ships: Debug builds, or Release when the project opts in.</summary>
    public bool DevTools { get; set; }

    /// <summary>The game's C# code: archive entry such as "Scripts/MyGame.Scripts.dll"; null without C# scripts.</summary>
    public string? ScriptsAssembly { get; set; }

    /// <summary>Every file of the game folder except this manifest, sorted by path.</summary>
    public List<PackagedFile> Files { get; set; } = [];

    /// <summary>The data folder of a game: <c>&lt;executable&gt;_Data</c>.</summary>
    public static string DataFolder(string executableName) => executableName + DataFolderSuffix;

    /// <summary>The <c>*_Data</c> folder holding a manifest inside <paramref name="gameDirectory"/>, or null if this is not an exported game.</summary>
    public static string? FindDataDirectory(string gameDirectory) =>
        Directory.Exists(gameDirectory)
            ? Directory.EnumerateDirectories(gameDirectory, "*" + DataFolderSuffix).FirstOrDefault(d => File.Exists(Path.Combine(d, ManifestFile)))
            : null;

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
    public static GamePackage Load(string dataDirectory)
    {
        var file = Path.Combine(dataDirectory, ManifestFile);
        var diagnostics = new List<SerializationDiagnostic>();
        try
        {
            var document = JObject.Parse(File.ReadAllText(file));
            new JsonMigrationPipeline(CurrentSchemaVersion).Migrate(document, diagnostics);
            var package = document.ToObject<GamePackage>(MnvtJson.CreateSerializer()) ?? throw new InvalidDataException("Manifest is empty.");
            if (package.ScriptsAssembly is { } scripts)
            {
                AssetPaths.Canonical(scripts); // refuses absolute or escaping paths
            }

            return package;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or ArgumentException)
        {
            throw new SerializationException(file, ex.Message, diagnostics, ex);
        }
    }

    public void Save(string dataDirectory)
    {
        Files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        File.WriteAllText(Path.Combine(dataDirectory, ManifestFile), JObject.FromObject(this, MnvtJson.CreateSerializer()).ToString(Formatting.Indented));
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.\-]{0,63}$")]
    private static partial Regex ExecutableNamePattern();
}
