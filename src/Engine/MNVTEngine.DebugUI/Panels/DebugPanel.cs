using System.Globalization;
using ImGuiNET;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Diagnostics;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;
using DiagnosticMetrics = MNVTEngine.Diagnostics.Metrics;
using SysVector2 = System.Numerics.Vector2;
using SysVector4 = System.Numerics.Vector4;

namespace MNVTEngine.DebugUI.Panels;

/// <summary>
/// A debug window. Subclass it for a custom window and add it with <see cref="DebugOverlay.AddPanel"/>; it then appears
/// in the Windows panel and the 'window' console command. Panels only read diagnostics data; collection happens elsewhere.
/// </summary>
public abstract class DebugPanel(string name)
{
    public string Name { get; } = name;

    public bool Open { get; set; }

    /// <summary>Windows that take input capture gameplay input while focused.</summary>
    public virtual bool Interactive => true;

    protected virtual SysVector2 DefaultSize => new(440, 340);

    /// <summary>Where the window first appears (until moved; positions are kept in the layout file). Default: cascaded.</summary>
    protected virtual SysVector2 DefaultPosition(SysVector2 display, int index) => new(260 + (index * 28), 50 + (index * 28));

    protected DebugOverlay Overlay { get; private set; } = null!;

    protected RuntimeDiagnostics Diagnostics => Overlay.Diagnostics;

    protected Scene? Scene => Overlay.Context.Scene();

    internal void Attach(DebugOverlay overlay)
    {
        Overlay = overlay;
        OnAttached();
    }

    protected virtual void OnAttached()
    {
    }

    internal void DrawWindow()
    {
        ImGui.SetNextWindowSize(DefaultSize, ImGuiCond.FirstUseEver);
        var display = ImGui.GetMainViewport().WorkSize;
        var position = DefaultPosition(display, Math.Max(0, Overlay.Panels.ToList().IndexOf(this)));
        ImGui.SetNextWindowPos(SysVector2.Clamp(position, SysVector2.Zero, SysVector2.Max(SysVector2.Zero, display - new SysVector2(120, 60))), ImGuiCond.FirstUseEver);
        var open = true;
        if (ImGui.Begin(Name + "##debug", ref open))
        {
            ImGui.SetWindowFontScale(Diagnostics.Settings.Overlay.Scale);
            Draw();
        }

        ImGui.End();
        Open = open;
    }

    protected abstract void Draw();

    protected static readonly SysVector4 Dim = new(0.55f, 0.57f, 0.62f, 1f);

    public static SysVector4 SeverityColor(BudgetSeverity severity) => severity switch
    {
        BudgetSeverity.Critical => new SysVector4(1f, 0.25f, 0.6f, 1f),
        BudgetSeverity.Error => new SysVector4(1f, 0.35f, 0.35f, 1f),
        BudgetSeverity.Warning => new SysVector4(1f, 0.75f, 0.3f, 1f),
        BudgetSeverity.Information => new SysVector4(0.55f, 0.8f, 1f, 1f),
        _ => new SysVector4(0.85f, 0.87f, 0.9f, 1f),
    };

    /// <summary>"Label  value" with unmeasured values dimmed (never shown as 0).</summary>
    protected void MetricRow(string id, string? label = null)
    {
        var value = Diagnostics.Metrics.Get(id);
        ImGui.TextUnformatted(label ?? DiagnosticMetrics.Find(id)?.Name ?? id);
        ImGui.SameLine(ImGui.GetFontSize() * 11);
        if (value.IsMeasured)
        {
            ImGui.TextUnformatted(DiagnosticMetrics.Format(id, value));
        }
        else
        {
            Gui.TextColored(Dim, DiagnosticMetrics.Format(id, value));
        }

        if (ImGui.IsItemHovered() && DiagnosticMetrics.Find(id) is { } definition)
        {
            Gui.Tooltip(definition.Description);
        }
    }

    /// <summary>A line graph of a tracked metric's history (gaps where it was not measured).</summary>
    protected void Graph(string id, string label, float height = 60f)
    {
        if (_graph.Length != Diagnostics.History.Capacity)
        {
            _graph = new float[Diagnostics.History.Capacity];
        }

        var values = _graph;
        if (!Diagnostics.History.CopyTo(id, values))
        {
            return;
        }

        var max = float.NaN;
        for (var i = 0; i < values.Length; i++)
        {
            if (float.IsNaN(values[i]))
            {
                values[i] = 0f; // not measured that frame: drawn at the baseline
            }
            else if (!(values[i] <= max))
            {
                max = values[i];
            }
        }

        if (float.IsNaN(max))
        {
            Gui.TextColored(Dim, $"{label}: no data");
            return;
        }

        var current = DiagnosticMetrics.Format(id, Diagnostics.Metrics.Get(id));
        ImGui.PlotLines($"##{id}", ref values[0], values.Length, 0, $"{label}: {current}", 0f, MathF.Max(max * 1.2f, 1e-3f), new SysVector2(-1, height));
    }

    private float[] _graph = [];
}

/// <summary>The always-on-top HUD: configurable sections in a corner; it never takes input.</summary>
internal static class HudPanel
{
    private static readonly Dictionary<string, (string Title, string[] Metrics)> Sections = new(StringComparer.OrdinalIgnoreCase)
    {
        ["frame"] = ("", [DiagnosticMetrics.Fps, DiagnosticMetrics.FrameTime, DiagnosticMetrics.CpuTime, DiagnosticMetrics.GpuTime]),
        ["memory"] = ("", [DiagnosticMetrics.ProcessMemory, DiagnosticMetrics.GpuMemoryUsed, DiagnosticMetrics.ManagedHeap, DiagnosticMetrics.AllocatedPerFrame, DiagnosticMetrics.CollectionsPerMinute]),
        ["renderer"] = ("RENDERER", [DiagnosticMetrics.DrawCalls, DiagnosticMetrics.Vertices, DiagnosticMetrics.Triangles, DiagnosticMetrics.Textures, DiagnosticMetrics.Shaders, DiagnosticMetrics.SpriteBatches, DiagnosticMetrics.Sprites]),
        ["runtime"] = ("RUNTIME", [DiagnosticMetrics.Entities, DiagnosticMetrics.Components, DiagnosticMetrics.Systems, DiagnosticMetrics.PhysicsBodies, DiagnosticMetrics.AudioSources, DiagnosticMetrics.CSharpScripts, DiagnosticMetrics.ScriptVms, DiagnosticMetrics.Uptime, DiagnosticMetrics.FrameNumber]),
        ["scripting"] = ("SCRIPTING", [DiagnosticMetrics.CSharpScripts, DiagnosticMetrics.ScriptVms, DiagnosticMetrics.ScriptFibers]),
        ["physics"] = ("PHYSICS", [DiagnosticMetrics.PhysicsBodies, DiagnosticMetrics.PhysicsColliders]),
        ["particles"] = ("PARTICLES", [DiagnosticMetrics.ParticleSystems, DiagnosticMetrics.ParticlesAlive, DiagnosticMetrics.ParticlesEmitted, DiagnosticMetrics.ParticleSimulationTime, DiagnosticMetrics.ParticlesDrawn, DiagnosticMetrics.ParticleDrawCalls, DiagnosticMetrics.ParticleRenderTime]),
        ["assets"] = ("ASSETS", [DiagnosticMetrics.AssetsLoaded, DiagnosticMetrics.AssetsFailed, DiagnosticMetrics.TextureMemory, DiagnosticMetrics.AudioMemory]),
    };

    public static void Draw(DebugOverlay overlay)
    {
        var diagnostics = overlay.Diagnostics;
        var settings = diagnostics.Settings.Overlay;
        diagnostics.GpuMemoryRequested |= settings.Sections.Contains("memory", StringComparer.OrdinalIgnoreCase);
        var viewport = ImGui.GetMainViewport();
        var (pivot, corner) = settings.Corner switch
        {
            OverlayCorner.TopRight => (new SysVector2(1, 0), new SysVector2(viewport.WorkPos.X + viewport.WorkSize.X - 10, viewport.WorkPos.Y + 10)),
            OverlayCorner.BottomLeft => (new SysVector2(0, 1), new SysVector2(viewport.WorkPos.X + 10, viewport.WorkPos.Y + viewport.WorkSize.Y - 10)),
            OverlayCorner.BottomRight => (new SysVector2(1, 1), viewport.WorkPos + viewport.WorkSize - new SysVector2(10, 10)),
            _ => (SysVector2.Zero, viewport.WorkPos + new SysVector2(10, 10)),
        };
        ImGui.SetNextWindowPos(corner, ImGuiCond.Always, pivot);
        ImGui.SetNextWindowBgAlpha(settings.Opacity);
        const ImGuiWindowFlags Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoDocking;
        if (ImGui.Begin("##hud", Flags))
        {
            ImGui.SetWindowFontScale(settings.Scale);
            if (settings.Title.Length > 0)
            {
                Gui.TextColored(new SysVector4(1f, 0.8f, 0.3f, 1f), settings.Title);
            }

            if (diagnostics.Target?.Scene() is { } scene)
            {
                Gui.TextColored(new SysVector4(0.6f, 0.62f, 0.66f, 1f), $"Scene: {scene.Name}");
            }

            foreach (var name in settings.Sections)
            {
                if (name.Equals("warnings", StringComparison.OrdinalIgnoreCase))
                {
                    if (overlay.WarningsVisible)
                    {
                        Warnings(diagnostics);
                    }

                    continue;
                }

                if (!Sections.TryGetValue(name, out var section))
                {
                    continue;
                }

                ImGui.Spacing();
                if (section.Title.Length > 0)
                {
                    Gui.TextColored(new SysVector4(0.55f, 0.75f, 1f, 1f), section.Title);
                }

                foreach (var id in section.Metrics)
                {
                    var value = diagnostics.Metrics.Get(id);
                    var label = (DiagnosticMetrics.Find(id)?.Name ?? id).ToUpperInvariant();
                    var text = $"{label}: {DiagnosticMetrics.Format(id, value)}";
                    if (value.IsMeasured)
                    {
                        ImGui.TextUnformatted(text);
                    }
                    else
                    {
                        Gui.TextColored(new SysVector4(0.55f, 0.57f, 0.62f, 1f), text);
                    }
                }
            }

            if (!Debug.DrawingEnabled)
            {
                Gui.TextColored(new SysVector4(0.55f, 0.57f, 0.62f, 1f), "(debug drawing off)");
            }
        }

        ImGui.End();
    }

    private static void Warnings(RuntimeDiagnostics diagnostics)
    {
        var active = diagnostics.Budgets.States.Where(s => s.Severity >= BudgetSeverity.Warning).OrderByDescending(s => s.Severity).ThenByDescending(s => s.Ratio).ToList();
        if (active.Count == 0)
        {
            return;
        }

        ImGui.Spacing();
        foreach (var state in active)
        {
            var name = (DiagnosticMetrics.Find(state.Budget.Metric)?.Name ?? state.Budget.Id).ToUpperInvariant();
            var label = state.Severity >= BudgetSeverity.Error ? "EXCEEDED" : "AT";
            Gui.TextColored(DebugPanel.SeverityColor(state.Severity), string.Create(CultureInfo.InvariantCulture, $"{state.Severity.ToString().ToUpperInvariant()}: {name} BUDGET {label} {state.Ratio * 100:0.00}%"));
        }
    }
}

/// <summary>Opens and closes the debug windows; shortcuts and overlay appearance for this session.</summary>
internal sealed class WindowsPanel() : DebugPanel("Windows")
{
    private static readonly string[] Themes = ["dark", "light", "classic"];
    private static readonly string[] AllSections = ["frame", "memory", "renderer", "runtime", "scripting", "physics", "assets", "warnings"];

    protected override SysVector2 DefaultSize => new(360, 520);

    protected override void Draw()
    {
        var settings = Diagnostics.Settings;
        var keys = settings.Keys;
        var hud = Overlay.HudVisible;
        if (ImGui.Checkbox($"HUD ({keys.ToggleOverlay})", ref hud))
        {
            Overlay.HudVisible = hud;
        }

        var drawing = Debug.DrawingEnabled;
        if (ImGui.Checkbox($"Debug drawing ({keys.ToggleDebugDrawing})", ref drawing))
        {
            Debug.DrawingEnabled = drawing;
        }

        var warnings = Overlay.WarningsVisible;
        if (ImGui.Checkbox($"Budget warnings ({keys.ToggleWarnings})", ref warnings))
        {
            Overlay.WarningsVisible = warnings;
        }

        var monitoring = Diagnostics.Budgets.Enabled;
        if (ImGui.Checkbox("Budget monitoring", ref monitoring))
        {
            Diagnostics.Budgets.Enabled = monitoring;
        }

        ImGui.SeparatorText("Windows");
        foreach (var panel in Overlay.Panels.Where(p => p != this))
        {
            var open = panel.Open;
            var shortcut = panel switch
            {
                ProfilerPanel => $" ({keys.ToggleProfiler})",
                ConsolePanel => $" ({keys.ToggleConsole})",
                _ => "",
            };
            if (ImGui.Checkbox(panel.Name + shortcut, ref open))
            {
                panel.Open = open;
            }
        }

        ImGui.SeparatorText("HUD");
        var overlay = settings.Overlay;
        var corner = (int)overlay.Corner;
        if (ImGui.Combo("Corner", ref corner, Enum.GetNames<OverlayCorner>(), 4))
        {
            overlay.Corner = (OverlayCorner)corner;
        }

        var opacity = overlay.Opacity;
        if (ImGui.SliderFloat("Opacity", ref opacity, 0f, 1f))
        {
            overlay.Opacity = opacity;
        }

        var scale = overlay.Scale;
        if (ImGui.SliderFloat("Scale", ref scale, 0.5f, 3f))
        {
            overlay.Scale = scale;
        }

        var theme = Math.Max(0, Array.IndexOf(Themes, overlay.Theme));
        if (ImGui.Combo("Theme", ref theme, Themes, Themes.Length))
        {
            overlay.Theme = Themes[theme];
        }

        foreach (var section in AllSections)
        {
            var shown = overlay.Sections.Contains(section, StringComparer.OrdinalIgnoreCase);
            if (ImGui.Checkbox("Show " + section, ref shown))
            {
                if (shown)
                {
                    overlay.Sections = [.. AllSections.Where(s => s == section || overlay.Sections.Contains(s, StringComparer.OrdinalIgnoreCase))];
                }
                else
                {
                    overlay.Sections.RemoveAll(s => s.Equals(section, StringComparison.OrdinalIgnoreCase));
                }
            }
        }

        ImGui.SeparatorText("Metric providers");
        foreach (var provider in Diagnostics.Collector.Providers)
        {
            var enabled = Diagnostics.Collector.IsEnabled(provider.Name);
            if (ImGui.Checkbox(provider.Name, ref enabled))
            {
                Diagnostics.Collector.SetEnabled(provider.Name, enabled);
            }
        }

        Gui.TextColored(Dim, "Changes here last for this session.");
    }
}
