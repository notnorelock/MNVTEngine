using System.Diagnostics;
using System.Globalization;
using Newtonsoft.Json;

namespace MNVTEngine.Editor.Services;

public enum ExternalEditorKind
{
    /// <summary>Whatever Windows opens the file type with.</summary>
    SystemDefault,

    /// <summary>VS Code with the project folder open, at the file and line.</summary>
    VisualStudioCode,

    /// <summary>Any program, with an argument template (<see cref="EditorPreferences.CustomEditorArguments"/>).</summary>
    Custom,
}

/// <summary>The editor's settings that belong to the user, not to a project (stored in %AppData%\MNVTEngine).</summary>
public sealed class EditorPreferences
{
    public static string DefaultFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MNVTEngine", "editor-preferences.json");

    public ExternalEditorKind ExternalEditor { get; set; } = ExternalEditorKind.SystemDefault;

    /// <summary>VS Code's Code.exe when it was not found automatically.</summary>
    public string VisualStudioCodePath { get; set; } = "";

    public string CustomEditorPath { get; set; } = "";

    /// <summary>Arguments of a custom editor: <c>{file}</c>, <c>{line}</c>, <c>{column}</c> and <c>{project}</c> are replaced (quote them yourself).</summary>
    public string CustomEditorArguments { get; set; } = "\"{file}\"";

    /// <summary>Double-clicking a script (C#, .mnvts) opens it in the external editor instead of the Script Editor.</summary>
    public bool OpenScriptsExternally { get; set; }

    /// <summary>The saved preferences, or defaults when there are none or the file is unreadable.</summary>
    public static EditorPreferences Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonConvert.DeserializeObject<EditorPreferences>(File.ReadAllText(file)) ?? new EditorPreferences() : new EditorPreferences();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new EditorPreferences(); // a damaged file is replaced by the next save
        }
    }

    /// <exception cref="IOException">The file could not be written.</exception>
    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonConvert.SerializeObject(this, Formatting.Indented));
    }
}

/// <summary>Opens files in the editor the user chose (<see cref="EditorPreferences"/>).</summary>
public static class ExternalEditor
{
    /// <summary>VS Code's executable: the user's install, the machine-wide one, or one on PATH; null when there is none.</summary>
    public static string? FindVisualStudioCode()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
        };

        // PATH holds VS Code's "bin" folder (code.cmd); Code.exe is one level up.
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (File.Exists(Path.Combine(folder, "code.cmd")) && Directory.GetParent(folder) is { } parent)
            {
                candidates.Add(Path.Combine(parent.FullName, "Code.exe"));
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>How to open <paramref name="file"/> at a line (1-based) with the preferences' editor.</summary>
    /// <exception cref="InvalidOperationException">The chosen editor is not installed or not configured.</exception>
    public static ProcessStartInfo Command(EditorPreferences preferences, string file, int line, int column, string projectRoot, Func<string?>? findVisualStudioCode = null)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        (line, column) = (Math.Max(1, line), Math.Max(1, column));
        switch (preferences.ExternalEditor)
        {
            case ExternalEditorKind.VisualStudioCode:
                var code = preferences.VisualStudioCodePath is { Length: > 0 } configured ? configured : (findVisualStudioCode ?? FindVisualStudioCode)();
                if (code is null || !File.Exists(code))
                {
                    throw new InvalidOperationException("Visual Studio Code was not found. Install it, or set its Code.exe in Edit > Preferences.");
                }

                var start = new ProcessStartInfo(code) { UseShellExecute = false };
                start.ArgumentList.Add(projectRoot); // the project as the workspace: C# projects get IntelliSense
                start.ArgumentList.Add("--goto");
                start.ArgumentList.Add(string.Create(CultureInfo.InvariantCulture, $"{file}:{line}:{column}"));
                return start;
            case ExternalEditorKind.Custom:
                if (preferences.CustomEditorPath is not { Length: > 0 } path)
                {
                    throw new InvalidOperationException("No custom editor is set: choose its program in Edit > Preferences.");
                }

                var arguments = preferences.CustomEditorArguments
                    .Replace("{file}", file, StringComparison.Ordinal)
                    .Replace("{line}", line.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("{column}", column.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("{project}", projectRoot, StringComparison.Ordinal);
                return new ProcessStartInfo(path, arguments) { UseShellExecute = false };
            default:
                return new ProcessStartInfo(file) { UseShellExecute = true };
        }
    }

    /// <summary>Opens the file; null on success, otherwise why it failed.</summary>
    public static string? Open(EditorPreferences preferences, string file, int line, int column, string projectRoot)
    {
        try
        {
            Process.Start(Command(preferences, file, line, column, projectRoot))?.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return ex.Message;
        }
    }
}
