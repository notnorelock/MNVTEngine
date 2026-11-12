namespace MNVTEngine.BrowserHosting;

/// <summary>What a browser may load and whether its pages get the game bridge (<c>window.gameApi</c>).</summary>
public enum BrowserSecurityProfile
{
    /// <summary>
    /// Game-owned UI: <c>mnvt://</c> assets (and http/https hosts on the allowlist); the bridge is on for those origins
    /// with the capabilities granted.
    /// </summary>
    TrustedLocal,

    /// <summary>Remote websites (http/https; the allowlist restricts them when it is not empty); the bridge is off unless granted.</summary>
    RemoteWeb,

    /// <summary>Allowlisted https hosts and about:blank only, no bridge.</summary>
    Restricted,
}

/// <summary>What pages may do through the bridge. Nothing is granted by default.</summary>
[Flags]
public enum BrowserCapabilities
{
    None = 0,

    /// <summary><c>gameApi.sendMessage</c> and <c>gameApi.log</c> reach the game.</summary>
    Messages = 1,

    /// <summary><c>gameApi.request</c> calls the handlers the game registered (<c>RegisterRequestHandler</c>).</summary>
    Requests = 2,

    /// <summary>Also give the bridge to remote (http/https) pages, for <see cref="BrowserSecurityProfile.RemoteWeb"/>.</summary>
    RemoteBridge = 4,
}

/// <summary>
/// The navigation and bridge rules of one browser, applied by the host (CEF's request, popup, download and permission
/// handlers) and again by the game on every bridge message. Popups, downloads, permission prompts, <c>file://</c> and
/// external protocols are always refused.
/// </summary>
public sealed record BrowserSecurityPolicy(BrowserSecurityProfile Profile, IReadOnlyList<string> AllowedDomains, BrowserCapabilities Capabilities)
{
    /// <summary>The custom scheme of game-owned web assets: <c>mnvt://game/index.html</c>.</summary>
    public const string GameScheme = "mnvt";

    /// <summary>The asset folder <c>mnvt://game/</c> serves: <c>Assets/Web</c>.</summary>
    public const string GameWebRoot = "Web/";

    /// <summary>
    /// Maps <c>mnvt://game/ui/menu.html</c> to the asset path <c>Web/ui/menu.html</c> (a trailing slash means
    /// <c>index.html</c>). False for another host, and for any path that could leave <c>Assets/Web</c>: <c>..</c> or
    /// <c>.</c> segments (also percent-encoded), backslashes, colons, empty segments or control characters.
    /// </summary>
    public static bool TryGetGameAssetPath(string url, out string assetPath)
    {
        assetPath = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != GameScheme || !uri.Host.Equals("game", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Uri decodes %2e and resolves dot segments itself; encoded dots and separators are refused outright.
        if (url.Contains("%2e", StringComparison.OrdinalIgnoreCase) || url.Contains("%2f", StringComparison.OrdinalIgnoreCase) || url.Contains("%5c", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var raw = uri.AbsolutePath;
        string path;
        try
        {
            path = Uri.UnescapeDataString(raw);
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (path.EndsWith('/'))
        {
            path += "index.html";
        }

        path = path.TrimStart('/');
        if (path.Length == 0 || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl))
        {
            return false;
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment is "" or "." or "..")
            {
                return false;
            }
        }

        assetPath = GameWebRoot + path;
        return true;
    }

    public bool IsNavigationAllowed(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.AbsoluteUri is "about:blank" || (uri.Scheme == "data" && Profile != BrowserSecurityProfile.Restricted))
        {
            return true;
        }

        return uri.Scheme switch
        {
            GameScheme => Profile == BrowserSecurityProfile.TrustedLocal,
            "https" => IsAllowedHost(uri.Host, emptyAllows: Profile == BrowserSecurityProfile.RemoteWeb),
            "http" => Profile != BrowserSecurityProfile.Restricted && IsAllowedHost(uri.Host, emptyAllows: Profile == BrowserSecurityProfile.RemoteWeb),
            _ => false, // file://, javascript:, mailto:, custom protocols of other applications
        };
    }

    /// <summary>
    /// Whether a page may load a resource (script, image, fetch) from <paramref name="url"/>. Remote web pages load from
    /// anywhere (or the allowlist); game UI and restricted browsers only from <c>mnvt://</c> (game UI), <c>data:</c>,
    /// <c>blob:</c> and allowlisted hosts, so a remote script never runs next to the bridge.
    /// </summary>
    public bool IsSubresourceAllowed(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme switch
        {
            "data" or "blob" => true,
            GameScheme => Profile == BrowserSecurityProfile.TrustedLocal,
            "https" or "http" or "ws" or "wss" => (uri.Scheme is "https" or "wss" || Profile != BrowserSecurityProfile.Restricted)
                && IsAllowedHost(uri.Host, emptyAllows: Profile == BrowserSecurityProfile.RemoteWeb),
            _ => false,
        };
    }

    /// <summary>Whether a page at <paramref name="url"/> gets <c>window.gameApi</c>.</summary>
    public bool IsBridgeAllowed(string url)
    {
        if ((Capabilities & (BrowserCapabilities.Messages | BrowserCapabilities.Requests)) == 0 || Profile == BrowserSecurityProfile.Restricted
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsNavigationAllowed(url))
        {
            return false;
        }

        return uri.Scheme == GameScheme
            || ((uri.Scheme is "https" or "http") && ((Capabilities & BrowserCapabilities.RemoteBridge) != 0 || (Profile == BrowserSecurityProfile.TrustedLocal && IsAllowedHost(uri.Host, emptyAllows: false))));
    }

    private bool IsAllowedHost(string host, bool emptyAllows)
    {
        if (AllowedDomains.Count == 0)
        {
            return emptyAllows;
        }

        foreach (var domain in AllowedDomains)
        {
            var d = domain.Trim().TrimStart('.').ToLowerInvariant();
            if (d.Length > 0 && (host.Equals(d, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
