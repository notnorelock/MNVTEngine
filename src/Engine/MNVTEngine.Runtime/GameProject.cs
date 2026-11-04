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

/// <summary>A named export configuration (<c>"buildProfiles"</c> in the .mnvtproj), used by <c>mnvt build</c>.</summary>
public sealed class BuildProfile
{
    public static readonly IReadOnlyList<string> SupportedTargets = ["win-x64"];
    public static readonly IReadOnlyList<string> Configurations = ["Debug", "Release"];

    public string Name { get; set; } = "release";

    /// <summary>"Debug" or "Release".</summary>
    public string Configuration { get; set; } = "Release";

    /// <summary>.NET runtime identifier; see <see cref="SupportedTargets"/>.</summary>
    public string Target { get; set; } = "win-x64";

    /// <summary>Relative to the project root; defaults to <c>Builds/&lt;name&gt;</c>.</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>File name of the game executable without extension; defaults to the project name without spaces.</summary>
    public string? ExecutableName { get; set; }

    /// <summary>Show a console window with the log; defaults to true for Debug and false for Release.</summary>
    public bool? Console { get; set; }

    [JsonIgnore]
    public bool ShowsConsole => Console ?? Configuration == "Debug";

    public static IReadOnlyList<BuildProfile> Defaults =>
    [
        new() { Name = "debug", Configuration = "Debug" },
        new() { Name = "release", Configuration = "Release" },
    ];

    public BuildProfile Clone() => (BuildProfile)MemberwiseClone();
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

    /// <summary>Runtime debug UI, debug drawing, profiler and performance budgets (the "diagnostics" section).</summary>
    public Diagnostics.DiagnosticsSettings Diagnostics { get; set; } = new();

    /// <summary>Export profiles; <see cref="BuildProfile.Defaults"/> (debug, release) when the file defines none.</summary>
    public List<BuildProfile> BuildProfiles { get; set; } = [];

    [JsonIgnore]
    public IReadOnlyList<BuildProfile> EffectiveBuildProfiles => BuildProfiles.Count > 0 ? BuildProfiles : BuildProfile.Defaults;

    [JsonIgnore]
    public string ProjectFile { get; private set; } = "";

    [JsonIgnore]
    public string RootDirectory { get; private set; } = "";

    [JsonIgnore]
    public string AssetsDirectory => Path.Combine(RootDirectory, "Assets");

    /// <param name="path">A .mnvtproj file, or a directory containing exactly one.</param>
    /// <exception cref="SerializationException">The file is unreadable or fails validation.</exception>
    public static GameProject Load(string path)
    {
        var file = Path.GetFullPath(FindProjectFile(path));
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (IOException ex)
        {
            throw new SerializationException(file, ex.Message, [], ex);
        }

        return Parse(text, file, Path.GetDirectoryName(file)!, null);
    }

    /// <summary>The project of an exported game, stored at the root of its globals archive.</summary>
    /// <param name="dataDirectory">The game's data folder; becomes <see cref="RootDirectory"/> (there are no project files on disk).</param>
    /// <exception cref="SerializationException">Missing, unreadable or invalid project.</exception>
    public static GameProject LoadPackaged(ContentArchiveSet archive, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var entries = archive.Entries.Where(e => !e.Contains('/', StringComparison.Ordinal) && e.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)).ToList();
        if (entries.Count != 1)
        {
            throw new SerializationException(archive.Directory, $"The game content must contain exactly one {Extension} file, found {entries.Count}.", []);
        }

        string text;
        try
        {
            text = System.Text.Encoding.UTF8.GetString(archive.Read(entries[0]));
        }
        catch (InvalidDataException ex)
        {
            throw new SerializationException(entries[0], ex.Message, [], ex);
        }

        return Parse(text, entries[0], dataDirectory, path => archive.Contains(AssetDatabase.AssetsEntryFolder + AssetPaths.Canonical(path)));
    }

    /// <param name="assetExists">Checks a path relative to Assets; null to look on disk. Packaged projects have no scripts project.</param>
    private static GameProject Parse(string text, string file, string rootDirectory, Func<string, bool>? assetExists)
    {
        var diagnostics = new List<SerializationDiagnostic>();
        GameProject project;
        try
        {
            var document = JObject.Parse(text);
            new JsonMigrationPipeline(CurrentSchemaVersion).Migrate(document, diagnostics);
            project = document.ToObject<GameProject>(MnvtJson.CreateSerializer())
                ?? throw new InvalidDataException("Project file is empty.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new SerializationException(file, ex.Message, diagnostics, ex);
        }

        project.ProjectFile = file;
        project.RootDirectory = rootDirectory;
        project.Validate(diagnostics, assetExists ?? (path => File.Exists(project.ResolveAsset(path))));
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            throw new SerializationException(file, string.Join(" ", diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Message)), diagnostics);
        }

        return project;
    }

    /// <summary>Writes the "diagnostics" section of the project file, leaving the rest of the file as it is.</summary>
    /// <exception cref="IOException">The project file cannot be read or written.</exception>
    /// <exception cref="JsonException">The project file is no longer valid JSON.</exception>
    public void SaveDiagnostics(Diagnostics.DiagnosticsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var document = JObject.Parse(File.ReadAllText(ProjectFile));
        document["diagnostics"] = JObject.FromObject(settings, MnvtJson.CreateSerializer());
        File.WriteAllText(ProjectFile, document.ToString(Formatting.Indented) + Environment.NewLine);
        Diagnostics = settings.Clone();
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

    private void Validate(List<SerializationDiagnostic> diagnostics, Func<string, bool> assetExists)
    {
        void Error(string path, string message) => diagnostics.Add(new SerializationDiagnostic(DiagnosticSeverity.Error, path, message));

        Diagnostics ??= new Diagnostics.DiagnosticsSettings();
        foreach (var problem in Diagnostics.Validate())
        {
            diagnostics.Add(new SerializationDiagnostic(DiagnosticSeverity.Warning, "diagnostics", problem));
        }

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

        ValidateBuildProfiles(Error);

        try
        {
            if (!assetExists(StartupScene))
            {
                Error("startupScene", $"Startup scene '{StartupScene}' not found under Assets.");
            }
        }
        catch (ArgumentException ex)
        {
            Error("startupScene", ex.Message);
        }
    }

    private void ValidateBuildProfiles(Action<string, string> error)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < BuildProfiles.Count; i++)
        {
            var profile = BuildProfiles[i];
            var path = $"buildProfiles[{i}]";
            if (!GamePackage.IsValidExecutableName(profile.Name) || !names.Add(profile.Name))
            {
                error(path + ".name", $"Build profile names must be unique and use letters, digits, '_', '-', '.' ('{profile.Name}').");
            }

            if (!BuildProfile.Configurations.Contains(profile.Configuration))
            {
                error(path + ".configuration", $"Configuration must be one of {string.Join(", ", BuildProfile.Configurations)}, got '{profile.Configuration}'.");
            }

            if (!BuildProfile.SupportedTargets.Contains(profile.Target))
            {
                error(path + ".target", $"Target '{profile.Target}' is not supported; supported: {string.Join(", ", BuildProfile.SupportedTargets)}.");
            }

            if (profile.ExecutableName is { } exe && !GamePackage.IsValidExecutableName(exe))
            {
                error(path + ".executableName", $"'{exe}' is not a valid executable name (letters, digits, '_', '-', '.'; must start with a letter).");
            }

            if (profile.OutputDirectory is { } output)
            {
                try
                {
                    ResolveProjectFile(output);
                }
                catch (ArgumentException ex)
                {
                    error(path + ".outputDirectory", ex.Message);
                }
            }
        }
    }
}
