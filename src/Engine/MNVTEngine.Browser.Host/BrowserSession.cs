using System.Globalization;
using System.Text;
using CefSharp;
using CefSharp.OffScreen;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.BrowserHosting.Host;

/// <summary>One off-screen browser: its CEF browser, shared frame, security policy and event forwarding.</summary>
internal sealed class BrowserSession : IDisposable
{
    /// <summary>
    /// <c>window.gameApi</c>, registered with DevTools (<c>Page.addScriptToEvaluateOnNewDocument</c>) so it runs in
    /// every document before the page's own scripts; <c>ALLOWED</c> is the policy's origin check
    /// (<see cref="BridgeOrigins"/>). Messages go to the host through CefSharp's <c>PostMessage</c>; the host checks the
    /// frame's URL again, and the game checks once more.
    /// </summary>
    private const string BridgeScript = """
        (function () {
          if (window.gameApi || window !== window.top || !(ALLOWED)(location.href)) return;
          // CefSharp adds its object after document-start scripts: look it up when a message is sent.
          const post = (message) => (window.CefSharp || window.cefSharp).PostMessage(Object.assign(message, { t: 'TOKEN' }));
          const pending = new Map();
          const listeners = [];
          let next = 0;
          window.gameApi = Object.freeze({
            sendMessage(name, data) { post({ k: 'm', name: String(name), data: data === undefined ? null : data }); },
            log(text) { post({ k: 'l', text: String(text) }); },
            request(name, data) {
              return new Promise((resolve, reject) => {
                const id = ++next;
                pending.set(id, { resolve, reject });
                post({ k: 'r', id, name: String(name), data: data === undefined ? null : data });
              });
            },
            onMessage(listener) { if (typeof listener === 'function') listeners.push(listener); },
            __resolve(id, ok, value) {
              const p = pending.get(id);
              if (!p) return;
              pending.delete(id);
              if (ok) p.resolve(value); else p.reject(new Error(String(value)));
            },
            __deliver(name, data) {
              for (const listener of listeners) { try { listener(name, data); } catch (e) { console.error(e); } }
            },
          });
          window.dispatchEvent(new Event('gameapiready'));
        })();
        """;

    private readonly BrowserHost _host;
    private readonly ChromiumWebBrowser _browser;
    private readonly bool _transparent;
    private readonly Lock _frameLock = new();
    private readonly Lock _audioLock = new();
    private SharedFrame? _frame;
    private SharedAudio? _audio;
    private int _audioGeneration;
    private CefSharp.DevTools.DevToolsClient? _devTools;
    private bool _pageStarted;

    /// <summary>Known only to the bridge script: messages without it come from a page that called CefSharp's API itself.</summary>
    private readonly string _bridgeToken = Guid.NewGuid().ToString("N");
    private int _generation;
    private bool _disposed;

    public BrowserSession(BrowserHost host, int id, JObject create)
    {
        (_host, Id) = (host, id);
        Policy = new BrowserSecurityPolicy(
            Enum.Parse<BrowserSecurityProfile>(create.Value<string>("profile") ?? nameof(BrowserSecurityProfile.Restricted)),
            create["domains"]?.ToObject<string[]>() ?? [],
            (BrowserCapabilities)(create.Value<int?>("capabilities") ?? 0));
        _transparent = create.Value<bool?>("transparent") ?? false;
        Muted = create.Value<bool?>("muted") ?? false;
        var width = Math.Clamp(create.Value<int?>("width") ?? 1280, 1, 8192);
        var height = Math.Clamp(create.Value<int?>("height") ?? 720, 1, 8192);
        var settings = new BrowserSettings(autoDispose: true)
        {
            WindowlessFrameRate = Math.Clamp(create.Value<int?>("frameRate") ?? 30, 1, 120),
            BackgroundColor = _transparent ? 0x00000000u : 0xFFFFFFFFu,
        };

        _browser = new ChromiumWebBrowser("about:blank", settings, requestContext: null, automaticallyCreateBrowser: false)
        {
            RequestHandler = new SessionRequestHandler(this),
            LifeSpanHandler = new SessionLifeSpanHandler(this),
            DownloadHandler = new DenyDownloads(),
            PermissionHandler = new DenyPermissions(this),
            JsDialogHandler = new SuppressDialogs(this),
            RenderProcessMessageHandler = new PageErrorReporter(this),
            AudioHandler = new GameAudioHandler(this),
        };
        _browser.Size = new System.Drawing.Size(width, height);
        _browser.JavascriptObjectRepository.Settings.JavascriptBindingApiEnabled = Policy.Capabilities != BrowserCapabilities.None && Policy.Profile != BrowserSecurityProfile.Restricted;
        _browser.Paint += OnPaint;
        _browser.BrowserInitialized += OnInitialized;
        _browser.FrameLoadStart += (_, e) =>
        {
            if (e.Frame.IsMain && !IsInitialBlank(e.Url))
            {
                _pageStarted = true;
                SendUrl(BrowserProtocol.LoadStarted, e.Url);
            }
        };
        _browser.FrameLoadEnd += (_, e) =>
        {
            if (e.Frame.IsMain && !IsInitialBlank(e.Url))
            {
                var done = Event(BrowserProtocol.LoadCompleted);
                done["url"] = e.Url;
                done["status"] = e.HttpStatusCode;
                _host.Send(done);
            }
        };
        _browser.LoadError += (_, e) =>
        {
            if (e.Frame.IsMain && e.ErrorCode != CefErrorCode.Aborted)
            {
                var error = Event(BrowserProtocol.LoadError);
                error["url"] = e.FailedUrl;
                error["code"] = (int)e.ErrorCode;
                error["error"] = e.ErrorText;
                _host.Send(error);
            }
        };
        _browser.TitleChanged += (_, e) => SendText(BrowserProtocol.Title, "title", e.Title);
        _browser.AddressChanged += (_, e) =>
        {
            if (!IsInitialBlank(e.Address))
            {
                SendUrl(BrowserProtocol.Address, e.Address);
            }
        };
        _browser.LoadingStateChanged += (_, e) =>
        {
            var state = Event(BrowserProtocol.NavigationState);
            (state["canGoBack"], state["canGoForward"], state["loading"]) = (e.CanGoBack, e.CanGoForward, e.IsLoading);
            _host.Send(state);
        };
        _browser.ConsoleMessage += (_, e) =>
        {
            var console = Event(BrowserProtocol.Console);
            (console["level"], console["message"], console["source"], console["line"]) = (e.Level.ToString(), e.Message, e.Source, e.Line);
            _host.Send(console);
        };
        _browser.JavascriptMessageReceived += OnPageMessage;

        var html = create.Value<string>("html");
        InlineHtml = Policy.Profile == BrowserSecurityProfile.TrustedLocal ? html : null;
        StartUrl = html is null ? create.Value<string>("url") ?? "about:blank" : InlineHtml is not null ? InlineUrl : DataUrl(html);
        _browser.CreateBrowser(WindowInfo(), settings);
    }

    public int Id { get; }

    /// <summary>CEF's id of the browser, once created (0 before).</summary>
    public int CefId { get; private set; }

    public BrowserSecurityPolicy Policy { get; }

    public bool Muted { get; private set; }

    /// <summary>The page <see cref="BrowserProtocol.LoadHtml"/> set, served at <see cref="InlineUrl"/> (trusted-local browsers).</summary>
    public string? InlineHtml { get; private set; }

    private string StartUrl { get; set; }

    private string InlineUrl => $"{BrowserSecurityPolicy.GameScheme}://inline/{Id.ToString(CultureInfo.InvariantCulture)}.html";

    public void Handle(string type, JObject message)
    {
        var host = _browser.IsBrowserInitialized ? _browser.GetBrowserHost() : null;
        switch (type)
        {
            case BrowserProtocol.Navigate:
                InlineHtml = null;
                Load(message.Value<string>("url") ?? "about:blank");
                break;
            case BrowserProtocol.LoadHtml:
                var html = message.Value<string>("html") ?? "";
                if (Policy.Profile == BrowserSecurityProfile.TrustedLocal)
                {
                    InlineHtml = html;
                    Load(InlineUrl + "?v=" + Environment.TickCount64.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    Load(DataUrl(html));
                }

                break;
            case BrowserProtocol.Reload:
                _browser.Reload(message.Value<bool?>("ignoreCache") ?? false);
                break;
            case BrowserProtocol.Stop:
                _browser.Stop();
                break;
            case BrowserProtocol.Back:
                _browser.Back();
                break;
            case BrowserProtocol.Forward:
                _browser.Forward();
                break;
            case BrowserProtocol.Evaluate:
                _ = EvaluateAsync(message.Value<int>("id"), message.Value<string>("script") ?? "");
                break;
            case BrowserProtocol.Resize:
                _browser.Size = new System.Drawing.Size(Math.Clamp(message.Value<int>("width"), 1, 8192), Math.Clamp(message.Value<int>("height"), 1, 8192));
                break;
            case BrowserProtocol.Visibility:
                host?.WasHidden(!(message.Value<bool?>("visible") ?? true));
                break;
            case BrowserProtocol.Focus:
                host?.SetFocus(message.Value<bool?>("focused") ?? true);
                break;
            case BrowserProtocol.Zoom:
                host?.SetZoomLevel(message.Value<double>("level"));
                break;
            case BrowserProtocol.FrameRate:
                if (host is not null)
                {
                    host.WindowlessFrameRate = Math.Clamp(message.Value<int>("fps"), 1, 120);
                }

                break;
            case BrowserProtocol.Mute:
                Muted = message.Value<bool?>("muted") ?? true;
                host?.SetAudioMuted(Muted);
                break;
            case BrowserProtocol.MouseMove:
                host?.SendMouseMoveEvent(Mouse(message), message.Value<bool?>("leave") ?? false);
                break;
            case BrowserProtocol.MouseButton:
                host?.SendMouseClickEvent(Mouse(message), (MouseButtonType)Math.Clamp(message.Value<int>("button"), 0, 2), message.Value<bool>("up"), Math.Clamp(message.Value<int?>("clicks") ?? 1, 1, 3));
                break;
            case BrowserProtocol.MouseWheel:
                host?.SendMouseWheelEvent(Mouse(message), message.Value<int>("dx"), message.Value<int>("dy"));
                break;
            case BrowserProtocol.Key:
                host?.SendKeyEvent(new KeyEvent
                {
                    Type = message.Value<string>("kind") switch { "down" => KeyEventType.RawKeyDown, "up" => KeyEventType.KeyUp, _ => KeyEventType.Char },
                    WindowsKeyCode = message.Value<int>("code"),
                    NativeKeyCode = message.Value<int?>("native") ?? 0,
                    Modifiers = (CefEventFlags)(message.Value<int?>("modifiers") ?? 0),
                });
                break;
            case BrowserProtocol.PostToPage:
                RunBridge($"window.gameApi && window.gameApi.__deliver({JsonConvert.ToString(message.Value<string>("name") ?? "")}, {message["data"]?.ToString(Formatting.None) ?? "null"});");
                break;
            case BrowserProtocol.RequestResult:
                RunBridge($"window.gameApi && window.gameApi.__resolve({message.Value<int>("id").ToString(CultureInfo.InvariantCulture)}, {(message.Value<bool>("ok") ? "true" : "false")}, {message["value"]?.ToString(Formatting.None) ?? "null"});");
                break;
            case BrowserProtocol.CrashRenderer:
                _ = CrashRendererAsync();
                break;
            default:
                throw new InvalidOperationException($"Unknown browser command '{type}'.");
        }
    }

    public JObject Event(string type) => BrowserProtocol.New(type, Id);

    public void Send(JObject message) => _host.Send(message);

    public void SendBlocked(string what, string url)
    {
        var blocked = Event(BrowserProtocol.Blocked);
        blocked["what"] = what;
        blocked["url"] = url;
        _host.Send(blocked);
    }

    /// <summary>The JavaScript origin check of the bridge, the same rules as <see cref="BrowserSecurityPolicy.IsBridgeAllowed"/>; null when no page gets it.</summary>
    public static string? BridgeOrigins(BrowserSecurityPolicy policy)
    {
        if ((policy.Capabilities & (BrowserCapabilities.Messages | BrowserCapabilities.Requests)) == 0 || policy.Profile == BrowserSecurityProfile.Restricted)
        {
            return null;
        }

        var remote = (policy.Capabilities & BrowserCapabilities.RemoteBridge) != 0;
        var anyHost = remote && policy.AllowedDomains.Count == 0 && policy.Profile == BrowserSecurityProfile.RemoteWeb;
        string[] hosts = remote || policy.Profile == BrowserSecurityProfile.TrustedLocal
            ? [.. policy.AllowedDomains.Select(d => d.Trim().TrimStart('.').ToLowerInvariant()).Where(d => d.Length > 0)]
            : [];
        return $$"""
            (u) => {
              let x;
              try { x = new URL(u); } catch (e) { return false; }
              if (x.protocol === 'mnvt:') return {{(policy.Profile == BrowserSecurityProfile.TrustedLocal ? "true" : "false")}};
              if (x.protocol !== 'https:' && x.protocol !== 'http:') return false;
              const hosts = {{JsonConvert.SerializeObject(hosts)}};
              return {{(anyHost ? "true" : "false")}} || hosts.some(d => x.hostname === d || x.hostname.endsWith('.' + d));
            }
            """;
    }

    public void Dispose()
    {
        _devTools?.Dispose();
        lock (_frameLock)
        {
            _disposed = true;
            _browser.Paint -= OnPaint;
            _frame?.Dispose();
            _frame = null;
        }

        lock (_audioLock)
        {
            _audio?.Dispose();
            _audio = null;
        }

        _browser.Dispose();
    }

    private static WindowInfo WindowInfo()
    {
        var info = new WindowInfo();
        info.SetAsWindowless(IntPtr.Zero);
        return info;
    }

    /// <summary>HTML for browsers that are not trusted-local: a data: URL has an opaque origin and never gets the bridge.</summary>
    private static string DataUrl(string html) => "data:text/html;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(html));

    private static MouseEvent Mouse(JObject message) => new(message.Value<int>("x"), message.Value<int>("y"), (CefEventFlags)(message.Value<int?>("modifiers") ?? 0));

    private void Load(string url)
    {
        if (!Policy.IsNavigationAllowed(url))
        {
            SendBlocked("navigation", url);
            return;
        }

        _browser.Load(url);
    }

    private bool IsInitialBlank(string url) => !_pageStarted && url == "about:blank" && StartUrl != "about:blank";

    private void OnInitialized(object? sender, EventArgs e)
    {
        var browser = _browser.GetBrowser();
        CefId = browser.Identifier;
        var host = browser.GetHost();
        host.SetAudioMuted(Muted);
        var created = Event(BrowserProtocol.Created);
        created["width"] = _browser.Size.Width;
        created["height"] = _browser.Size.Height;
        _host.Send(created);
        _ = StartAsync();
    }

    /// <summary>Registers the bridge for every new document, then loads the first page.</summary>
    private async Task StartAsync()
    {
        try
        {
            if (BridgeOrigins(Policy) is { } origins)
            {
                // The DevTools session stays open: scripts it registered end with it. Page.enable is required for them to run.
                _devTools = _browser.GetDevToolsClient();
                await _devTools.Page.EnableAsync().ConfigureAwait(false);
                await _devTools.Page.AddScriptToEvaluateOnNewDocumentAsync(BridgeScript.Replace("ALLOWED", origins, StringComparison.Ordinal).Replace("TOKEN", _bridgeToken, StringComparison.Ordinal)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The page still loads; it reports the bridge as missing.
            Console.Error.WriteLine($"Browser {Id}: the bridge could not be registered: {ex.Message}");
        }

        Load(StartUrl);
    }

    /// <summary>A page started playing: a new audio ring (its format may differ from the last one). CEF's audio thread.</summary>
    public void OnAudioStarted(int channels, int sampleRate)
    {
        lock (_audioLock)
        {
            if (_disposed)
            {
                return;
            }

            _audio?.Dispose();
            _audio = SharedAudio.Create(SharedAudio.NameFor(_host.Session, Id, ++_audioGeneration), channels, sampleRate);
            var audio = Event(BrowserProtocol.Audio);
            (audio["name"], audio["channels"], audio["sampleRate"]) = (_audio.Name, channels, sampleRate);
            _host.Send(audio);
        }
    }

    public void OnAudioPacket(nint planar, int frames)
    {
        lock (_audioLock)
        {
            if (!_disposed && _audio is { } audio && planar != 0 && frames > 0)
            {
                audio.Write(planar, frames);
            }
        }
    }

    private void OnPaint(object? sender, OnPaintEventArgs e)
    {
        e.Handled = true; // the pixels go to shared memory; CefSharp's own bitmap copy is not needed
        if (e.IsPopup)
        {
            return; // shortcut: <select> dropdown popups are not drawn; composite PopupBuffer when a game needs them
        }

        lock (_frameLock)
        {
            if (_disposed)
            {
                return;
            }

            var full = false;
            if (_frame is null || _frame.Width != e.Width || _frame.Height != e.Height)
            {
                _frame?.Dispose();
                _frame = SharedFrame.Create(SharedFrame.NameFor(_host.Session, Id, ++_generation), e.Width, e.Height);
                var surface = Event(BrowserProtocol.Surface);
                (surface["name"], surface["width"], surface["height"]) = (_frame.Name, e.Width, e.Height);
                _host.Send(surface);
                full = true;
            }

            var dirty = full ? new CefSharp.Structs.Rect(0, 0, e.Width, e.Height) : e.DirtyRect;
            _frame.Write(e.BufferHandle, e.Width, dirty.X, dirty.Y, dirty.Width, dirty.Height, _transparent);
        }
    }

    private void OnPageMessage(object? sender, JavascriptMessageReceivedEventArgs e)
    {
        try
        {
            ForwardPageMessage(e);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A malformed page message is dropped (and reported); it must not end CEF's callback thread.
            Console.Error.WriteLine($"Browser {Id}: page message dropped: {ex}");
        }
    }

    private void ForwardPageMessage(JavascriptMessageReceivedEventArgs e)
    {
        if (e.Message is not IDictionary<string, object> message || !message.TryGetValue("t", out var token) || token as string != _bridgeToken)
        {
            return;
        }

        // The bridge exists in the top frame only; CefSharp may have disposed the sending frame already.
        string origin;
        try
        {
            origin = e.Frame.Url;
        }
        catch (ObjectDisposedException)
        {
            origin = _browser.Address;
        }

        // The page may have navigated to an origin without the bridge since the message was posted: check again.
        if (!Policy.IsBridgeAllowed(origin))
        {
            return;
        }

        JToken Data(string key) => message.TryGetValue(key, out var value) && value is not null ? JToken.FromObject(value) : JValue.CreateNull();
        string Text(string key) => message.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" : "";
        var kind = Text("k");
        JObject forward;
        switch (kind)
        {
            case "m":
                forward = Event(BrowserProtocol.Message);
                (forward["name"], forward["data"]) = (Text("name"), Data("data"));
                break;
            case "r":
                forward = Event(BrowserProtocol.Request);
                (forward["id"], forward["name"], forward["data"]) = (Convert.ToInt32(message["id"], CultureInfo.InvariantCulture), Text("name"), Data("data"));
                break;
            case "l":
                forward = Event(BrowserProtocol.Log);
                forward["text"] = Text("text");
                break;
            default:
                return;
        }

        forward["origin"] = origin;
        _host.Send(forward);
    }

    private async Task EvaluateAsync(int requestId, string script)
    {
        var result = Event(BrowserProtocol.EvaluateResult);
        result["id"] = requestId;
        try
        {
            // Through the frame: it runs as soon as the page has a script context instead of refusing before.
            var response = await _browser.GetMainFrame().EvaluateScriptAsync(script, "mnvt://evaluate", 1, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            result["ok"] = response.Success;
            if (response.Success)
            {
                result["value"] = response.Result is null ? JValue.CreateNull() : JToken.FromObject(response.Result);
            }
            else
            {
                result["error"] = response.Message;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any failure answers the game: an unanswered evaluation would wait forever.
            Console.Error.WriteLine($"Browser {Id}: evaluate failed: {ex}");
            result["ok"] = false;
            result["error"] = ex.Message;
        }

        _host.Send(result);
    }

    /// <summary>DevTools Page.crash: the renderer dies, the page's URL stays (so Reload brings it back).</summary>
    private async Task CrashRendererAsync()
    {
        try
        {
            using var devTools = _browser.GetDevToolsClient();
            await devTools.Page.CrashAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The renderer usually dies before DevTools answers; anything else is reported.
            Console.Error.WriteLine($"Browser {Id}: crash request: {ex.Message}");
        }
    }

    private void RunBridge(string script)
    {
        if (_browser.IsBrowserInitialized && _browser.GetBrowser().MainFrame is { IsValid: true } frame && Policy.IsBridgeAllowed(frame.Url))
        {
            frame.ExecuteJavaScriptAsync(script);
        }
    }

    private void SendUrl(string type, string url) => SendText(type, "url", url);

    private void SendText(string type, string key, string value)
    {
        var message = Event(type);
        message[key] = value;
        _host.Send(message);
    }
}
