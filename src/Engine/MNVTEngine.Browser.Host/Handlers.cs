using System.Globalization;
using CefSharp;
using CefSharp.Handler;

namespace MNVTEngine.BrowserHosting.Host;

/// <summary>Navigation and resource rules of the browser's profile; renderer crashes.</summary>
internal sealed class SessionRequestHandler(BrowserSession session) : RequestHandler
{

    protected override bool OnBeforeBrowse(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request, bool userGesture, bool isRedirect)
    {
        var allowed = frame.IsMain ? session.Policy.IsNavigationAllowed(request.Url) : session.Policy.IsSubresourceAllowed(request.Url) || request.Url == "about:blank";
        if (!allowed)
        {
            session.SendBlocked(frame.IsMain ? "navigation" : "frame", request.Url);
        }

        return !allowed; // true cancels
    }

    protected override bool OnOpenUrlFromTab(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, string targetUrl, WindowOpenDisposition targetDisposition, bool userGesture)
    {
        session.SendBlocked("popup", targetUrl);
        return true;
    }

    protected override IResourceRequestHandler GetResourceRequestHandler(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request, bool isNavigation, bool isDownload, string requestInitiator, ref bool disableDefaultHandling) => new SessionResourceHandler(session);

    protected override bool GetAuthCredentials(IWebBrowser chromiumWebBrowser, IBrowser browser, string originUrl, bool isProxy, string host, int port, string realm, string scheme, IAuthCallback callback) => false;

    protected override void OnRenderProcessTerminated(IWebBrowser chromiumWebBrowser, IBrowser browser, CefTerminationStatus status, int errorCode, string errorMessage)
    {
        var crashed = session.Event(BrowserProtocol.Crashed);
        crashed["status"] = status.ToString();
        crashed["error"] = string.IsNullOrEmpty(errorMessage) ? errorCode.ToString(CultureInfo.InvariantCulture) : errorMessage;
        session.Send(crashed);
    }
}

/// <summary>Subresources (scripts, images, fetch, WebSockets) outside the profile's sources are cancelled.</summary>
internal sealed class SessionResourceHandler(BrowserSession session) : ResourceRequestHandler
{
    protected override CefReturnValue OnBeforeResourceLoad(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request, IRequestCallback callback)
    {
        if (request.ResourceType == ResourceType.MainFrame || session.Policy.IsSubresourceAllowed(request.Url))
        {
            return CefReturnValue.Continue;
        }

        session.SendBlocked("resource", request.Url);
        return CefReturnValue.Cancel;
    }
}

/// <summary>Popups never open a window: they are reported to the game, which may navigate instead.</summary>
internal sealed class SessionLifeSpanHandler(BrowserSession session) : LifeSpanHandler
{
    protected override bool OnBeforePopup(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, string targetUrl, string targetFrameName, WindowOpenDisposition targetDisposition, bool userGesture, IPopupFeatures popupFeatures, IWindowInfo windowInfo, IBrowserSettings browserSettings, ref bool noJavascriptAccess, out IWebBrowser? newBrowser)
    {
        newBrowser = null;
        session.SendBlocked("popup", targetUrl);
        return true;
    }
}

internal sealed class DenyDownloads : DownloadHandler
{
    protected override bool CanDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, string url, string requestMethod) => false;
}

/// <summary>Camera, microphone, notifications, geolocation and the like are denied without a prompt.</summary>
internal sealed class DenyPermissions(BrowserSession session) : PermissionHandler
{
    protected override bool OnShowPermissionPrompt(IWebBrowser chromiumWebBrowser, IBrowser browser, ulong promptId, string requestingOrigin, PermissionRequestType requestedPermissions, IPermissionPromptCallback callback)
    {
        session.SendBlocked("permission:" + requestedPermissions, requestingOrigin);
        callback.Continue(PermissionRequestResult.Deny);
        return true;
    }

    protected override bool OnRequestMediaAccessPermission(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, string requestingOrigin, MediaAccessPermissionType requestedPermissions, IMediaAccessCallback callback)
    {
        session.SendBlocked("permission:" + requestedPermissions, requestingOrigin);
        callback.Cancel();
        return true;
    }
}

/// <summary>alert, confirm and prompt would wait for a dialog nobody sees: they are suppressed and logged.</summary>
internal sealed class SuppressDialogs(BrowserSession session) : JsDialogHandler
{
    protected override bool OnJSDialog(IWebBrowser chromiumWebBrowser, IBrowser browser, string originUrl, CefJsDialogType dialogType, string messageText, string defaultPromptText, IJsDialogCallback callback, ref bool suppressMessage)
    {
        var console = session.Event(BrowserProtocol.Console);
        (console["level"], console["message"], console["source"], console["line"]) = ("Warning", $"{dialogType} suppressed: {messageText}", originUrl, 0);
        session.Send(console);
        suppressMessage = true;
        return false;
    }

    protected override bool OnBeforeUnloadDialog(IWebBrowser chromiumWebBrowser, IBrowser browser, string messageText, bool isReload, IJsDialogCallback callback)
    {
        callback.Continue(true);
        return true;
    }
}

/// <summary>Uncaught page exceptions reach the game as console errors.</summary>
internal sealed class PageErrorReporter(BrowserSession session) : IRenderProcessMessageHandler
{
    public void OnContextCreated(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame)
    {
    }

    public void OnContextReleased(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame)
    {
    }

    public void OnFocusedNodeChanged(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IDomNode node)
    {
    }

    public void OnUncaughtException(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, JavascriptException exception)
    {
        var console = session.Event(BrowserProtocol.Console);
        (console["level"], console["message"], console["source"], console["line"]) = ("Error", "Uncaught " + exception.Message, frame.Url, 0);
        session.Send(console);
    }
}

/// <summary>
/// <c>mnvt://game/...</c> files of the game's <c>Assets/Web</c> (asked from the game over the pipe) and
/// <c>mnvt://inline/&lt;id&gt;.html</c> pages set with LoadHtml. Trusted-local browsers only.
/// </summary>
internal sealed class GameSchemeHandlerFactory(BrowserHost host) : ISchemeHandlerFactory
{
    public IResourceHandler? Create(IBrowser browser, IFrame frame, string schemeName, IRequest request)
    {
        var session = browser is null ? null : host.FindByCefId(browser.Identifier);
        if (session is null || session.Policy.Profile != BrowserSecurityProfile.TrustedLocal)
        {
            return ResourceHandler.ForErrorMessage("Not available", System.Net.HttpStatusCode.Forbidden);
        }

        var uri = new Uri(request.Url);
        if (uri.Host == "inline")
        {
            return uri.AbsolutePath == $"/{session.Id.ToString(CultureInfo.InvariantCulture)}.html" && session.InlineHtml is { } html
                ? ResourceHandler.FromString(html, mimeType: "text/html")
                : ResourceHandler.ForErrorMessage("Not found", System.Net.HttpStatusCode.NotFound);
        }

        return BrowserSecurityPolicy.TryGetGameAssetPath(request.Url, out var path)
            ? new GameAssetHandler(host, path)
            : ResourceHandler.ForErrorMessage("Not found", System.Net.HttpStatusCode.NotFound);
    }
}

internal sealed class GameAssetHandler(BrowserHost host, string assetPath) : ResourceHandler
{
    public override CefReturnValue ProcessRequestAsync(IRequest request, ICallback callback)
    {
        _ = Task.Run(async () =>
        {
            using (callback)
            {
                var answer = await host.RequestResourceAsync(assetPath).ConfigureAwait(false);
                if (answer?.Value<bool>("ok") == true && answer.Value<string>("data") is { } base64)
                {
                    StatusCode = 200;
                    MimeType = GetMimeType(Path.GetExtension(assetPath));
                    Stream = new MemoryStream(Convert.FromBase64String(base64));
                }
                else
                {
                    StatusCode = 404;
                    StatusText = "Not Found";
                    MimeType = "text/plain";
                    Stream = new MemoryStream("Not found"u8.ToArray());
                }

                callback.Continue();
            }
        });
        return CefReturnValue.ContinueAsync;
    }
}

/// <summary>
/// Page audio goes to the game's mixer, not to the speakers: with this handler Chromium hands over the decoded PCM
/// (planar float) instead of playing it, and the session writes it into its shared audio ring.
/// </summary>
internal sealed class GameAudioHandler(BrowserSession session) : CefSharp.Handler.AudioHandler
{
    public const int SampleRate = 48000;

    protected override bool GetAudioParameters(IWebBrowser chromiumWebBrowser, IBrowser browser, ref CefSharp.Structs.AudioParameters parameters)
    {
        parameters.ChannelLayout = CefSharp.Enums.ChannelLayout.LayoutStereo;
        parameters.SampleRate = SampleRate;
        return true;
    }

    protected override void OnAudioStreamStarted(IWebBrowser chromiumWebBrowser, IBrowser browser, CefSharp.Structs.AudioParameters parameters, int channels) =>
        session.OnAudioStarted(Math.Clamp(channels, 1, 2), parameters.SampleRate);

    protected override void OnAudioStreamPacket(IWebBrowser chromiumWebBrowser, IBrowser browser, IntPtr data, int noOfFrames, long pts) =>
        session.OnAudioPacket(data, noOfFrames);

    protected override void OnAudioStreamError(IWebBrowser chromiumWebBrowser, IBrowser browser, string errorMessage) =>
        Console.Error.WriteLine($"Browser {session.Id}: audio stream error: {errorMessage}");
}
