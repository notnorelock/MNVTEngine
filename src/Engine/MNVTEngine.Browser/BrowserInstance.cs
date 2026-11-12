using MNVTEngine.BrowserHosting;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Browser;

/// <summary>How a browser is created. Pages get no bridge and no game files unless the profile and capabilities say so.</summary>
public sealed record BrowserOptions
{
    /// <summary>The first page: <c>mnvt://game/index.html</c> (the game's <c>Assets/Web</c>), https://, or about:blank.</summary>
    public string Url { get; init; } = "about:blank";

    /// <summary>HTML to show instead of <see cref="Url"/> (trusted-local browsers serve it from <c>mnvt://inline/</c>, others as a data: URL).</summary>
    public string? Html { get; init; }

    /// <summary>Page size in pixels (the texture size).</summary>
    public int Width { get; init; } = 1280;

    public int Height { get; init; } = 720;

    /// <summary>Transparent page background: the page's alpha reaches the texture (overlays). Opaque white otherwise.</summary>
    public bool Transparent { get; init; }

    /// <summary>Chromium's paint rate, 1 to 120.</summary>
    public int FrameRate { get; init; } = 30;

    public BrowserSecurityProfile Profile { get; init; } = BrowserSecurityProfile.RemoteWeb;

    /// <summary>Hosts (and their subdomains) the profile allows; see <see cref="BrowserSecurityPolicy"/>.</summary>
    public IReadOnlyList<string> AllowedDomains { get; init; } = [];

    public BrowserCapabilities Capabilities { get; init; }

    public bool Muted { get; init; }

    public BrowserSecurityPolicy Policy => new(Profile, AllowedDomains, Capabilities);
}

public enum BrowserState
{
    /// <summary>Waiting for the browser host (it starts with the first browser).</summary>
    Creating,

    Ready,

    /// <summary>The page's renderer or the whole browser host stopped; <see cref="BrowserInstance.Reload"/> recovers.</summary>
    Crashed,

    /// <summary>The browser host could not start; see <see cref="BrowserInstance.Error"/>.</summary>
    Failed,

    Closed,
}

/// <summary>A console message of the page.</summary>
public sealed record BrowserConsoleMessage(string Level, string Message, string Source, int Line);

/// <summary>A message the page sent with <c>gameApi.sendMessage(name, data)</c>.</summary>
public sealed record BrowserMessage(string Name, JToken Data, string Origin)
{
    /// <summary>The data as <typeparamref name="T"/> (Newtonsoft); default when it does not convert.</summary>
    public T? DataAs<T>() => Data.Type == JTokenType.Null ? default : Data.ToObject<T>();
}

/// <summary>JavaScript the game ran failed (threw, did not compile, or the page went away).</summary>
public sealed class BrowserScriptException(string message) : Exception(message);

/// <summary>
/// One off-screen browser. Commands go to <c>MNVTBrowserHost.exe</c> and return at once; events are raised on the game
/// thread (by the browser system each frame). Its page is drawn into a texture under <see cref="TextureId"/>: give a
/// sprite or material <c>asset:&lt;TextureId&gt;</c> (the surfaces do). Close it with <see cref="Dispose"/>.
/// </summary>
public sealed class BrowserInstance : IDisposable
{
    private readonly BrowserService _service;
    private readonly Dictionary<int, TaskCompletionSource<JToken?>> _evaluations = [];
    private readonly Dictionary<string, Func<JToken, Task<object?>>> _requestHandlers = new(StringComparer.Ordinal);
    private readonly Lock _frameLock = new();
    private SharedFrame? _frame;
    private (string Name, int Width, int Height)? _pendingSurface;
    private int _nextEvaluation;

    internal BrowserInstance(BrowserService service, int id, BrowserOptions options)
    {
        (_service, Id, Options) = (service, id, options);
        Policy = options.Policy;
        Url = options.Html is null ? options.Url : "";
        (Width, Height, Muted, FrameRate) = (options.Width, options.Height, options.Muted, options.FrameRate);
    }

    public int Id { get; }

    public BrowserOptions Options { get; }

    public BrowserSecurityPolicy Policy { get; }

    public BrowserState State { get; private set; } = BrowserState.Creating;

    /// <summary>Why the browser failed or crashed.</summary>
    public string? Error { get; private set; }

    /// <summary>The id the browser's texture is shown under: <c>new AssetRef(TextureId)</c>.</summary>
    public Guid TextureId { get; } = Guid.NewGuid();

    public string Url { get; private set; }

    public string Title { get; private set; } = "";

    public bool IsLoading { get; private set; } = true;

    public bool CanGoBack { get; private set; }

    public bool CanGoForward { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public bool Muted { get; private set; }

    public int FrameRate { get; private set; }

    public bool Visible { get; private set; } = true;

    public bool Focused { get; private set; }

    public double ZoomLevel { get; private set; }

    /// <summary>Paints Chromium delivered (several merge into one texture update when they come faster than frames).</summary>
    public long FramesPainted { get; private set; }

    /// <summary>Texture updates made from this browser's pixels.</summary>
    public long FramesUploaded { get; internal set; }

    /// <summary>The texture's size in pixels (0 before the first paint).</summary>
    public int TextureWidth { get; private set; }

    public int TextureHeight { get; private set; }

    /// <summary>
    /// Store the texture bottom row first, for meshes whose UV origin is the bottom left (the engine's built-in
    /// primitives); glTF models and sprites read textures top row first. Set it before the first frame.
    /// </summary>
    public bool FlipVertically { get; set; }

    /// <summary>Changes when the texture is recreated (resize, host restart).</summary>
    internal int SurfaceGeneration { get; private set; }

    public event Action<string>? LoadStarted;

    /// <summary>The main frame finished loading: the URL and the HTTP status (0 for non-HTTP pages).</summary>
    public event Action<string, int>? LoadCompleted;

    /// <summary>The main frame failed to load: the URL, Chromium's error code and text.</summary>
    public event Action<string, int, string>? LoadFailed;

    public event Action<string>? TitleChanged;

    public event Action<string>? AddressChanged;

    public event Action<BrowserConsoleMessage>? ConsoleMessage;

    /// <summary>A page message (<c>gameApi.sendMessage</c>); only from pages the bridge is allowed for, with <see cref="BrowserCapabilities.Messages"/>.</summary>
    public event Action<BrowserMessage>? MessageReceived;

    /// <summary>Something the security profile refused: what ("navigation", "resource", "popup", "permission:...") and the URL.</summary>
    public event Action<string, string>? Blocked;

    /// <summary>The page's renderer or the browser host stopped: the reason. <see cref="Reload"/> recovers.</summary>
    public event Action<string>? Crashed;

    public void Navigate(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        Url = url;
        Command(BrowserProtocol.Navigate, m => m["url"] = url);
    }

    public void LoadHtml(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        Command(BrowserProtocol.LoadHtml, m => m["html"] = html);
    }

    /// <summary>Reloads the page; after a crash, recreates the browser (and restarts the browser host if it stopped).</summary>
    public void Reload(bool ignoreCache = false)
    {
        if (State == BrowserState.Crashed && !_service.IsHostRunning)
        {
            _service.Recreate(this);
            return;
        }

        if (State == BrowserState.Crashed)
        {
            State = BrowserState.Ready;
            Error = null;
        }

        Command(BrowserProtocol.Reload, m => m["ignoreCache"] = ignoreCache);
    }

    public void Stop() => Command(BrowserProtocol.Stop);

    public void GoBack() => Command(BrowserProtocol.Back);

    public void GoForward() => Command(BrowserProtocol.Forward);

    /// <summary>Changes the page size; the texture follows with the next paint.</summary>
    public void Resize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        (Width, Height) = (Math.Min(width, 8192), Math.Min(height, 8192));
        Command(BrowserProtocol.Resize, m => (m["width"], m["height"]) = (Width, Height));
    }

    /// <summary>Hidden browsers stop painting (and stop running animation frames).</summary>
    public void SetVisible(bool visible)
    {
        if (Visible != visible)
        {
            Visible = visible;
            Command(BrowserProtocol.Visibility, m => m["visible"] = visible);
        }
    }

    public void SetFocus(bool focused)
    {
        if (Focused != focused)
        {
            Focused = focused;
            Command(BrowserProtocol.Focus, m => m["focused"] = focused);
        }
    }

    public void SetMuted(bool muted)
    {
        Muted = muted;
        Command(BrowserProtocol.Mute, m => m["muted"] = muted);
    }

    public void SetFrameRate(int framesPerSecond)
    {
        FrameRate = Math.Clamp(framesPerSecond, 1, 120);
        Command(BrowserProtocol.FrameRate, m => m["fps"] = FrameRate);
    }

    /// <summary>Chromium's zoom level: 0 is 100%, each step is 20% (1 = 120%, -1 = 83%).</summary>
    public void SetZoomLevel(double level)
    {
        ZoomLevel = level;
        Command(BrowserProtocol.Zoom, m => m["level"] = level);
    }

    /// <summary>
    /// Runs JavaScript in the page's main frame; the result is the value of the last expression (JSON values only).
    /// Completes on the game thread.
    /// </summary>
    /// <exception cref="BrowserScriptException">The script threw or the page went away.</exception>
    public Task<JToken?> ExecuteJavaScriptAsync(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var id = ++_nextEvaluation;
        var completion = new TaskCompletionSource<JToken?>();
        _evaluations[id] = completion;
        Command(BrowserProtocol.Evaluate, m => (m["id"], m["script"]) = (id, script));
        return completion.Task;
    }

    /// <summary>Sends a message to the page's <c>gameApi.onMessage</c> listeners (pages with the bridge only).</summary>
    public void PostMessage(string name, object? data = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        Command(BrowserProtocol.PostToPage, m => (m["name"], m["data"]) = (name, data is null ? JValue.CreateNull() : JToken.FromObject(data)));
    }

    /// <summary>
    /// Answers the page's <c>gameApi.request(name, data)</c> (needs <see cref="BrowserCapabilities.Requests"/>). The
    /// result is sent to the page as JSON; an exception rejects the page's promise with its message.
    /// </summary>
    public void RegisterRequestHandler(string name, Func<JToken, Task<object?>> handler)
    {
        ArgumentNullException.ThrowIfNull(name);
        _requestHandlers[name] = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public void RegisterRequestHandler(string name, Func<JToken, object?> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        RegisterRequestHandler(name, data => Task.FromResult(handler(data)));
    }

    public bool UnregisterRequestHandler(string name) => _requestHandlers.Remove(name);

    /// <summary>Moves the page's pointer to a pixel of the page (0,0 = top left).</summary>
    public void SendMouseMove(int x, int y, BrowserModifiers modifiers = BrowserModifiers.None) =>
        Command(BrowserProtocol.MouseMove, m => (m["x"], m["y"], m["modifiers"]) = (x, y, (int)modifiers));

    /// <summary>The pointer left the page.</summary>
    public void SendMouseLeave() => Command(BrowserProtocol.MouseMove, m => (m["x"], m["y"], m["leave"]) = (-1, -1, true));

    public void SendMouseButton(int x, int y, BrowserMouseButton button, bool released, int clickCount = 1, BrowserModifiers modifiers = BrowserModifiers.None) =>
        Command(BrowserProtocol.MouseButton, m => (m["x"], m["y"], m["button"], m["up"], m["clicks"], m["modifiers"]) = (x, y, (int)button, released, clickCount, (int)modifiers));

    /// <summary>Scrolls at a pixel; deltas in pixels (a wheel notch is 120).</summary>
    public void SendMouseWheel(int x, int y, int deltaX, int deltaY) =>
        Command(BrowserProtocol.MouseWheel, m => (m["x"], m["y"], m["dx"], m["dy"]) = (x, y, deltaX, deltaY));

    /// <summary>A key went down or up: <paramref name="windowsKeyCode"/> is a Windows virtual-key code (see <see cref="BrowserInput"/>).</summary>
    public void SendKey(int windowsKeyCode, bool released, BrowserModifiers modifiers = BrowserModifiers.None) =>
        Command(BrowserProtocol.Key, m => (m["kind"], m["code"], m["modifiers"]) = (released ? "up" : "down", windowsKeyCode, (int)modifiers));

    /// <summary>Types text (one character event per UTF-16 unit).</summary>
    public void SendText(string text, BrowserModifiers modifiers = BrowserModifiers.None)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var c in text)
        {
            Command(BrowserProtocol.Key, m => (m["kind"], m["code"], m["modifiers"]) = ("char", (int)c, (int)modifiers));
        }
    }

    /// <summary>Kills the page's renderer process (tests the crash recovery: <see cref="Crashed"/>, then <see cref="Reload"/>).</summary>
    public void SimulateRendererCrash() => Command(BrowserProtocol.CrashRenderer);

    public void Dispose()
    {
        if (State == BrowserState.Closed)
        {
            return;
        }

        State = BrowserState.Closed;
        _service.Close(this);
        FailPending("The browser was closed.");
        CloseFrame();
    }

    /// <summary>Pixels painted since the last call, under the shared-memory lock (never waits for it).</summary>
    internal bool TryTakeFrame(ref byte[] buffer, out (int X, int Y, int Width, int Height) dirty)
    {
        dirty = default;
        lock (_frameLock)
        {
            if (_pendingSurface is { } surface && OperatingSystem.IsWindows())
            {
                var opened = SharedFrame.Open(surface.Name, surface.Width, surface.Height);
                if (opened is null)
                {
                    return false; // the host replaced it again meanwhile; a newer surface message follows
                }

                _frame?.Dispose();
                _frame = opened;
                _pendingSurface = null;
                (TextureWidth, TextureHeight) = (surface.Width, surface.Height);
                SurfaceGeneration++;
            }

            if (_frame is null || !OperatingSystem.IsWindows() || !_frame.TryTake(ref buffer, out dirty, out var painted))
            {
                return false;
            }

            FramesPainted = painted;
            return true;
        }
    }

    /// <summary>The host made a new shared frame (first paint, resize). Reader thread.</summary>
    internal void OnSurface(string name, int width, int height)
    {
        lock (_frameLock)
        {
            _pendingSurface = (name, width, height);
        }
    }

    internal void CloseFrame()
    {
        lock (_frameLock)
        {
            if (OperatingSystem.IsWindows())
            {
                _frame?.Dispose();
            }

            _frame = null;
            _pendingSurface = null;
        }
    }

    internal void OnCreating()
    {
        State = BrowserState.Creating;
        Error = null;
    }

    internal void OnHostLost(string reason, bool failed)
    {
        if (State == BrowserState.Closed)
        {
            return;
        }

        CloseFrame();
        FailPending(reason);
        State = failed ? BrowserState.Failed : BrowserState.Crashed;
        Error = reason;
        Crashed?.Invoke(reason);
    }

    /// <summary>A message from the host for this browser. Game thread.</summary>
    internal void Dispatch(JObject message)
    {
        if (State == BrowserState.Closed)
        {
            return;
        }

        switch (message.Value<string>("type"))
        {
            case BrowserProtocol.Created:
                State = BrowserState.Ready;
                break;
            case BrowserProtocol.LoadStarted:
                IsLoading = true;
                LoadStarted?.Invoke(message.Value<string>("url") ?? "");
                break;
            case BrowserProtocol.LoadCompleted:
                LoadCompleted?.Invoke(message.Value<string>("url") ?? "", message.Value<int?>("status") ?? 0);
                break;
            case BrowserProtocol.LoadError:
                LoadFailed?.Invoke(message.Value<string>("url") ?? "", message.Value<int?>("code") ?? 0, message.Value<string>("error") ?? "");
                break;
            case BrowserProtocol.Title:
                Title = message.Value<string>("title") ?? "";
                TitleChanged?.Invoke(Title);
                break;
            case BrowserProtocol.Address:
                Url = message.Value<string>("url") ?? Url;
                AddressChanged?.Invoke(Url);
                break;
            case BrowserProtocol.NavigationState:
                (CanGoBack, CanGoForward, IsLoading) = (message.Value<bool>("canGoBack"), message.Value<bool>("canGoForward"), message.Value<bool>("loading"));
                break;
            case BrowserProtocol.Console:
                ConsoleMessage?.Invoke(new BrowserConsoleMessage(message.Value<string>("level") ?? "Info", message.Value<string>("message") ?? "", message.Value<string>("source") ?? "", message.Value<int?>("line") ?? 0));
                break;
            case BrowserProtocol.Log:
                if (BridgeAllows(message, BrowserCapabilities.Messages))
                {
                    ConsoleMessage?.Invoke(new BrowserConsoleMessage("Bridge", message.Value<string>("text") ?? "", message.Value<string>("origin") ?? "", 0));
                }

                break;
            case BrowserProtocol.Message:
                if (BridgeAllows(message, BrowserCapabilities.Messages))
                {
                    MessageReceived?.Invoke(new BrowserMessage(message.Value<string>("name") ?? "", message["data"] ?? JValue.CreateNull(), message.Value<string>("origin") ?? ""));
                }

                break;
            case BrowserProtocol.Request:
                _ = AnswerRequestAsync(message);
                break;
            case BrowserProtocol.EvaluateResult:
                if (_evaluations.Remove(message.Value<int>("id"), out var evaluation))
                {
                    if (message.Value<bool>("ok"))
                    {
                        evaluation.TrySetResult(message["value"]);
                    }
                    else
                    {
                        evaluation.TrySetException(new BrowserScriptException(message.Value<string>("error") ?? "The script failed."));
                    }
                }

                break;
            case BrowserProtocol.Blocked:
                Blocked?.Invoke(message.Value<string>("what") ?? "", message.Value<string>("url") ?? "");
                break;
            case BrowserProtocol.Crashed:
                State = BrowserState.Crashed;
                Error = $"The page's renderer stopped ({message.Value<string>("status")}: {message.Value<string>("error")}).";
                FailPending(Error);
                Crashed?.Invoke(Error);
                break;
            case BrowserProtocol.Failed:
                Error = $"{message.Value<string>("command")}: {message.Value<string>("error")}";
                _service.LogCommandFailed(Id, Error);
                break;
            default:
                break;
        }
    }

    /// <summary>The host checked the origin already; the game checks again, so a confused host cannot widen the bridge.</summary>
    private bool BridgeAllows(JObject message, BrowserCapabilities capability) =>
        (Policy.Capabilities & capability) != 0 && Policy.IsBridgeAllowed(message.Value<string>("origin") ?? "");

    private async Task AnswerRequestAsync(JObject message)
    {
        var id = message.Value<int>("id");
        var name = message.Value<string>("name") ?? "";
        (bool Ok, JToken Value) answer;
        if (!BridgeAllows(message, BrowserCapabilities.Requests))
        {
            answer = (false, "Requests are not allowed for this page.");
        }
        else if (!_requestHandlers.TryGetValue(name, out var handler))
        {
            answer = (false, $"No handler for request '{name}'.");
        }
        else
        {
            try
            {
                var result = await handler(message["data"] ?? JValue.CreateNull()).ConfigureAwait(true);
                answer = (true, result is null ? JValue.CreateNull() : JToken.FromObject(result));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The game's handler failed: the page's promise is rejected with the message, the game logs it.
                _service.LogRequestFailed(Id, name, ex);
                answer = (false, ex.Message);
            }
        }

        Command(BrowserProtocol.RequestResult, m => (m["id"], m["ok"], m["value"]) = (id, answer.Ok, answer.Value));
    }

    private void FailPending(string reason)
    {
        foreach (var pending in _evaluations.Values)
        {
            pending.TrySetException(new BrowserScriptException(reason));
        }

        _evaluations.Clear();
    }

    private void Command(string type, Action<JObject>? fill = null)
    {
        if (State is BrowserState.Closed or BrowserState.Failed)
        {
            return;
        }

        var message = BrowserProtocol.New(type, Id);
        fill?.Invoke(message);
        _service.Send(message);
    }
}

[Flags]
public enum BrowserModifiers
{
    None = 0,
    CapsLock = 1,
    Shift = 2,
    Control = 4,
    Alt = 8,
    LeftMouse = 16,
    MiddleMouse = 32,
    RightMouse = 64,
}

public enum BrowserMouseButton
{
    Left,
    Middle,
    Right,
}
