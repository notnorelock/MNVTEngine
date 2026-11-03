namespace MNVTEngine.Assets;

/// <summary>Safe handling of paths relative to a project's Assets folder (a trust boundary: paths come from project files).</summary>
public static class AssetPaths
{
    public static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Resolves a project-relative asset path, refusing anything outside <paramref name="assetsDirectory"/>.</summary>
    /// <exception cref="ArgumentException">Empty, rooted, or escaping path.</exception>
    public static string Resolve(string assetsDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException($"Asset path '{relativePath}' must be a non-empty path relative to Assets.", nameof(relativePath));
        }

        var root = Path.GetFullPath(assetsDirectory);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException($"Asset path '{relativePath}' leaves the Assets folder.", nameof(relativePath));
        }

        return full;
    }

    /// <summary>Canonical relative form (forward slashes) of a path inside <paramref name="assetsDirectory"/>.</summary>
    /// <exception cref="ArgumentException">The path escapes the Assets folder.</exception>
    public static string Normalize(string assetsDirectory, string relativePath) =>
        ToRelative(assetsDirectory, Resolve(assetsDirectory, relativePath));

    /// <summary>Canonical relative form of a path that has no folder on disk (packaged content), with the same checks as <see cref="Resolve"/>.</summary>
    /// <exception cref="ArgumentException">Empty, rooted, or escaping path.</exception>
    public static string Canonical(string relativePath) => Normalize(VirtualRoot, relativePath);

    private static readonly string VirtualRoot = Path.Combine(Path.GetTempPath(), "mnvt-virtual-root");

    /// <summary>Converts a full path under <paramref name="assetsDirectory"/> to the canonical relative form.</summary>
    public static string ToRelative(string assetsDirectory, string fullPath) =>
        Path.GetRelativePath(Path.GetFullPath(assetsDirectory), Path.GetFullPath(fullPath)).Replace(Path.DirectorySeparatorChar, '/');
}
