using System.Globalization;
using ImGuiNET;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Diagnostics;
using Newtonsoft.Json;

namespace MNVTEngine.Editor.Panels;

/// <summary>
/// Project diagnostics: debug UI toggles, HUD appearance, budgets and thresholds. Edits change this editor session
/// (used from the next Play); "Save to project" writes them into the .mnvtproj so builds use them too.
/// </summary>
internal sealed class DiagnosticsSettingsWindow(EditorContext context)
{
    private static readonly string[] Themes = ["dark", "light", "classic"];
    private static readonly string[] Panels = ["Profiler", "Console", "Budgets", "Renderer", "ECS", "Memory", "Scripting", "Physics", "Assets"];
    private static readonly string[] Providers = ["Time", "Memory", "Renderer", "Runtime", "Assets"];
    private string? _status;

    public bool Open { get; set; }

    public void Draw()
    {
        if (!Open)
        {
            return;
        }

        var open = Open;
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(520, 640), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Diagnostics", ref open, ImGuiWindowFlags.NoDocking))
        {
            var settings = context.DiagnosticsSettings;
            ImGui.PushTextWrapPos(0f);
            Gui.Muted("Changes apply from the next Play. Budgets of a running game can be changed in its Budgets window (F6).");
            ImGui.PopTextWrapPos();
            if (ImGui.Button("Save to project"))
            {
                Save(settings);
            }

            ImGui.SameLine();
            if (ImGui.Button("Revert to project"))
            {
                context.DiagnosticsSettings = context.Session.Project.Diagnostics.Clone();
                _status = "Reverted to the project's settings.";
            }

            if (_status is not null)
            {
                Gui.Muted(_status);
            }

            Toggles(settings);
            Overlay(settings.Overlay);
            Budgets(settings);
            Lists(settings);
        }

        ImGui.End();
        Open = open;
    }

    private void Save(DiagnosticsSettings settings)
    {
        var problems = settings.Validate();
        try
        {
            context.Session.Project.SaveDiagnostics(settings);
            _status = problems.Count == 0 ? "Saved to the project file." : "Saved; fixed: " + string.Join(" ", problems);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _status = "Could not save: " + ex.Message;
        }
    }

    private static void Toggles(DiagnosticsSettings s)
    {
        ImGui.SeparatorText("Runtime debug interface");
        Check("Enabled (Play Mode, Player, Debug builds)", s.Enabled, v => s.Enabled = v);
        Check("Include in Release builds", s.IncludeInRelease, v => s.IncludeInRelease = v);
        Check("Show HUD at start", s.ShowOverlayByDefault, v => s.ShowOverlayByDefault = v);
        Check("Profiler", s.EnableProfiler, v => s.EnableProfiler = v);
        Check("GPU timing (timer queries)", s.EnableGpuTiming, v => s.EnableGpuTiming = v);
        Check("Debug drawing", s.EnableDebugDrawing, v => s.EnableDebugDrawing = v);
        Check("Performance warnings", s.EnablePerformanceWarnings, v => s.EnablePerformanceWarnings = v);
        Check("Keyboard shortcuts", s.ShortcutsEnabled, v => s.ShortcutsEnabled = v);
        var max = s.MaxDebugDrawCommands;
        if (ImGui.InputInt("Max debug draws", ref max, 1000))
        {
            s.MaxDebugDrawCommands = Math.Clamp(max, 1, 1_000_000);
        }

        var warmup = s.WarmupFrames;
        if (ImGui.InputInt("Warm-up frames", ref warmup, 10))
        {
            s.WarmupFrames = Math.Clamp(warmup, 0, 100_000);
        }

        var repeat = s.WarningRepeatSeconds;
        if (Gui.InputNumber("Warning repeat (s)", ref repeat))
        {
            s.WarningRepeatSeconds = Math.Max(0.5, repeat);
        }

        var keys = s.Keys;
        ImGui.TextUnformatted("Keys:");
        Key("HUD", keys.ToggleOverlay, v => keys.ToggleOverlay = v);
        Key("Profiler", keys.ToggleProfiler, v => keys.ToggleProfiler = v);
        Key("Console", keys.ToggleConsole, v => keys.ToggleConsole = v);
        ImGui.NewLine();
        Key("Debug drawing", keys.ToggleDebugDrawing, v => keys.ToggleDebugDrawing = v);
        Key("Warnings", keys.ToggleWarnings, v => keys.ToggleWarnings = v);
        Key("All windows", keys.ToggleWindows, v => keys.ToggleWindows = v);
        ImGui.NewLine();
    }

    private static void Overlay(OverlaySettings o)
    {
        ImGui.SeparatorText("HUD");
        var corner = (int)o.Corner;
        if (ImGui.Combo("Corner", ref corner, Enum.GetNames<OverlayCorner>(), 4))
        {
            o.Corner = (OverlayCorner)corner;
        }

        var opacity = o.Opacity;
        if (ImGui.SliderFloat("Opacity", ref opacity, 0f, 1f))
        {
            o.Opacity = opacity;
        }

        var scale = o.Scale;
        if (ImGui.SliderFloat("Scale", ref scale, 0.5f, 3f))
        {
            o.Scale = scale;
        }

        var theme = Math.Max(0, Array.IndexOf(Themes, o.Theme));
        if (ImGui.Combo("Theme", ref theme, Themes, Themes.Length))
        {
            o.Theme = Themes[theme];
        }

        var title = o.Title;
        if (ImGui.InputText("Title", ref title, 64))
        {
            o.Title = title;
        }

        var sections = string.Join(", ", o.Sections);
        if (ImGui.InputText("Sections", ref sections, 256))
        {
            o.Sections = [.. sections.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }

        Gui.Muted("frame, memory, renderer, runtime, scripting, physics, assets, warnings");
    }

    private static void Budgets(DiagnosticsSettings s)
    {
        ImGui.SeparatorText("Budgets (unchecked = off)");
        foreach (var property in typeof(BudgetSettings).GetProperties())
        {
            var value = (double?)property.GetValue(s.Budgets);
            var on = value is not null;
            ImGui.PushID(property.Name);
            if (ImGui.Checkbox("##on", ref on))
            {
                property.SetValue(s.Budgets, on ? 1.0 : null);
            }

            ImGui.SameLine();
            var number = value ?? 0;
            using (Gui.StyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.55f, 0.57f, 0.62f, 1f), !on))
            {
                ImGui.SetNextItemWidth(140);
                if (Gui.InputNumber(property.Name, ref number) && on && number > 0)
                {
                    property.SetValue(s.Budgets, number);
                }
            }

            ImGui.PopID();
        }

        var t = s.Thresholds;
        var values = new System.Numerics.Vector4((float)t.Information * 100, (float)t.Warning * 100, (float)t.Error * 100, (float)t.Critical * 100);
        if (ImGui.InputFloat4("Thresholds %", ref values, "%.0f"))
        {
            var changed = new BudgetThresholds(values.X / 100.0, values.Y / 100.0, values.Z / 100.0, values.W / 100.0);
            try
            {
                changed.Validate();
                s.Thresholds = changed;
            }
            catch (ArgumentException)
            {
                // Not increasing yet (the user is still typing); the previous thresholds stay.
            }
        }

        Gui.Muted(string.Create(CultureInfo.InvariantCulture, $"information / warning / error / critical, as % of each limit"));
    }

    private static void Lists(DiagnosticsSettings s)
    {
        ImGui.SeparatorText("Debug windows offered");
        foreach (var panel in Panels)
        {
            Check(panel, !s.DisabledPanels.Contains(panel, StringComparer.OrdinalIgnoreCase), on => Toggle(s.DisabledPanels, panel, !on));
        }

        ImGui.SeparatorText("Metric providers");
        foreach (var provider in Providers)
        {
            Check(provider + "##provider", !s.DisabledProviders.Contains(provider, StringComparer.OrdinalIgnoreCase), on => Toggle(s.DisabledProviders, provider, !on));
        }
    }

    private static void Toggle(List<string> list, string name, bool present)
    {
        list.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (present)
        {
            list.Add(name);
        }
    }

    private static void Check(string label, bool value, Action<bool> set)
    {
        if (ImGui.Checkbox(label, ref value))
        {
            set(value);
        }
    }

    private static void Key(string label, string value, Action<string> set)
    {
        ImGui.SetNextItemWidth(60);
        if (ImGui.InputText(label + "##key", ref value, 16))
        {
            set(value.Trim());
        }

        ImGui.SameLine();
    }
}
