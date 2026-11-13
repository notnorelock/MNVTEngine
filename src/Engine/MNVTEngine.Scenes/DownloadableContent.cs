namespace MNVTEngine;

/// <summary>Implemented by the game host (exported games); gameplay code uses <see cref="DownloadableContent"/>.</summary>
public interface IDownloadableContentHost
{
    /// <summary>DLC packages present but encrypted with a key the game does not have (file names).</summary>
    IReadOnlyList<string> WaitingForKey { get; }

    bool Mount(string fileName, ReadOnlySpan<byte> key, out string? problem);
}

/// <summary>
/// DLC whose key the game did not ship with: a game with an online backend gets the key from its server (after a
/// purchase or login) and mounts the package here; its assets are then loadable like any other. Packages must still be
/// signed with the game's key. Only exported games have DLC; elsewhere <see cref="Mount"/> fails with an explanation.
/// </summary>
public static class DownloadableContent
{
    private static IDownloadableContentHost? _host;

    /// <summary>DLC packages in the game's <c>DLC</c> folder that wait for their key.</summary>
    public static IReadOnlyList<string> WaitingForKey => _host?.WaitingForKey ?? [];

    /// <summary>Mounts <c>&lt;Game&gt;_Data/DLC/&lt;fileName&gt;</c> with a 32-byte key.</summary>
    /// <returns>False, with the reason, when the package is missing, damaged, not signed by the game, or the key is wrong.</returns>
    public static bool Mount(string fileName, ReadOnlySpan<byte> key, out string? error)
    {
        if (_host is null)
        {
            error = "Downloadable content is only available in exported games.";
            return false;
        }

        return _host.Mount(fileName, key, out error);
    }

    /// <summary>Set by the game host while a game runs; null clears it.</summary>
    public static void SetHost(IDownloadableContentHost? host) => _host = host;
}
