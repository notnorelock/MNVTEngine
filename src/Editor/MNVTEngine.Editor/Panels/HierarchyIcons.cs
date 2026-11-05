using SysVector4 = System.Numerics.Vector4;

namespace MNVTEngine.Editor.Panels;

/// <summary>
/// The icons of Hierarchy rows: one for what a GameObject is (its most telling component: camera, light, renderer...),
/// and small badges for its other components.
/// </summary>
internal static class HierarchyIcons
{
    private static readonly SysVector4 Muted = new(0.62f, 0.64f, 0.7f, 1f);
    private static readonly SysVector4 Folder = new(0.86f, 0.76f, 0.5f, 1f);
    private static readonly SysVector4 Script = new(0.98f, 0.68f, 0.38f, 1f);

    /// <summary>Component type names (or a base type's) in priority order: the first one present gives the row its icon.</summary>
    private static readonly (string Type, string Icon, SysVector4 Color)[] Looks =
    [
        ("MissingComponent", Icons.TriangleAlert, new(1f, 0.45f, 0.45f, 1f)),
        ("Camera", Icons.Video, new(0.45f, 0.72f, 1f, 1f)),
        ("Light", Icons.Lightbulb, new(1f, 0.83f, 0.35f, 1f)),
        ("Light2D", Icons.Lightbulb, new(1f, 0.83f, 0.35f, 1f)),
        ("GlobalLight2D", Icons.Sun, new(1f, 0.83f, 0.35f, 1f)),
        ("AmbientLight", Icons.CloudSun, new(1f, 0.83f, 0.35f, 1f)),
        ("SceneEnvironment", Icons.Mountain, new(0.55f, 0.8f, 1f, 1f)),
        ("UIDocument", Icons.LayoutTemplate, new(0.4f, 0.86f, 0.8f, 1f)),
        ("AudioListener", Icons.Headphones, new(0.86f, 0.62f, 1f, 1f)),
        ("AudioSource", Icons.Volume2, new(0.86f, 0.62f, 1f, 1f)),
        ("AudioMixer", Icons.AudioLines, new(0.86f, 0.62f, 1f, 1f)),
        ("CharacterController", Icons.PersonStanding, new(0.55f, 0.86f, 0.45f, 1f)),
        ("ModelAnimator", Icons.PersonStanding, new(0.55f, 0.72f, 1f, 1f)),
        ("Tilemap", Icons.Grid3x3, new(0.55f, 0.86f, 0.45f, 1f)),
        ("SpriteAnimator", Icons.Film, new(0.55f, 0.72f, 1f, 1f)),
        ("SpriteRenderer", Icons.Image, new(0.75f, 0.8f, 0.9f, 1f)),
        ("MeshRenderer", Icons.Box, new(0.75f, 0.8f, 0.9f, 1f)),
        ("MissionRunner", Icons.ScrollText, Script),
        ("Rigidbody", Icons.Dumbbell, new(0.55f, 0.86f, 0.45f, 1f)),
        ("Rigidbody2D", Icons.Dumbbell, new(0.55f, 0.86f, 0.45f, 1f)),
        ("Collider", Icons.Shapes, new(0.55f, 0.86f, 0.45f, 1f)),
        ("Collider2D", Icons.Shapes, new(0.55f, 0.86f, 0.45f, 1f)),
        ("ModelAnimatorController", Icons.Workflow, new(0.55f, 0.72f, 1f, 1f)),
    ];

    /// <summary>The row icon: the first matching look, else a script, a folder (an empty with children) or an empty.</summary>
    public static (string Icon, SysVector4 Color) Primary(GameObject gameObject, bool open)
    {
        foreach (var (type, icon, color) in Looks)
        {
            if (gameObject.Components.Any(c => Is(c, type)))
            {
                return (icon, color);
            }
        }

        if (gameObject.Components.Any(IsScript))
        {
            return (Icons.FileCode, Script);
        }

        return gameObject.Transform.ChildCount > 0 ? (open ? Icons.FolderOpen : Icons.Folder, Folder) : (Icons.Box, Muted);
    }

    /// <summary>Badges for the components the row icon does not already show (one per icon), with their type names.</summary>
    public static List<(string Icon, SysVector4 Color, string Name)> Badges(GameObject gameObject, string primary)
    {
        var badges = new List<(string, SysVector4, string)>();
        foreach (var component in gameObject.Components)
        {
            if (component is Transform or HierarchyStyle)
            {
                continue;
            }

            var (icon, color) = For(component);
            var existing = badges.FindIndex(b => b.Item1 == icon);
            if (existing >= 0)
            {
                badges[existing] = (icon, color, $"{badges[existing].Item3}, {component.GetType().Name}");
            }
            else if (icon != primary)
            {
                badges.Add((icon, color, component.GetType().Name));
            }
        }

        return badges;
    }

    /// <summary>A component's icon (the Inspector's component headers use it too).</summary>
    public static (string Icon, SysVector4 Color) For(Component component)
    {
        if (component is Transform)
        {
            return (Icons.Move3D, new SysVector4(0.55f, 0.72f, 1f, 1f));
        }

        if (component is HierarchyStyle)
        {
            return (Icons.Palette, Muted);
        }

        foreach (var (type, icon, color) in Looks)
        {
            if (Is(component, type))
            {
                return (icon, color);
            }
        }

        return IsScript(component) ? (Icons.FileCode, Script) : (Icons.Puzzle, Muted);
    }

    /// <summary>Game code: components from outside the engine's assemblies.</summary>
    private static bool IsScript(Component component) =>
        component.GetType().Assembly.GetName().Name?.StartsWith("MNVTEngine.", StringComparison.Ordinal) != true;

    private static bool Is(Component component, string typeName)
    {
        for (var type = component.GetType(); type is not null && type != typeof(Component); type = type.BaseType)
        {
            if (type.Name == typeName)
            {
                return true;
            }
        }

        return false;
    }
}
