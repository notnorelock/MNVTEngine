using System.Text;

namespace MNVTEngine.IO;

/// <summary>
/// Paths of the virtual file system: independent of the operating system and a trust boundary (they come from scenes,
/// scripts, web pages and package indexes). The canonical form uses '/' separators, has no leading or trailing '/', no
/// empty, "." or ".." segments. A ".." inside a path is resolved; one that would leave the root is refused, as are rooted
/// paths ("C:\", "/x", "\\server"), ':' (drive letters, alternate data streams) and control characters.
/// <para>
/// Lookups ignore ASCII/ordinal case on every platform (<see cref="Comparer"/>), like the Windows file systems games are
/// authored on, so a game behaves the same from a package and from a folder. The stored spelling is kept for display.
/// </para>
/// </summary>
public static class VirtualPath
{
    /// <summary>How virtual paths compare: ordinal, ignoring case.</summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    public const StringComparison Comparison = StringComparison.OrdinalIgnoreCase;

    /// <summary>The canonical form of <paramref name="path"/>; "" is the root.</summary>
    /// <exception cref="ArgumentException">The path is rooted, escapes the root, or holds invalid characters.</exception>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return TryNormalize(path, out var normalized, out var error) ? normalized : throw new ArgumentException(error, nameof(path));
    }

    /// <summary>Like <see cref="Normalize"/>, without throwing.</summary>
    public static bool TryNormalize(string? path, out string normalized, out string? error)
    {
        normalized = "";
        error = null;
        if (path is null)
        {
            error = "Path is null.";
            return false;
        }

        if (path.Length > 0 && (path[0] is '/' or '\\'))
        {
            error = $"Virtual path '{path}' must be relative.";
            return false;
        }

        var segments = new List<string>();
        foreach (var segment in path.Split('/', '\\'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    error = $"Virtual path '{path}' leaves the root.";
                    return false;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            foreach (var c in segment)
            {
                if (c == ':' || char.IsControl(c))
                {
                    error = $"Virtual path '{path}' contains an invalid character.";
                    return false;
                }
            }

            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                // Windows strips these, so "a." and "a" would name the same physical file.
                error = $"Virtual path '{path}' has a segment ending in '.' or ' '.";
                return false;
            }

            segments.Add(segment);
        }

        normalized = string.Join('/', segments);
        return true;
    }

    /// <summary>Joins two virtual paths and normalizes the result.</summary>
    /// <exception cref="ArgumentException">The result is invalid or escapes the root.</exception>
    public static string Combine(string first, string second) => Normalize(first.Length == 0 ? second : first + "/" + second);

    /// <summary>The parent folder of a canonical path; "" for a file in the root.</summary>
    public static string GetDirectory(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    /// <summary>True when canonical <paramref name="path"/> is <paramref name="directory"/> or inside it ("" holds everything).</summary>
    public static bool IsUnder(string path, string directory)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(directory);
        return directory.Length == 0
            || (path.StartsWith(directory, Comparison) && (path.Length == directory.Length || path[directory.Length] == '/'));
    }

    /// <summary>Canonical <paramref name="path"/> relative to <paramref name="directory"/>; null when it is not inside it.</summary>
    public static string? RelativeTo(string path, string directory) =>
        !IsUnder(path, directory) ? null
        : directory.Length == 0 ? path
        : path.Length == directory.Length ? ""
        : path[(directory.Length + 1)..];

    internal static byte[] ToUtf8(string path) => Encoding.UTF8.GetBytes(path);
}
