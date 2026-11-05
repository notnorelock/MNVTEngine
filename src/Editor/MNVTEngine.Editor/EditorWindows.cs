using System.Text.RegularExpressions;

namespace MNVTEngine.Editor;

/// <summary>
/// The editor's dockable windows: tab titles with an icon, on a stable ImGui id ("###Name") so docking, focusing and the
/// saved layout do not depend on the icon.
/// </summary>
internal static partial class EditorWindows
{
    private static readonly Dictionary<string, string> WindowIcons = new(StringComparer.Ordinal)
    {
        ["Hierarchy"] = Icons.ListTree,
        ["Inspector"] = Icons.Info,
        ["Scene"] = Icons.Boxes,
        ["Game"] = Icons.Gamepad2,
        ["Project"] = Icons.FolderOpen,
        ["Console"] = Icons.Terminal,
        ["Profiler"] = Icons.Activity,
        ["Script Debugger"] = Icons.Bug,
        ["Script Editor"] = Icons.FileCode,
        ["UI Editor"] = Icons.LayoutTemplate,
        ["Animator"] = Icons.Workflow,
        ["Build Settings"] = Icons.Hammer,
        ["Diagnostics"] = Icons.SlidersHorizontal,
    };

    /// <summary>What <c>ImGui.Begin</c> gets: the icon, the name, and the stable id.</summary>
    public static string Title(string name) => WindowIcons.TryGetValue(name, out var icon) ? $"{icon}  {name}###{name}" : name;

    /// <summary>The window for <c>SetWindowFocus</c> and the dock builder.</summary>
    public static string Id(string name) => WindowIcons.ContainsKey(name) ? $"###{name}" : name;

    /// <summary>
    /// A layout saved before the windows had icons names them "[Window][Hierarchy]": renamed to the stable ids, so the
    /// windows keep their docking.
    /// </summary>
    public static string MigrateLayout(string ini) =>
        WindowSection().Replace(ini, m => WindowIcons.ContainsKey(m.Groups[1].Value) ? $"[Window][###{m.Groups[1].Value}]" : m.Value);

    [GeneratedRegex(@"^\[Window\]\[([^\]#]+)\]", RegexOptions.Multiline)]
    private static partial Regex WindowSection();
}
