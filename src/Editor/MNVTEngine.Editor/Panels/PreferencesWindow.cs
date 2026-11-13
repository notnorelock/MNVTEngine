using ImGuiNET;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Editor.Services;
using SysVector2 = System.Numerics.Vector2;

namespace MNVTEngine.Editor.Panels;

/// <summary>Edit > Preferences: the external script editor (saved for the user, not the project).</summary>
internal sealed class PreferencesWindow(EditorContext context)
{
    private static readonly string[] Editors = ["System default (the app Windows opens the file type with)", "Visual Studio Code", "Custom program"];

    private string? _detected;
    private bool _searched;

    public bool Open { get; set; }

    public void Draw()
    {
        if (!Open)
        {
            return;
        }

        var open = Open;
        ImGui.SetNextWindowSize(new SysVector2(620f, 300f), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Preferences", ref open, ImGuiWindowFlags.NoDocking))
        {
            var preferences = context.Preferences;
            var changed = false;
            ImGui.SeparatorText("External script editor");
            var kind = (int)preferences.ExternalEditor;
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.Combo("##editor", ref kind, Editors, Editors.Length))
            {
                preferences.ExternalEditor = (ExternalEditorKind)kind;
                changed = true;
            }

            switch (preferences.ExternalEditor)
            {
                case ExternalEditorKind.VisualStudioCode:
                    if (!_searched)
                    {
                        (_detected, _searched) = (ExternalEditor.FindVisualStudioCode(), true);
                    }

                    Gui.Muted(_detected is null ? "Code.exe was not found automatically: set it below." : $"Found: {_detected}");
                    var path = preferences.VisualStudioCodePath;
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.InputTextWithHint("##code", "Code.exe (empty: found automatically)", ref path, 512))
                    {
                        preferences.VisualStudioCodePath = path.Trim().Trim('"');
                        changed = true;
                    }

                    Gui.Muted("Opens the project folder with the file at its line: C# gets VS Code's IntelliSense from the scripts project.");
                    break;
                case ExternalEditorKind.Custom:
                    var program = preferences.CustomEditorPath;
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.InputTextWithHint("##program", "Program (e.g. C:\\Program Files\\Notepad++\\notepad++.exe)", ref program, 512))
                    {
                        preferences.CustomEditorPath = program.Trim().Trim('"');
                        changed = true;
                    }

                    var arguments = preferences.CustomEditorArguments;
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.InputTextWithHint("##arguments", "Arguments", ref arguments, 512))
                    {
                        preferences.CustomEditorArguments = arguments;
                        changed = true;
                    }

                    Gui.Muted("{file}, {line}, {column} and {project} are replaced. Quote them: \"{file}\" -n{line}");
                    break;
                default:
                    break;
            }

            var external = preferences.OpenScriptsExternally;
            if (ImGui.Checkbox("Double-click opens scripts (C#, .mnvts) in the external editor", ref external))
            {
                preferences.OpenScriptsExternally = external;
                changed = true;
            }

            Gui.Muted("Off: they open in the Script Editor. Either way, right-click a script for both.");
            if (context.ExternalEditorError is { } error)
            {
                Gui.TextColored(new System.Numerics.Vector4(1f, 0.45f, 0.45f, 1f), error);
            }

            if (changed)
            {
                try
                {
                    preferences.Save(EditorPreferences.DefaultFile);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    context.ExternalEditorError = $"Preferences not saved: {ex.Message}";
                }
            }
        }

        ImGui.End();
        Open = open;
    }
}
