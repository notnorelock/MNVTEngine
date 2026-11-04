using System.Globalization;
using ImGuiNET;
using Microsoft.Extensions.Logging;
using MNVTEngine.Core.Logging;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Runtime;
using SysVector4 = System.Numerics.Vector4;

namespace MNVTEngine.Editor.Panels;

/// <summary>
/// Engine and game log. Each line shows the class and method that logged it; hover for namespace, logger, file:line and
/// the exception. The filter matches message, logger, namespace, class and method. "Levels" sets the level per logger
/// (a rule covers every logger below that name) and saves it to Settings/logging.json.
/// </summary>
internal sealed class ConsolePanel(EditorContext context)
{
    private static readonly string[] LevelNames = [.. Enum.GetNames<LogLevel>()];
    private bool _showInfo = true;
    private bool _showWarnings = true;
    private bool _showErrors = true;
    private string _filter = "";
    private string _newRule = "";
    private string? _levelsError;
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
            ImGui.SameLine();
            ImGui.SetNextItemWidth(220f);
            ImGui.InputTextWithHint("##filter", "Filter (text, namespace, class...)", ref _filter, 128);
            ImGui.SameLine();
            if (ImGui.SmallButton("Levels..."))
            {
                ImGui.OpenPopup("##levels");
            }

            DrawLevels();
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
                if (!show || !Matches(entry))
                {
                    continue;
                }

                var color = entry.Level switch
                {
                    <= LogLevel.Debug => new SysVector4(0.55f, 0.57f, 0.6f, 1f),
                    < LogLevel.Warning => new SysVector4(0.8f, 0.82f, 0.85f, 1f),
                    LogLevel.Warning => new SysVector4(1f, 0.78f, 0.35f, 1f),
                    _ => new SysVector4(1f, 0.42f, 0.42f, 1f),
                };
                var source = entry.Source is { } site ? $"{site.Class}.{site.Method}" : entry.Category;
                using (Gui.StyleColor(ImGuiCol.Text, color))
                {
                    ImGui.TextUnformatted(string.Create(CultureInfo.InvariantCulture, $"[{entry.Timestamp.ToLocalTime():HH:mm:ss}] {LogFormat.Level(entry.Level)} {source}: {entry.Message}"));
                }

                if (ImGui.IsItemHovered())
                {
                    Gui.Tooltip(Details(entry));
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

    private bool Matches(LogEntry entry)
    {
        if (_filter.Length == 0)
        {
            return true;
        }

        return entry.Message.Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || entry.Category.Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || (entry.Source?.ToString().Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string Details(LogEntry entry)
    {
        var lines = new List<string> { $"Logger: {entry.Category}" };
        if (entry.Source is { } site)
        {
            lines.Add($"Namespace: {(site.Namespace.Length == 0 ? "(global)" : site.Namespace)}");
            lines.Add($"Class: {site.Class}");
            lines.Add($"Method: {site.Method}");
            if (site.File is { Length: > 0 } file)
            {
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"At: {file}:{site.Line}"));
            }
        }

        if (entry.EventId.Id != 0)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"Event: {entry.EventId.Id}"));
        }

        lines.Add(entry.Message);
        if (entry.Exception is { } exception)
        {
            lines.Add(exception.ToString());
        }

        return string.Join('\n', lines);
    }

    private void DrawLevels()
    {
        if (!ImGui.BeginPopup("##levels"))
        {
            return;
        }

        var hub = context.LogHub;
        var levels = hub.Levels;
        ImGui.TextUnformatted("A rule applies to the logger and every logger below it; the longest name wins.");
        var level = (int)levels.Default;
        ImGui.SetNextItemWidth(140f);
        if (ImGui.Combo("Default", ref level, LevelNames, LevelNames.Length))
        {
            levels.Default = (LogLevel)level;
        }

        var callSites = hub.CaptureCallSites;
        if (ImGui.Checkbox("Record class/method for every message (stack walk)", ref callSites))
        {
            hub.CaptureCallSites = callSites;
        }

        ImGui.SeparatorText("Rules");
        string? remove = null;
        foreach (var (name, ruleLevel) in levels.Rules.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase))
        {
            ImGui.PushID(name);
            var value = (int)ruleLevel;
            ImGui.SetNextItemWidth(120f);
            if (ImGui.Combo("##level", ref value, LevelNames, LevelNames.Length))
            {
                levels.Set(name, (LogLevel)value);
            }

            ImGui.SameLine();
            ImGui.TextUnformatted(name);
            ImGui.SameLine();
            if (ImGui.SmallButton("X"))
            {
                remove = name;
            }

            ImGui.PopID();
        }

        if (remove is not null)
        {
            levels.Remove(remove);
        }

        ImGui.SetNextItemWidth(320f);
        if (ImGui.BeginCombo("##known", "Add rule for a logger...", ImGuiComboFlags.HeightLarge))
        {
            // Known loggers plus each of their namespace prefixes, so a whole namespace can get one rule.
            var names = hub.Names
                .SelectMany(n => n.Split('.').Select((_, i) => string.Join('.', n.Split('.').Take(i + 1))))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(n => !levels.Rules.ContainsKey(n))
                .Order(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                if (ImGui.Selectable(name))
                {
                    levels.Set(name, LogLevel.Debug);
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SetNextItemWidth(240f);
        ImGui.InputTextWithHint("##newrule", "or type a name (Game.Player)", ref _newRule, 128);
        ImGui.SameLine();
        if (ImGui.SmallButton("Add") && _newRule.Trim().Length > 0)
        {
            levels.Set(_newRule.Trim(), LogLevel.Debug);
            _newRule = "";
        }

        ImGui.Separator();
        if (ImGui.Button("Save to Settings/logging.json"))
        {
            try
            {
                LoggingSettings.From(hub).Save(LoggingSettings.PathFor(context.Session.Project));
                _levelsError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _levelsError = ex.Message;
            }
        }

        if (_levelsError is not null)
        {
            Gui.TextColored(new SysVector4(1f, 0.42f, 0.42f, 1f), _levelsError);
        }

        ImGui.EndPopup();
    }
}
