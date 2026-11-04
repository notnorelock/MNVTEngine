using ImGuiNET;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Editor.Services;
using MNVTEngine.Scripting.Compiler;
using MNVTEngine.Scripting.Missions;
using MNVTEngine.Scripting.Syntax;
using SysVector2 = System.Numerics.Vector2;
using SysVector4 = System.Numerics.Vector4;

namespace MNVTEngine.Editor.Panels;

/// <summary>
/// Read-only view of a <c>.mnvts</c> asset: mission outline and source with diagnostics marked on their lines.
/// Clicking a line number toggles a breakpoint; the line where the script debugger stopped is highlighted.
/// </summary>
internal sealed class ScriptView
{
    private static readonly SysVector4 ErrorColor = new(1f, 0.45f, 0.45f, 1f);
    private static readonly SysVector4 WarningColor = new(1f, 0.8f, 0.35f, 1f);
    private static readonly SysVector4 BreakpointColor = new(0.95f, 0.25f, 0.25f, 1f);
    private static readonly SysVector4 StopColor = new(1f, 0.85f, 0.2f, 1f);

    private string? _path;
    private DateTime _writeTime;
    private string[] _lines = [];
    private Dictionary<int, List<Diagnostic>> _diagnostics = [];
    private List<(string Mission, List<string> Objectives)> _outline = [];

    public void Draw(string fullPath, Guid asset, ScriptDebugSession debugger)
    {
        ArgumentNullException.ThrowIfNull(debugger);
        Reload(fullPath);
        if (_outline.Count > 0)
        {
            ImGui.SeparatorText("Missions");
            foreach (var (mission, objectives) in _outline)
            {
                if (ImGui.TreeNodeEx(mission, ImGuiTreeNodeFlags.DefaultOpen))
                {
                    foreach (var objective in objectives)
                    {
                        Gui.Bullet(objective);
                    }

                    ImGui.TreePop();
                }
            }
        }

        ImGui.SeparatorText("Source");
        if (ImGui.BeginChild("##script-source", new SysVector2(0, 320), ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar))
        {
            var stoppedHere = debugger.Break is { } stop && stop.Script == asset ? stop.Line : 0;
            for (var i = 0; i < _lines.Length; i++)
            {
                var line = i + 1;
                var hasBreakpoint = debugger.HasBreakpoint(asset, line);
                ImGui.PushID(line);
                if (ImGui.Selectable("##gutter", false, ImGuiSelectableFlags.None, new SysVector2(14, 0)))
                {
                    debugger.ToggleBreakpoint(asset, line);
                }

                if (hasBreakpoint)
                {
                    var center = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * 0.5f;
                    ImGui.GetWindowDrawList().AddCircleFilled(center, 4.5f, ImGui.ColorConvertFloat4ToU32(BreakpointColor));
                }

                if (ImGui.IsItemHovered())
                {
                    Gui.Tooltip(hasBreakpoint ? "Remove breakpoint" : "Set breakpoint");
                }

                ImGui.PopID();
                ImGui.SameLine();
                var text = $"{line,4}  {_lines[i]}";
                if (line == stoppedHere)
                {
                    Gui.TextColored(StopColor, "> " + text.TrimStart());
                }
                else if (hasBreakpoint)
                {
                    Gui.TextColored(BreakpointColor, text);
                }
                else if (_diagnostics.TryGetValue(line, out var problems))
                {
                    var color = problems.Any(p => p.Severity == DiagnosticSeverity.Error) ? ErrorColor : WarningColor;
                    Gui.TextColored(color, text);
                    if (ImGui.IsItemHovered())
                    {
                        Gui.Tooltip(string.Join("\n", problems.Select(p => $"{p.Code}: {p.Message}")));
                    }
                }
                else if (_lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    Gui.Muted(text);
                }
                else
                {
                    ImGui.TextUnformatted(text);
                }
            }
        }

        ImGui.EndChild();
    }

    private void Reload(string fullPath)
    {
        var writeTime = File.GetLastWriteTimeUtc(fullPath);
        if (_path == fullPath && _writeTime == writeTime)
        {
            return;
        }

        _path = fullPath;
        _writeTime = writeTime;
        var source = File.ReadAllText(fullPath);
        _lines = source.ReplaceLineEndings("\n").Split('\n');
        var result = ScriptCompiler.Compile(source, Path.GetFileName(fullPath), new CompileOptions { Api = StandardApi.All, AllowUnknownFunctions = true });
        _diagnostics = result.Diagnostics.GroupBy(d => d.Location.Line).ToDictionary(g => g.Key, g => g.ToList());
        _outline = result.Script is { } script
            ? [.. script.Missions.Select(m => (
                $"{m.Name ?? m.Id} ({m.Id})",
                m.Objectives.Select(o => o.Id + (o.Requires.Length > 0 ? $"  requires {string.Join(", ", o.Requires)}" : "") + (o.Optional ? "  [optional]" : "")).ToList()))]
            : [];
    }
}
