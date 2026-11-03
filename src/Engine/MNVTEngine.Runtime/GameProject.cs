using MNVTEngine.Assets;
using MNVTEngine.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Runtime;

public sealed class ScriptSettings
{
    /// <summary>C# class library with the game's components, relative to the project root, e.g. "Scripts/MyGame.Scripts.csproj".</summary>
    public string? Project { get; set; }
}

public sealed class WindowSettings
{
    /// <summary>Defaults to the project name.</summary>
    public string? Title { get; set; }

    public int Width { get; set; } = 1280;

    public int Height { get; set; } = 720;

    public bool VSync { get; set; } = true;

    public bool Resizable { get; set; } = true;
}

/// <summary>
/// The <c>.mnvtproj</c> file at the root of a game project. Asset paths everywhere are relative to the
/// project's <c>Assets</c> folder and may not escape it.
/// </summary>
public sealed class GameProject
{
    public const string Extension = ".mnvtproj";
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Name { get; set; } = "Game";

    /// <summary>Scene loaded at startup, relative to Assets, e.g. "Scenes/Main.mnvt".</summary>
    public string StartupScene { get; set; } = "";

    public WindowSettings Window { get; set; } = new();

    public float FixedTimestep { get; set; } = 1f / 60f;

    /// <summary>Optional C# game code; null when the game uses only built-in components.</summary>
    public ScriptSettings? Scripts { get; set; }

    [JsonIgnore]
    public string RootDirectory { get; private set; } = "";

    [JsonIgnore]
    public string AssetsDirectory => Path.Combine(RootDirectory, "Assets");

    /// <param name="path">A .mnvtproj file, or a directory containing exactly one.</param>
    /// <exception cref="SerializationException">The file is unreadable or fails validation.</exception>
    public static GameProject Load(string path)
    {
        var file = FindProjectFile(path);
        var diagnostics = new List<SerializationDiagnostic>();
        GameProject project;
        try
        {
            var document = JObject.Parse(File.ReadAllText(file));
            new JsonMigrationPipeline(CurrentSchemaVersion).Migrate(document, diagnostics);
            project = document.ToObject<GameProject>(MnvtJson.CreateSerializer())
                ?? throw new InvalidDataException("Project file is empty.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            throw new SerializationException(file, ex.Message, diagnostics, ex);
        }

        project.RootDirectory = Path.GetDirectoryName(Path.GetFullPath(file))!;
        project.Validate(diagnostics);
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            throw new SerializationException(file, string.Join(" ", diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Message)), diagnostics);
        }

        return project;
    }

    /// <exception cref="FileNotFoundException">No project file, or several in the directory.</exception>
    public static string FindProjectFile(string path)
    {
        if (File.Exists(path))
        {
            return path;
        }

        if (Directory.Exists(path))
        {
            var candidates = Directory.GetFiles(path, "*" + Extension);
            if (candidates.Length == 1)
            {
                return candidates[0];
            }

            throw new FileNotFoundException(candidates.Length == 0
                ? $"No {Extension} file in '{path}'."
                : $"Several {Extension} files in '{path}'; pass one explicitly.");
        }

        throw new FileNotFoundException($"Project '{path}' does not exist.");
    }

    /// <exception cref="ArgumentException">The path is absolute or escapes the Assets folder.</exception>
    public string ResolveAsset(string relativePath) => AssetPaths.Resolve(AssetsDirectory, relativePath);

    /// <summary>Resolves a path relative to the project root, refusing anything outside it.</summary>
    /// <exception cref="ArgumentException">The path is absolute or escapes the project folder.</exception>
    public string ResolveProjectFile(string relativePath) => AssetPaths.Resolve(RootDirectory, relativePath);

    private void Validate(List<SerializationDiagnostic> diagnostics)
    {
        void Error(string path, string message) => diagnostics.Add(new SerializationDiagnostic(DiagnosticSeverity.Error, path, message));

        if (string.IsNullOrWhiteSpace(Name))
        {
            Error("name", "Project name is required.");
        }

        if (!(FixedTimestep > 0f && FixedTimestep <= 1f))
        {
            Error("fixedTimestep", $"fixedTimestep must be in (0, 1] seconds, got {FixedTimestep}.");
        }

        if (Window.Width <= 0 || Window.Height <= 0)
        {
            Error("window", "Window size must be positive.");
        }

        if (Scripts?.Project is { Length: > 0 } scripts)
        {
            try
            {
                if (!File.Exists(ResolveProjectFile(scripts)))
                {
                    Error("scripts.project", $"Scripts project '{scripts}' not found.");
                }
            }
            catch (ArgumentException ex)
            {
                Error("scripts.project", ex.Message);
            }
        }

        try
        {
            if (!File.Exists(ResolveAsset(StartupScene)))
            {
                Error("startupScene", $"Startup scene '{StartupScene}' not found under Assets.");
            }
        }
        catch (ArgumentException ex)
        {
            Error("startupScene", ex.Message);
        }
    }
}
