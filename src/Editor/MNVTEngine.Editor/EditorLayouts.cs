using SysVector2 = System.Numerics.Vector2;

namespace MNVTEngine.Editor;

/// <summary>The built-in window arrangements of the Layout menu (Unity's Default, 2 by 3, Tall and Wide).</summary>
internal enum EditorLayout
{
    Default,
    TwoByThree,
    Tall,
    Wide,
}

/// <summary>
/// Window layouts: the built-in arrangements, docked from scratch, and layouts the user saved, stored as ImGui ini
/// files in the project's <c>Library/Editor/Layouts</c> folder (per user, like the current layout).
/// </summary>
internal static class EditorLayouts
{
    public static readonly (EditorLayout Kind, string Name)[] BuiltIn =
    [
        (EditorLayout.Default, "Default"),
        (EditorLayout.TwoByThree, "2 by 3"),
        (EditorLayout.Tall, "Tall"),
        (EditorLayout.Wide, "Wide"),
    ];

    private static readonly string[] BottomTabs = ["Project", "Console", "Profiler", "Script Debugger"];
    private static readonly string[] CenterTabs = ["Game", "Script Editor", "UI Editor", "Scene"];

    public static void Build(EditorLayout layout, uint dockId, SysVector2 size)
    {
        DockBuilder.RemoveNode(dockId);
        _ = DockBuilder.AddNode(dockId, DockBuilder.DockSpaceFlag);
        DockBuilder.SetNodeSize(dockId, size);
        uint left, right, bottom, center, rest;
        switch (layout)
        {
            case EditorLayout.TwoByThree:
                // Scene over Game on the left; Hierarchy, Project and Inspector as three columns on the right.
                DockBuilder.SplitNode(dockId, DockBuilder.Direction.Right, 0.55f, out right, out var views);
                DockBuilder.SplitNode(views, DockBuilder.Direction.Down, 0.5f, out var game, out var scene);
                DockBuilder.SplitNode(right, DockBuilder.Direction.Right, 0.38f, out var inspector, out rest);
                DockBuilder.SplitNode(rest, DockBuilder.Direction.Right, 0.5f, out var project, out var hierarchy);
                Dock(hierarchy, "Hierarchy");
                Dock(project, BottomTabs);
                Dock(inspector, "Inspector");
                Dock(game, "Game", "Script Editor", "UI Editor");
                Dock(scene, "Scene");
                break;
            case EditorLayout.Tall:
                // Tall side columns: Hierarchy over Project on the left, Inspector on the right, the views between.
                DockBuilder.SplitNode(dockId, DockBuilder.Direction.Left, 0.22f, out left, out rest);
                DockBuilder.SplitNode(rest, DockBuilder.Direction.Right, 0.3f, out right, out center);
                DockBuilder.SplitNode(left, DockBuilder.Direction.Down, 0.5f, out bottom, out var top);
                Dock(top, "Hierarchy");
                Dock(bottom, BottomTabs);
                Dock(right, "Inspector");
                Dock(center, CenterTabs);
                break;
            case EditorLayout.Wide:
                // A full-width bottom strip under Hierarchy, the views and the Inspector.
                DockBuilder.SplitNode(dockId, DockBuilder.Direction.Down, 0.3f, out bottom, out rest);
                DockBuilder.SplitNode(rest, DockBuilder.Direction.Left, 0.18f, out left, out rest);
                DockBuilder.SplitNode(rest, DockBuilder.Direction.Right, 0.25f, out right, out center);
                Dock(left, "Hierarchy");
                Dock(right, "Inspector");
                Dock(bottom, BottomTabs);
                Dock(center, CenterTabs);
                break;
            default:
                DockBuilder.SplitNode(dockId, DockBuilder.Direction.Left, 0.18f, out left, out rest);
                DockBuilder.SplitNode(rest, DockBuilder.Direction.Right, 0.28f, out right, out rest);
                DockBuilder.SplitNode(rest, DockBuilder.Direction.Down, 0.30f, out bottom, out center);
                Dock(left, "Hierarchy");
                Dock(right, "Inspector");
                Dock(bottom, BottomTabs);
                Dock(center, CenterTabs);
                break;
        }

        DockBuilder.Finish(dockId);
    }

    /// <summary>Names of the saved layouts in a folder, sorted.</summary>
    public static IReadOnlyList<string> Saved(string folder) =>
        Directory.Exists(folder) ? [.. Directory.GetFiles(folder, "*.ini").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.OrdinalIgnoreCase)] : [];

    /// <summary>The file of a saved layout; null for a name that is empty or not a plain file name.</summary>
    public static string? FileOf(string folder, string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length == 0 || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || trimmed.All(c => c == '.') ? null : Path.Combine(folder, trimmed + ".ini");
    }

    private static void Dock(uint node, params string[] windows)
    {
        foreach (var window in windows)
        {
            DockBuilder.DockWindow(EditorWindows.Id(window), node);
        }
    }
}
