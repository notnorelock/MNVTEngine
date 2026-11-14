using MNVTEngine.Assets;
using MNVTEngine.Modules;
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

    /// <summary>"windowed", "borderless", "fullscreen" or "borderlessFullscreen".</summary>
    public WindowMode Mode { get; set; } = WindowMode.Windowed;

    /// <summary>Texture asset (path under Assets, e.g. "Icons/game.png") for the window and taskbar icon; null for the default.</summary>
    public string? Icon { get; set; }

    /// <summary>Size limits while resizable (0 = none).</summary>
    public int MinWidth { get; set; }

    public int MinHeight { get; set; }

    public int MaxWidth { get; set; }

    public int MaxHeight { get; set; }

    /// <summary>Initial top-left position in screen coordinates; both null centers the window.</summary>
    public int? X { get; set; }

    public int? Y { get; set; }

    public bool TopMost { get; set; }
}

/// <summary>
/// The startup splash: background, logo, title and subtitle, shown while the first scene loads, for at least
/// <see cref="MinimumDuration"/> seconds, with fades. Off by default; engine branding is opt-in
/// (<see cref="ShowEngineBranding"/>), never forced.
/// </summary>
public sealed class SplashScreenSettings
{
    public bool Enabled { get; set; }

    public Mathematics.Color Background { get; set; } = new(0f, 0f, 0f, 1f);

    public Mathematics.Color Foreground { get; set; } = new(0.92f, 0.93f, 0.95f, 1f);

    /// <summary>Texture asset path (e.g. "Branding/logo.png").</summary>
    public string? Logo { get; set; }

    /// <summary>Background image asset path, stretched to cover the window.</summary>
    public string? BackgroundImage { get; set; }

    /// <summary>Defaults to none (only the logo).</summary>
    public string? Title { get; set; }

    public string? Subtitle { get; set; }

    public float MinimumDuration { get; set; } = 2f;

    public float FadeIn { get; set; } = 0.4f;

    public float FadeOut { get; set; } = 0.4f;

    /// <summary>A key or click skips the rest of the splash once loading has finished.</summary>
    public bool Skippable { get; set; } = true;

    /// <summary>Adds "Made with MNVTEngine".</summary>
    public bool ShowEngineBranding { get; set; }

    /// <summary>Shows a progress bar of the startup load on the splash.</summary>
    public bool ShowProgress { get; set; }
}

/// <summary>Defaults for <see cref="MNVTEngine.LoadingScreen.Options"/>; game code can change them at run time.</summary>
public sealed class LoadingScreenSettings
{
    /// <summary>Shown for scene loads; off keeps the previous scene visible (frozen) while the next one loads.</summary>
    public bool Enabled { get; set; } = true;

    public string Title { get; set; } = "Loading...";

    public Mathematics.Color Background { get; set; } = new(0.04f, 0.04f, 0.05f, 1f);

    public Mathematics.Color Foreground { get; set; } = new(0.92f, 0.93f, 0.95f, 1f);

    public Mathematics.Color Accent { get; set; } = new(0.27f, 0.55f, 1f, 1f);

    public string? Logo { get; set; }

    /// <summary>Font asset path (TTF/OTF); the built-in font when null.</summary>
    public string? Font { get; set; }

    public bool ShowProgress { get; set; } = true;

    public bool ShowPercentage { get; set; } = true;

    public List<string> Tips { get; set; } = [];

    public float TipSeconds { get; set; } = 5f;
}

/// <summary>Texture streaming and asset memory (<c>"streaming"</c> in the project file).</summary>
public sealed class StreamingSettings
{
    /// <summary>
    /// Exported games show large textures as a 64-pixel proxy first and load the full image in the background, and drop
    /// full images back to their proxy when memory runs short. Development always loads full textures.
    /// </summary>
    public bool Textures { get; set; } = true;

    /// <summary>Full-resolution streamed textures beyond this many megabytes are released (least recently drawn first); 0 = only under memory pressure.</summary>
    public int TextureBudgetMB { get; set; }

    /// <summary>A texture not drawn for this long may lose its full image (budget, memory pressure), and is unloaded after a scene change.</summary>
    public float UnusedSeconds { get; set; } = 10f;
}

/// <summary>
/// Content mounted after the game's own packages (<c>"content"</c> in the project file); see
/// docs/architecture/virtual-filesystem.md#mods-and-extra-content.
/// </summary>
public sealed class ContentSettings
{
    /// <summary>
    /// Mount <c>&lt;Game&gt;_Data/DLC/*.mnvtpack</c>: official extra content, which must be signed with the game's key and
    /// may hold code and encrypted content. On by default (an empty or missing folder mounts nothing).
    /// </summary>
    public bool Dlc { get; set; } = true;

    /// <summary>
    /// Mount <c>Mods/*.mnvtpack</c> next to the executable: user content, unencrypted, signed or not, never trusted with
    /// code (no C# assemblies are served from it). Off unless the game opts in.
    /// </summary>
    public bool Mods { get; set; }

    /// <summary>
    /// C# code of DLC: DLC name (<c>mnvt package dlc --name</c>) to a class library project relative to the project root,
    /// which references the game's scripts project. <c>mnvt package dlc</c> builds and packages it; ahead-of-time builds
    /// of the game compile it in, and enable its components when that DLC is mounted.
    /// </summary>
    public Dictionary<string, string> DlcCode { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>A named export configuration (<c>"buildProfiles"</c> in the .mnvtproj), used by <c>mnvt build</c>.</summary>
public sealed class BuildProfile
{
    public static readonly IReadOnlyList<string> SupportedTargets = ["win-x64"];
    public static readonly IReadOnlyList<string> Configurations = ["Debug", "Release"];

    /// <summary>
    /// How the game's C# runs: "jit" (CoreCLR, IL assemblies, the default) or "aot" (NativeAOT: one native executable,
    /// no .NET runtime). There is no IL-to-C++ ("il2cpp") backend; see docs/architecture/il2cpp-aot-pipeline.md.
    /// </summary>
    public static readonly IReadOnlyList<string> ScriptingBackends = ["jit", "aot"];

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

    /// <summary>
    /// Leave out the framework assemblies the game cannot reach from its references (default). Turn off for games that
    /// load framework assemblies by name at run time.
    /// </summary>
    public bool PruneFramework { get; set; } = true;

    /// <summary>See <see cref="ScriptingBackends"/>.</summary>
    public string ScriptingBackend { get; set; } = "jit";

    /// <summary>Package compression: "none", "fast" (LZ4), "balanced" (Zstandard 9, default) or "small" (Zstandard 19).</summary>
    public IO.Packages.PackageCompression Compression { get; set; } = IO.Packages.PackageCompression.Balanced;

    /// <summary>Compression per file type, e.g. <c>{ ".wav": "fast" }</c>; already-compressed types (PNG, Ogg, ...) are stored as is unless listed.</summary>
    public Dictionary<string, IO.Packages.PackageCompression> CompressionByExtension { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Encrypt the packages (default). Off writes plain packages, e.g. to inspect a debug build with other tools; they are still signed and verified.</summary>
    public bool Encrypt { get; set; } = true;

    [JsonIgnore]
    public bool ShowsConsole => Console ?? Configuration == "Debug";

    public static IReadOnlyList<BuildProfile> Defaults =>
    [
        new() { Name = "debug", Configuration = "Debug" },
        new() { Name = "release", Configuration = "Release" },
    ];

    public BuildProfile Clone()
    {
        var clone = (BuildProfile)MemberwiseClone();
        clone.CompressionByExtension = new(CompressionByExtension, StringComparer.OrdinalIgnoreCase);
        return clone;
    }
}

/// <summary>
/// The <c>.mnvtproj</c> file at the root of a game project. Asset paths everywhere are relative to the
/// project's <c>Assets</c> folder and may not escape it.
/// </summary>
public sealed class GameProject
{
    public const string Extension = ".mnvtproj";

    /// <summary>2 added <see cref="Profile"/>; version 1 files load without one and are migrated by <see cref="ProjectProfileMigration"/>.</summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Name { get; set; } = "Game";

    /// <summary>
    /// The project's primary rendering mode, "2d" or "3d" (<c>"profile"</c>). Required from schema version 2; null only
    /// for a version 1 file that has not been migrated yet (<see cref="NeedsProfileMigration"/>).
    /// </summary>
    public ProjectProfile? Profile { get; set; }

    /// <summary>
    /// Module switches (<c>"modules": { "terrain": false, "devtools": true }</c>) on top of the profile's defaults; see
    /// <see cref="ProjectModules"/>. Unknown, incompatible or required modules are reported when the modules are resolved.
    /// </summary>
    public Dictionary<string, bool> Modules { get; set; } = new(StringComparer.Ordinal);

    private const string DevToolsModule = "devtools";

    /// <summary>A pre-profile (schema version 1) project: its profile must be inferred or chosen, then saved.</summary>
    [JsonIgnore]
    public bool NeedsProfileMigration => Profile is null;

    /// <summary>Scene loaded at startup, relative to Assets, e.g. "Scenes/Main.mnvt".</summary>
    public string StartupScene { get; set; } = "";

    public WindowSettings Window { get; set; } = new();

    /// <summary>Startup splash (<c>"splashScreen"</c>); off unless the project enables it.</summary>
    public SplashScreenSettings SplashScreen { get; set; } = new();

    /// <summary>Screen shown while scenes load (<c>"loadingScreen"</c>).</summary>
    public LoadingScreenSettings LoadingScreen { get; set; } = new();

    public float FixedTimestep { get; set; } = 1f / 60f;

    /// <summary>Texture streaming and asset memory (<c>"streaming"</c>).</summary>
    public StreamingSettings Streaming { get; set; } = new();

    /// <summary>Extra content mounted at startup (<c>"content"</c>): DLC and mods.</summary>
    public ContentSettings Content { get; set; } = new();

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

    /// <summary>
    /// Package entry with an exported game's runtime configuration: the project's settings the game needs, without
    /// development settings (C# project, build profiles), marked with <c>"kind": "mnvt.runtimeConfig"</c>. It is not a
    /// project file, and <see cref="Load"/> refuses it even when renamed to one.
    /// </summary>
    public const string RuntimeConfigEntry = "RuntimeConfig.json";

    /// <summary>Value of the <c>"kind"</c> property that marks a runtime configuration.</summary>
    public const string RuntimeConfigKind = "mnvt.runtimeConfig";

    private const string KindProperty = "kind";

    /// <summary>The configuration of an exported game, from its globals package (<see cref="RuntimeConfigEntry"/>).</summary>
    /// <exception cref="SerializationException">Missing, unreadable or invalid configuration.</exception>
    public static GameProject LoadPackaged(GameContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        string text;
        try
        {
            text = System.Text.Encoding.UTF8.GetString(content.Read(RuntimeConfigEntry));
        }
        catch (IOException ex)
        {
            throw new SerializationException(RuntimeConfigEntry, ex is FileNotFoundException ? $"The game content has no {RuntimeConfigEntry}." : ex.Message, [], ex);
        }

        return Parse(text, RuntimeConfigEntry, content.Directory, path => content.Contains(AssetDatabase.AssetsEntryFolder + AssetPaths.Canonical(path)), runtimeConfig: true);
    }

    /// <param name="assetExists">Checks a path relative to Assets; null to look on disk. Packaged projects have no scripts project.</param>
    /// <param name="runtimeConfig">Parse an exported game's runtime configuration (which must be marked as one) instead of a project file (which must not).</param>
    private static GameProject Parse(string text, string file, string rootDirectory, Func<string, bool>? assetExists, bool runtimeConfig = false)
    {
        var diagnostics = new List<SerializationDiagnostic>();
        GameProject project;
        try
        {
            var document = JObject.Parse(text);
            var isRuntimeConfig = document.Value<string>(KindProperty) == RuntimeConfigKind;
            if (isRuntimeConfig != runtimeConfig)
            {
                throw runtimeConfig
                    ? new InvalidDataException($"{RuntimeConfigEntry} is not an exported game's runtime configuration.")
                    : new NotAProjectException(file, ProjectFolderKind.ExportedGame);
            }

            document.Remove(KindProperty);
            document.Remove("buildId");
            var original = new JsonMigrationPipeline(CurrentSchemaVersion)
                .Add(1, _ => { }) // the profile cannot be read from the file; ProjectProfileMigration infers it from the content
                .Migrate(document, diagnostics);
            switch (document["profile"])
            {
                case null when original >= 2:
                    throw new InvalidDataException("'profile' is required: \"2d\" or \"3d\".");
                case null:
                    break;
                case JValue { Type: JTokenType.String } value when ProjectProfiles.TryParse(value.Value<string>(), out var parsed):
                    document["profile"] = parsed.Id();
                    break;
                case var other:
                    throw new InvalidDataException($"Unknown profile {other.ToString(Formatting.None)}; use \"2d\" or \"3d\".");
            }

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
        var saved = settings.Clone();
        saved.IncludeInRelease = false; // stored as the devtools switch
        document["diagnostics"] = JObject.FromObject(saved, MnvtJson.CreateSerializer());
        if (settings.IncludeInRelease)
        {
            Modules[DevToolsModule] = true;
        }
        else if (Modules.GetValueOrDefault(DevToolsModule))
        {
            Modules.Remove(DevToolsModule); // back to the default: on in Debug builds, off in Release
        }

        document["modules"] = new JObject(Modules.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => new JProperty(m.Key, m.Value)));
        if (Modules.Count == 0)
        {
            document.Remove("modules");
        }

        File.WriteAllText(ProjectFile, document.ToString(Formatting.Indented) + Environment.NewLine);
        Diagnostics = settings.Clone();
    }

    /// <summary>
    /// Writes <paramref name="profile"/> into the project file (and raises a version 1 file to the current schema),
    /// leaving every other setting as it is. A version 1 file is first copied to <c>&lt;file&gt;.v1.bak</c> (an existing
    /// backup is kept); the new file is written next to the old one and then swapped in, so a failure leaves the
    /// original intact.
    /// </summary>
    /// <exception cref="IOException">The project file cannot be read or written.</exception>
    /// <exception cref="JsonException">The project file is no longer valid JSON.</exception>
    public void SaveProfile(ProjectProfile profile)
    {
        var text = File.ReadAllText(ProjectFile);
        var original = JObject.Parse(text);
        var document = new JObject();
        foreach (var property in original.Properties())
        {
            if (property.Name == "profile")
            {
                continue;
            }

            document[property.Name] = property.Name == "schemaVersion" ? CurrentSchemaVersion : property.Value;
            if (property.Name == "name")
            {
                document["profile"] = profile.Id(); // next to the name, where people look for it
            }
        }

        document["schemaVersion"] ??= CurrentSchemaVersion;
        document["profile"] ??= profile.Id();
        if (original.Value<int?>("schemaVersion") is < 2 && !File.Exists(ProjectFile + ".v1.bak"))
        {
            File.WriteAllText(ProjectFile + ".v1.bak", text);
        }

        var temporary = ProjectFile + ".tmp";
        File.WriteAllText(temporary, document.ToString(Formatting.Indented) + Environment.NewLine);
        File.Move(temporary, ProjectFile, overwrite: true);
        Profile = profile;
        SchemaVersion = CurrentSchemaVersion;
    }

    /// <exception cref="NotAProjectException">The path is an exported game, its packages or its extracted content.</exception>
    /// <exception cref="FileNotFoundException">No project file, or several in the directory.</exception>
    public static string FindProjectFile(string path)
    {
        var kind = Classify(path);
        if (kind is ProjectFolderKind.ExportedGame or ProjectFolderKind.GamePackages)
        {
            throw new NotAProjectException(path, kind);
        }

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

    /// <summary>
    /// What <paramref name="path"/> (a folder or file) is, judged by its content, not by a missing file name: an exported
    /// game (a <c>*_Data</c> folder with a build manifest, its executable, or a runtime configuration even when renamed
    /// to <c>.mnvtproj</c>), a folder of game packages, an editable project, or none of these.
    /// </summary>
    public static ProjectFolderKind Classify(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (File.Exists(path))
        {
            var extension = Path.GetExtension(path);
            var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            return extension.ToUpperInvariant() switch
            {
                ".MNVTPACK" => ProjectFolderKind.GamePackages,
                ".EXE" when File.Exists(Path.Combine(folder, GamePackage.DataFolder(Path.GetFileNameWithoutExtension(path)), GamePackage.ManifestFile)) => ProjectFolderKind.ExportedGame,
                _ when IsRuntimeConfig(path) => ProjectFolderKind.ExportedGame,
                ".MNVTPROJ" => ProjectFolderKind.Project,
                _ => ProjectFolderKind.None,
            };
        }

        if (!Directory.Exists(path))
        {
            return ProjectFolderKind.None;
        }

        if (GamePackage.FindDataDirectory(path) is not null || Directory.EnumerateFiles(path, "*" + Extension).Any(IsRuntimeConfig)
            || (File.Exists(Path.Combine(path, RuntimeConfigEntry)) && IsRuntimeConfig(Path.Combine(path, RuntimeConfigEntry))))
        {
            return ProjectFolderKind.ExportedGame;
        }

        if (File.Exists(Path.Combine(path, GamePackage.ManifestFile)) || Directory.EnumerateFiles(path, "*" + GamePackage.PackageExtension).Any())
        {
            return ProjectFolderKind.GamePackages;
        }

        return Directory.EnumerateFiles(path, "*" + Extension).Any() ? ProjectFolderKind.Project : ProjectFolderKind.None;
    }

    /// <summary>A JSON file marked as an exported game's runtime configuration.</summary>
    private static bool IsRuntimeConfig(string file)
    {
        try
        {
            if (new FileInfo(file).Length > 4 * 1024 * 1024)
            {
                return false;
            }

            return JObject.Parse(File.ReadAllText(file)).Value<string>(KindProperty) == RuntimeConfigKind;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidCastException)
        {
            return false; // not JSON or unreadable: not a runtime configuration
        }
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

        // diagnostics.includeInRelease became the devtools module switch: read as "devtools": true; in memory it mirrors
        // the switch (the editor's checkbox) and SaveDiagnostics writes it back as the switch.
        if (Diagnostics.IncludeInRelease)
        {
            Modules.TryAdd(DevToolsModule, true);
        }

        Diagnostics.IncludeInRelease = Modules.GetValueOrDefault(DevToolsModule);
        foreach (var problem in Diagnostics.Validate())
        {
            diagnostics.Add(new SerializationDiagnostic(DiagnosticSeverity.Warning, "diagnostics", problem));
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            Error("name", "Project name is required.");
        }

        Streaming ??= new StreamingSettings();
        Content ??= new ContentSettings();
        Content.DlcCode ??= new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (dlc, code) in Content.DlcCode)
        {
            if (!GamePackage.IsValidExecutableName(dlc) || string.IsNullOrWhiteSpace(code) || Path.IsPathRooted(code) || !code.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || code.Replace('\\', '/').Split('/').Contains(".."))
            {
                Error("content", $"content.dlcCode: '{dlc}' must map a DLC name to a .csproj path inside the project.");
            }
        }
        if (Streaming.TextureBudgetMB < 0 || !(Streaming.UnusedSeconds >= 0f))
        {
            Error("streaming", "streaming.textureBudgetMB and streaming.unusedSeconds must not be negative.");
        }

        if (!(FixedTimestep > 0f && FixedTimestep <= 1f))
        {
            Error("fixedTimestep", $"fixedTimestep must be in (0, 1] seconds, got {FixedTimestep}.");
        }

        if (Window.Width <= 0 || Window.Height <= 0)
        {
            Error("window", "Window size must be positive.");
        }

        if (Window.MinWidth < 0 || Window.MinHeight < 0 || Window.MaxWidth < 0 || Window.MaxHeight < 0
            || (Window.MaxWidth > 0 && Window.MaxWidth < Window.MinWidth) || (Window.MaxHeight > 0 && Window.MaxHeight < Window.MinHeight))
        {
            Error("window", "Window size limits must be non-negative, and each maximum at least its minimum.");
        }

        if (Window.X.HasValue != Window.Y.HasValue)
        {
            Error("window", "Set both window.x and window.y, or neither (centered).");
        }

        foreach (var (name, path) in new[] { ("splashScreen.logo", SplashScreen.Logo), ("splashScreen.backgroundImage", SplashScreen.BackgroundImage), ("loadingScreen.logo", LoadingScreen.Logo), ("loadingScreen.font", LoadingScreen.Font) })
        {
            try
            {
                if (path is { Length: > 0 } && !assetExists(path))
                {
                    Error(name, $"'{path}' not found under Assets.");
                }
            }
            catch (ArgumentException ex)
            {
                Error(name, ex.Message);
            }
        }

        if (SplashScreen.MinimumDuration < 0 || SplashScreen.FadeIn < 0 || SplashScreen.FadeOut < 0 || LoadingScreen.TipSeconds <= 0)
        {
            Error("splashScreen", "Durations must not be negative (and tip intervals must be positive).");
        }

        if (!Enum.IsDefined(Window.Mode))
        {
            Error("window.mode", $"Unknown window mode {(int)Window.Mode}.");
        }

        if (Window.Icon is { Length: > 0 } icon)
        {
            try
            {
                if (!assetExists(icon))
                {
                    Error("window.icon", $"Window icon '{icon}' not found under Assets.");
                }
            }
            catch (ArgumentException ex)
            {
                Error("window.icon", ex.Message);
            }
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
