using System.Text;
using System.Text.RegularExpressions;
using MNVTEngine.Runtime;
using MNVTEngine.Scripting.Compiler;
using MNVTEngine.Scripting.Missions;
using MNVTEngine.Scripting.Syntax;

namespace MNVTEngine.Editor.Services;

public enum ScriptKind
{
    /// <summary>.mnvts gameplay script: checked live while typing.</summary>
    Mission,

    /// <summary>C# game code: compiled by the script reload controller when saved.</summary>
    CSharp,

    /// <summary>Anything else that is text (JSON assets, shaders, notes).</summary>
    Text,
}

/// <summary>A file open in the script editor (UI-free): text, unsaved changes, and checks.</summary>
public sealed class ScriptDocument
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private DateTime _diskTime;

    private ScriptDocument(string path, string text)
    {
        FullPath = path;
        Text = SavedText = text;
        _diskTime = File.GetLastWriteTimeUtc(path);
        Kind = Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".MNVTS" => ScriptKind.Mission,
            ".CS" => ScriptKind.CSharp,
            _ => ScriptKind.Text,
        };
    }

    public string FullPath { get; }

    public string Name => Path.GetFileName(FullPath);

    public ScriptKind Kind { get; }

    /// <summary>Current (possibly unsaved) text, with '\n' line endings.</summary>
    public string Text { get; set; }

    public string SavedText { get; private set; }

    public bool IsDirty => Text != SavedText;

    /// <exception cref="IOException">The file cannot be read.</exception>
    public static ScriptDocument Open(string path) => new(Path.GetFullPath(path), File.ReadAllText(path).ReplaceLineEndings("\n"));

    /// <summary>Writes the text; the editor's watchers then reimport (.mnvts) or recompile and hot-reload (.cs).</summary>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public void Save()
    {
        var text = OperatingSystem.IsWindows() ? Text.Replace("\n", "\r\n", StringComparison.Ordinal) : Text;
        File.WriteAllText(FullPath, text, Utf8);
        SavedText = Text;
        _diskTime = File.GetLastWriteTimeUtc(FullPath);
    }

    /// <summary>
    /// Picks up changes made outside the editor. Unsaved edits are never overwritten: returns true (and keeps the edits)
    /// when the file changed underneath them, so the UI can warn.
    /// </summary>
    public bool CheckDisk()
    {
        if (!File.Exists(FullPath) || File.GetLastWriteTimeUtc(FullPath) == _diskTime)
        {
            return false;
        }

        _diskTime = File.GetLastWriteTimeUtc(FullPath);
        var disk = File.ReadAllText(FullPath).ReplaceLineEndings("\n");
        if (disk == SavedText)
        {
            return false;
        }

        if (IsDirty)
        {
            return true;
        }

        Text = SavedText = disk;
        return false;
    }

    /// <summary>Compiles a .mnvts text the way the importer does (unknown API functions are warnings). Empty for other kinds.</summary>
    public IReadOnlyList<Diagnostic> Check() => Kind == ScriptKind.Mission
        ? ScriptCompiler.Compile(Text, Name, new CompileOptions { Api = StandardApi.All, AllowUnknownFunctions = true }).Diagnostics
        : [];
}

/// <summary>Starting points for new scripts, so nobody has to remember the boilerplate.</summary>
public static partial class ScriptTemplates
{
    public static bool IsValidName(string name) => Identifier().IsMatch(name ?? "");

    /// <summary>Writes <c>Scripts/&lt;Name&gt;.cs</c> with a component skeleton in the project's namespace.</summary>
    /// <exception cref="ArgumentException">Invalid class name.</exception>
    /// <exception cref="IOException">The file already exists, or the project has no C# scripts project.</exception>
    public static string CreateComponent(GameProject project, string name)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!IsValidName(name))
        {
            throw new ArgumentException($"'{name}' is not a valid C# class name.", nameof(name));
        }

        var scripts = project.Scripts?.Project is { Length: > 0 } relative
            ? Path.GetDirectoryName(project.ResolveProjectFile(relative))!
            : throw new IOException("This project has no C# scripts project (\"scripts\" in the project file).");
        var path = Path.Combine(scripts, name + ".cs");
        var ns = Identifier().IsMatch(project.Name.Replace(' ', '_').Replace('-', '_')) ? project.Name.Replace(' ', '_').Replace('-', '_') : "Game";
        return Write(path, $$"""
            using MNVTEngine;
            using MNVTEngine.Mathematics;

            namespace {{ns}};

            /// <summary>Describe what {{name}} does.</summary>
            public sealed class {{name}} : Component
            {
                // Public fields are saved with the scene and shown in the Inspector.
                public float Speed = 1f;

                public override void Start()
                {
                }

                public override void Update()
                {
                }
            }

            """);
    }

    /// <summary>Writes <c>Assets/&lt;folder&gt;/&lt;name&gt;.mnvts</c> with a one-objective mission.</summary>
    /// <exception cref="ArgumentException">Invalid name.</exception>
    /// <exception cref="IOException">The file already exists.</exception>
    public static string CreateMission(GameProject project, string name, string folder = "Missions")
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!IsValidName(name))
        {
            throw new ArgumentException($"'{name}' is not a valid mission name (letters, digits, '_').", nameof(name));
        }

        var id = string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
        return Write(project.ResolveAsset(Path.Combine(folder, name + ".mnvts")), $$"""
            // Run with a MissionRunner component: Script = this file, Start Mission = "{{id}}".
            mission "{{id}}" {
                name = "{{name}}"

                on start { objective.activate("first") }

                objective "first" {
                    description = "Describe the goal"
                    wait until player.distance_to("Goal") < 1
                    objective.complete()
                    mission.complete()
                }

                on complete { log("{{name}} complete") }
            }

            """);
    }

    private static string Write(string path, string text)
    {
        if (File.Exists(path))
        {
            throw new IOException($"'{path}' already exists.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ReplaceLineEndings(OperatingSystem.IsWindows() ? "\r\n" : "\n"), new UTF8Encoding(false));
        return path;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex Identifier();
}
