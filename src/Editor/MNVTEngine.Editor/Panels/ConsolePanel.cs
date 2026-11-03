using System.Globalization;
using ImGuiNET;
using Microsoft.Extensions.Logging;
using MNVTEngine.Editor.ImGuiBackend;
using SysVector4 = System.Numerics.Vector4;

namespace MNVTEngine.Editor.Panels;

/// <summary>Engine and game log with level filters. Hover an entry with an exception to see the stack trace.</summary>
internal sealed class ConsolePanel(EditorContext context)
{
    private bool _showInfo = true;
    private bool _showWarnings = true;
    private bool _showErrors = true;
    private int _lastCount;

    public void Draw()
    {
        if (ImGui.Begin("Console"))
        {
            var entries = context.Logs.Snapshot();
            if (ImGui.SmallButton("Clear"))
            {
                context.Logs.Clear();
                entries = [];
            }

            ImGui.SameLine();
            ImGui.Checkbox($"Info ({entries.Count(e => e.Level < LogLevel.Warning)})", ref _showInfo);
            ImGui.SameLine();
            ImGui.Checkbox($"Warnings ({entries.Count(e => e.Level == LogLevel.Warning)})", ref _showWarnings);
            ImGui.SameLine();
            ImGui.Checkbox($"Errors ({entries.Count(e => e.Level > LogLevel.Warning)})", ref _showErrors);
            ImGui.Separator();

            ImGui.BeginChild("##log", System.Numerics.Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);
            var atBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 4f;
            foreach (var entry in entries)
            {
                var show = entry.Level switch
                {
                    < LogLevel.Warning => _showInfo,
                    LogLevel.Warning => _showWarnings,
                    _ => _showErrors,
                };
                if (!show)
                {
                    continue;
                }

                var color = entry.Level switch
                {
                    < LogLevel.Warning => new SysVector4(0.8f, 0.82f, 0.85f, 1f),
                    LogLevel.Warning => new SysVector4(1f, 0.78f, 0.35f, 1f),
                    _ => new SysVector4(1f, 0.42f, 0.42f, 1f),
                };
                using (Gui.StyleColor(ImGuiCol.Text, color))
                {
                    ImGui.TextUnformatted(string.Create(CultureInfo.InvariantCulture, $"[{entry.Timestamp.ToLocalTime():HH:mm:ss}] {entry.Category}: {entry.Message}"));
                }
                if (entry.Exception is { } exception && ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(exception.ToString());
                }
            }

            // Follow new output only if the user was already at the bottom.
            if (entries.Count != _lastCount && atBottom)
            {
                ImGui.SetScrollHereY(1f);
            }

            _lastCount = entries.Count;
            ImGui.EndChild();
        }

        ImGui.End();
    }
}
