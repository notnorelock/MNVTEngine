using ImGuiNET;
using MNVTEngine.DebugUI.ImGuiBackend;

namespace MNVTEngine.Editor.Panels;

/// <summary>Help > About: the editor's engine build (version, build number, commit, date, dirty state), copyable for bug reports.</summary>
internal sealed class AboutWindow
{
    public bool Open { get; set; }

    public void Draw()
    {
        if (!Open)
        {
            return;
        }

        var open = Open;
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(560, 330), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(EditorWindows.Title("About MNVTEngine"), ref open, ImGuiWindowFlags.NoDocking))
        {
            ImGui.TextUnformatted($"MNVTEngine {EngineVersion.Version}");
            Gui.Muted(EngineVersion.IsDevelopmentBuild ? "Development build: built with dotnet build, no build number." : $"Build {EngineVersion.Build} of the {EngineVersion.Channel} channel.");
            ImGui.Separator();
            if (ImGui.BeginTable("##version", 2, ImGuiTableFlags.SizingFixedFit))
            {
                Row("Version", EngineVersion.Version);
                Row("Build number", EngineVersion.Build.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Row("Commit", EngineVersion.CommitHash);
                Row("Branch", EngineVersion.Branch);
                Row("Built", EngineVersion.BuildTimestampUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture));
                Row("Configuration", EngineVersion.BuildConfiguration);
                Row("Build ID", EngineVersion.BuildId);
                Row("Dirty", EngineVersion.IsDirtyBuild ? "yes: built from a modified working tree, not exactly this commit" : "no");
                if (EngineVersion.SourceProblem is { } problem)
                {
                    Row("Source", problem);
                }

                ImGui.EndTable();
            }

            if (ImGui.Button("Copy to clipboard"))
            {
                ImGui.SetClipboardText(EngineVersion.Describe());
            }
        }

        ImGui.End();
        Open = open;
    }

    private static void Row(string name, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        Gui.Muted(name);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(value);
    }
}
