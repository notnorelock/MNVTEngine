using ImGuiNET;

namespace MNVTEngine.Editor;

/// <summary>
/// A menu item described as data, so the same menus show in the native menu bar (<see cref="NativeMenuBar"/>) and in
/// ImGui (context menus, and the menu bar where no native one exists).
/// </summary>
internal sealed record MenuEntry(string Label, string? Shortcut = null, Action? Action = null, bool Enabled = true, bool Checked = false, IReadOnlyList<MenuEntry>? Children = null)
{
    public static MenuEntry Separator { get; } = new("-");

    public bool IsSeparator => ReferenceEquals(this, Separator);

    public static MenuEntry Submenu(string label, IReadOnlyList<MenuEntry> children) => new(label, Children: children);
}

/// <summary>A top-level menu: its title and its items, built when it opens (so enabled and checked states are current).</summary>
internal sealed record TopMenu(string Title, Func<IReadOnlyList<MenuEntry>> Items);

internal static class ImGuiMenus
{
    /// <summary>Draws the entries into the current ImGui menu or popup; a clicked item runs at once.</summary>
    public static void Draw(IEnumerable<MenuEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                ImGui.Separator();
            }
            else if (entry.Children is { } children)
            {
                if (ImGui.BeginMenu(entry.Label, entry.Enabled))
                {
                    Draw(children);
                    ImGui.EndMenu();
                }
            }
            else if (ImGui.MenuItem(entry.Label, entry.Shortcut, entry.Checked, entry.Enabled))
            {
                entry.Action?.Invoke();
            }
        }
    }

    /// <summary>A menu bar of top-level menus (for platforms without a native one).</summary>
    public static void DrawBar(IEnumerable<TopMenu> menus)
    {
        if (!ImGui.BeginMenuBar())
        {
            return;
        }

        foreach (var menu in menus)
        {
            if (ImGui.BeginMenu(menu.Title))
            {
                Draw(menu.Items());
                ImGui.EndMenu();
            }
        }

        ImGui.EndMenuBar();
    }
}
