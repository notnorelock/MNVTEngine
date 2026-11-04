using System.Globalization;
using ImGuiNET;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Diagnostics;
using MNVTEngine.Mathematics;
using MNVTEngine.UI;
using Debug = MNVTEngine.Diagnostics.Debug;
using DiagnosticMetrics = MNVTEngine.Diagnostics.Metrics;
using SysVector2 = System.Numerics.Vector2;

namespace MNVTEngine.DebugUI.Panels;

/// <summary>
/// The game UI of the current scene:
/// <list type="bullet">
/// <item>statistics, and focus, hover and pressed state per context;</item>
/// <item>the element tree with bounds and state;</item>
/// <item>outlines in the game: every element, the selected one, the focused one, and the hit-test target under the
/// mouse;</item>
/// <item>the glyph atlas pages.</item>
/// </list>
/// </summary>
public sealed class UIPanel() : DebugPanel("UI")
{
    private UIElement? _selected;
    private bool _showBounds;
    private bool _showAtlas;

    protected override SysVector2 DefaultSize => new(520, 560);

    protected override void Draw()
    {
        foreach (var id in (string[])[DiagnosticMetrics.UIElements, DiagnosticMetrics.UIVisibleElements, DiagnosticMetrics.UIDrawCalls, DiagnosticMetrics.UIQuads, DiagnosticMetrics.UILayoutPasses, DiagnosticMetrics.UIGeometryRebuilds, DiagnosticMetrics.UITextLayouts, DiagnosticMetrics.UIGlyphs, DiagnosticMetrics.UIAtlasPages])
        {
            MetricRow(id);
        }

        if (Diagnostics.Target?.Scene() is not { } scene || UISystem.For(scene) is not { } system || system.Contexts.Count == 0)
        {
            Gui.Muted("No UI contexts in this scene.");
            return;
        }

        ImGui.Checkbox("Outline elements", ref _showBounds);
        ImGui.SameLine();
        ImGui.Checkbox("Glyph atlas", ref _showAtlas);
        for (var i = 0; i < system.Contexts.Count; i++)
        {
            var context = system.Contexts[i];
            var s = context.Statistics;
            if (ImGui.TreeNodeEx($"Context {i} (sort {context.SortOrder})##ctx{i}", ImGuiTreeNodeFlags.DefaultOpen))
            {
                Gui.Muted(string.Create(CultureInfo.InvariantCulture, $"{s.Elements} elements, {s.VisibleElements} visible, {s.Quads} quads, {s.DrawCalls} draws, layout {s.LastLayoutMilliseconds:0.000} ms, scale {context.Scale:0.##}"));
                Gui.Muted($"Focused: {Describe(context.Focused)}  Hovered: {Describe(context.Hovered)}  Pressed: {Describe(context.Pressed)}");
                Tree(context.Root);
                if (context.Overlay.Children.Count > 0)
                {
                    Tree(context.Overlay);
                }

                ImGui.TreePop();
            }

            if (_showBounds && context.Visible)
            {
                Outline(context, context.Root, new Color(0.3f, 0.8f, 1f, 0.35f));
                Outline(context, context.Overlay, new Color(1f, 0.6f, 0.2f, 0.5f));
                Mark(context, context.Focused, new Color(0.2f, 1f, 0.3f, 1f), 2f);
                Mark(context, context.Hovered, new Color(1f, 1f, 0.2f, 1f), 1f); // what the pointer hit test returns
            }

            Mark(context, _selected?.Context == context ? _selected : null, Color.Magenta, 2f);
        }

        if (_showAtlas && Diagnostics.Target?.UI is { } renderer)
        {
            var atlas = renderer.Atlas;
            Gui.Muted($"{atlas.PageCount} page(s) of {Graphics.Text.GlyphAtlas.PageSize}^2, {atlas.GlyphCount} glyphs, {atlas.ImageCount} shapes, generation {atlas.Generation}");
            foreach (var page in atlas.Pages)
            {
                var size = Math.Min(ImGui.GetContentRegionAvail().X, 512f);
                Gui.Image(Overlay.TextureId(page), page, new SysVector2(size, size));
            }
        }
    }

    private void Tree(UIElement element)
    {
        var label = $"{element.GetType().Name} {(element.Name.Length > 0 ? $"'{element.Name}' " : "")}{Format(element.Bounds)}{(element.Visible ? "" : " (hidden)")}";
        var flags = ImGuiTreeNodeFlags.OpenOnArrow | (element.Children.Count == 0 ? ImGuiTreeNodeFlags.Leaf : 0) | (element == _selected ? ImGuiTreeNodeFlags.Selected : 0);
        var open = ImGui.TreeNodeEx($"{label}##{element.GetHashCode()}", flags);
        if (ImGui.IsItemClicked())
        {
            _selected = element;
        }

        if (ImGui.IsItemHovered())
        {
            Gui.Tooltip($"State: {element.State}\nStyle class: {element.StyleClass ?? "-"}\nLayout: {element.Layout}, {element.Width} x {element.Height}\nContent: {Format(element.ContentBounds)}\nClip: {Format(element.ClipBounds)}");
        }

        if (open)
        {
            foreach (var child in element.Children)
            {
                Tree(child);
            }

            ImGui.TreePop();
        }
    }

    private static void Outline(UIContext context, UIElement element, Color color)
    {
        if (!element.Visible)
        {
            return;
        }

        Mark(context, element, color, 1f);
        foreach (var child in element.Children)
        {
            Outline(context, child, color);
        }
    }

    private static void Mark(UIContext context, UIElement? element, Color color, float thickness)
    {
        if (element is null)
        {
            return;
        }

        var b = element.Bounds;
        var s = context.Scale; // logical UI pixels to window pixels (debug screen space)
        Debug.DrawRect(new Rect(b.X * s, b.Y * s, b.Width * s, b.Height * s), color, 0f, false, thickness, DebugSpace.Screen);
    }

    private static string Describe(UIElement? element) => element is null ? "-" : $"{element.GetType().Name} '{element.Name}'";

    private static string Format(Rect r) => string.Create(CultureInfo.InvariantCulture, $"[{r.X:0},{r.Y:0} {r.Width:0}x{r.Height:0}]");
}
