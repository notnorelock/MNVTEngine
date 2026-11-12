using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.BrowserHosting;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Browser.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BrowserHostTestGroup
{
    public const string Name = "Browser host";
}

/// <summary>
/// End to end through MNVTBrowserHost.exe and Chromium: pixels, JavaScript, the bridge, input, security and crash
/// recovery. Skipped when the host is not built (it is built with this project).
/// </summary>
[Collection(BrowserHostTestGroup.Name)]
public sealed class BrowserHostTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly BrowserService _service;
    private byte[] _pixels = new byte[64 * 1024];

    public BrowserHostTests()
    {
        if (!OperatingSystem.IsWindows() || BrowserService.FindHost() is null)
        {
            Assert.Skip("MNVTBrowserHost.exe is not built (src/Engine/MNVTEngine.Browser.Host).");
        }

        _service = new BrowserService(NullLogger.Instance, new BrowserServiceOptions { Gpu = false });
    }

    public void Dispose() => _service?.Dispose();

    [Fact]
    public void Page_IsPaintedIntoSharedMemory()
    {
        using var browser = Open("<body style='margin:0;background:rgb(255,0,0)'></body>", width: 64, height: 32);
        Assert.True(Pump(() => browser.TryTakeFrame(ref _pixels, out var dirty) && dirty.Width > 0 && _pixels[0] == 255 && _pixels[1] == 0), "no red frame");
        Assert.Equal((64, 32), (browser.TextureWidth, browser.TextureHeight));
        Assert.Equal(255, _pixels[3]); // opaque
    }

    [Fact]
    public void TransparentPage_KeepsAlpha()
    {
        using var browser = Open("<body style='margin:0;background:transparent'></body>", width: 16, height: 16, transparent: true);
        Assert.True(Pump(() => browser.TryTakeFrame(ref _pixels, out _)));
        Assert.Equal(0, _pixels[3]);
    }

    [Fact]
    public void Resize_RecreatesTheSurface()
    {
        using var browser = Open("<body style='background:#08f'></body>", width: 40, height: 20);
        Assert.True(Pump(() => browser.TryTakeFrame(ref _pixels, out _)));
        var generation = browser.SurfaceGeneration;
        browser.Resize(100, 50);
        Assert.True(Pump(() => browser.TryTakeFrame(ref _pixels, out _) && browser.SurfaceGeneration != generation), "no frame after resize");
        Assert.Equal((100, 50), (browser.TextureWidth, browser.TextureHeight));
    }

    [Fact]
    public void JavaScript_ReturnsValues_AndReportsErrors()
    {
        using var browser = Open("<title>Hi</title><body></body>");
        Assert.Equal(3, Await(browser.ExecuteJavaScriptAsync("1 + 2"))!.Value<int>());
        Assert.Equal("Hi", Await(browser.ExecuteJavaScriptAsync("document.title"))!.Value<string>());
        var failed = browser.ExecuteJavaScriptAsync("throw new Error('nope')");
        Pump(() => failed.IsCompleted);
        Assert.IsType<BrowserScriptException>(failed.Exception?.InnerException);
    }

    [Fact]
    public void Bridge_DeliversMessagesRequestsAndReplies()
    {
        const string Page = """
            <script>
              gameApi.onMessage((name, data) => { if (name === 'ping') gameApi.sendMessage('pong', data.n + 1); });
              gameApi.sendMessage('hello', { a: 1 });
              gameApi.log('logged');
              gameApi.request('add', { x: 2, y: 3 }).then(v => gameApi.sendMessage('sum', v));
              gameApi.request('missing').catch(e => gameApi.sendMessage('rejected', e.message));
            </script>
            """;
        var messages = new List<BrowserMessage>();
        var logs = new List<string>();
        using var browser = Open(Page, capabilities: BrowserCapabilities.Messages | BrowserCapabilities.Requests, register: b =>
        {
            b.RegisterRequestHandler("add", d => d.Value<int>("x") + d.Value<int>("y"));
            b.MessageReceived += messages.Add;
            b.ConsoleMessage += m => logs.Add(m.Message);
        });
        // CefSharp raises page messages on the thread pool: their order is not guaranteed.
        Assert.True(Pump(() => messages.Any(m => m.Name == "sum") && messages.Any(m => m.Name == "rejected") && messages.Any(m => m.Name == "hello") && logs.Contains("logged")), Describe(messages));
        Assert.Equal(1, messages.Single(m => m.Name == "hello").Data.Value<int>("a"));
        Assert.Equal(5, messages.Single(m => m.Name == "sum").DataAs<int>());
        Assert.Contains("missing", messages.Single(m => m.Name == "rejected").DataAs<string>(), StringComparison.Ordinal);
        Assert.Contains("logged", logs);
        Assert.All(messages, m => Assert.StartsWith("mnvt://inline/", m.Origin, StringComparison.Ordinal));

        browser.PostMessage("ping", new { n = 41 });
        Assert.True(Pump(() => messages.Any(m => m.Name == "pong")));
        Assert.Equal(42, messages.Single(m => m.Name == "pong").DataAs<int>());
    }

    [Theory]
    [InlineData(BrowserSecurityProfile.TrustedLocal, BrowserCapabilities.None)]
    [InlineData(BrowserSecurityProfile.RemoteWeb, BrowserCapabilities.Messages)] // inline HTML of a remote profile is a data: URL: no bridge
    [InlineData(BrowserSecurityProfile.Restricted, BrowserCapabilities.Messages | BrowserCapabilities.Requests)]
    public void Bridge_IsAbsentWhenNotGranted(BrowserSecurityProfile profile, BrowserCapabilities capabilities)
    {
        using var browser = Open(profile == BrowserSecurityProfile.Restricted ? null : "<body>x</body>", profile: profile, capabilities: capabilities);
        Assert.Equal("undefined", Await(browser.ExecuteJavaScriptAsync("typeof window.gameApi"))!.Value<string>());
    }

    [Fact]
    public void Restricted_BlocksNavigationOutsideTheAllowlist()
    {
        using var browser = _service.Create(new BrowserOptions { Url = "about:blank", Width = 32, Height = 32, Profile = BrowserSecurityProfile.Restricted, AllowedDomains = ["example.com"] });
        var blocked = new List<(string What, string Url)>();
        browser.Blocked += (what, url) => blocked.Add((what, url));
        Assert.True(Pump(() => browser.State == BrowserState.Ready));
        browser.Navigate("https://not-allowed.invalid/");
        browser.Navigate("file:///C:/Windows/win.ini");
        Assert.True(Pump(() => blocked.Count >= 2), Describe(blocked));
        Assert.All(blocked, b => Assert.Equal("navigation", b.What));
    }

    [Fact]
    public void Popups_AreBlocked()
    {
        // A popup opened from a click (Chromium's own blocker stops the ones without a user gesture).
        using var browser = Open("<button style='position:absolute;inset:0' onclick=\"window.open('mnvt://game/x.html')\">x</button>", profile: BrowserSecurityProfile.TrustedLocal);
        var blocked = new List<string>();
        browser.Blocked += (what, _) => blocked.Add(what);
        browser.SendMouseButton(10, 10, BrowserMouseButton.Left, released: false);
        browser.SendMouseButton(10, 10, BrowserMouseButton.Left, released: true);
        Assert.True(Pump(() => blocked.Contains("popup")), Describe(blocked));
    }

    [Fact]
    public void Mouse_And_Keyboard_ReachThePage()
    {
        const string Page = """
            <body style="margin:0">
              <button id="b" style="position:absolute;left:0;top:0;width:100px;height:50px" onclick="gameApi.sendMessage('clicked', 1)">B</button>
              <input id="t" style="position:absolute;left:0;top:60px;width:100px">
            </body>
            """;
        using var browser = Open(Page, width: 200, height: 100, capabilities: BrowserCapabilities.Messages);
        var clicks = 0;
        browser.MessageReceived += m => clicks += m.Name == "clicked" ? 1 : 0;
        browser.SendMouseMove(50, 25);
        browser.SendMouseButton(50, 25, BrowserMouseButton.Left, released: false);
        browser.SendMouseButton(50, 25, BrowserMouseButton.Left, released: true);
        Assert.True(Pump(() => clicks == 1), "the click did not reach the button");

        browser.SetFocus(true);
        browser.SendMouseButton(50, 70, BrowserMouseButton.Left, released: false);
        browser.SendMouseButton(50, 70, BrowserMouseButton.Left, released: true);
        browser.SendText("abc");
        browser.SendKey(BrowserInput.VirtualKey(Key.Backspace), released: false);
        browser.SendKey(BrowserInput.VirtualKey(Key.Backspace), released: true);
        Assert.True(Pump(() => Await(browser.ExecuteJavaScriptAsync("document.getElementById('t').value"))?.Value<string>() == "ab"), "typed text did not arrive");
    }

    [Fact]
    public void GameScheme_ServesWebAssets()
    {
        var asked = new List<string>();
        _service.ResourceReader = path =>
        {
            asked.Add(path);
            return path == "Web/index.html" ? Encoding.UTF8.GetBytes("<title>From assets</title><script src='app.js'></script>") : path == "Web/app.js" ? "document.title += ' + js';"u8.ToArray() : null;
        };
        using var browser = _service.Create(new BrowserOptions { Url = "mnvt://game/index.html", Width = 32, Height = 32, Profile = BrowserSecurityProfile.TrustedLocal });
        var loaded = new List<(string Url, int Status)>();
        browser.LoadCompleted += (url, status) => loaded.Add((url, status));
        Assert.True(Pump(() => loaded.Any(l => l.Url.StartsWith("mnvt://game/", StringComparison.Ordinal))), Describe(loaded));
        Assert.Equal(200, loaded.Last().Status);
        Assert.Equal("From assets + js", Await(browser.ExecuteJavaScriptAsync("document.title"))!.Value<string>());
        Assert.All(asked, p => Assert.StartsWith("Web/", p, StringComparison.Ordinal));
    }

    [Fact]
    public void GameScheme_IsNotServedToRemoteProfiles()
    {
        _service.ResourceReader = _ => "<title>secret</title>"u8.ToArray();
        using var browser = Open("<title>remote</title>", profile: BrowserSecurityProfile.RemoteWeb);
        var blocked = new List<string>();
        browser.Blocked += (_, url) => blocked.Add(url);
        browser.Navigate("mnvt://game/index.html");
        Assert.True(Pump(() => blocked.Count > 0));
        Assert.Equal("remote", Await(browser.ExecuteJavaScriptAsync("document.title"))?.Value<string>());
    }

    [Fact]
    public void RendererCrash_IsReported_AndReloadRecovers()
    {
        using var browser = Open("<title>alive</title>");
        var crashes = new List<string>();
        browser.Crashed += crashes.Add;
        browser.SimulateRendererCrash();
        Assert.True(Pump(() => crashes.Count > 0), "no crash event");
        Assert.Equal(BrowserState.Crashed, browser.State);
        var reloaded = false;
        browser.LoadCompleted += (_, _) => reloaded = true;
        browser.Reload();
        Assert.True(Pump(() => reloaded), "the page did not load again");
        Assert.Equal(BrowserState.Ready, browser.State);
        Assert.Equal("alive", Await(browser.ExecuteJavaScriptAsync("document.title"))?.Value<string>());
    }

    [Fact]
    public void HostExit_FailsBrowsers_AndRecreateRestartsTheHost()
    {
        using var browser = Open("<title>one</title>");
        var firstHost = _service.HostProcessId!.Value;
        using (var host = Process.GetProcessById(firstHost))
        {
            host.Kill(entireProcessTree: true);
        }

        Assert.True(Pump(() => browser.State == BrowserState.Crashed), "the host's exit was not noticed");
        Assert.Equal(BrowserHostState.Exited, _service.HostState);
        browser.Reload();
        Assert.True(Pump(() => _service.HostState == BrowserHostState.Running && browser.State == BrowserState.Ready));
        Assert.NotEqual(firstHost, _service.HostProcessId);
    }

    [Fact]
    public void PageAudio_ReachesTheGamesVoice_AtBrowserTimesGroupVolume()
    {
        const string Page = "<script>const c = new AudioContext(); const o = c.createOscillator(); o.connect(c.destination); o.start();</script>";
        var device = new Audio.NullAudioDevice();
        using var browser = Open(Page);
        browser.Volume = 0.5f;
        Assert.True(Pump(() =>
        {
            browser.PumpAudio(device, groupVolume: 0.8f);
            return browser.QueuedAudioSeconds > 0.05f;
        }), "no page audio arrived");
        var voice = Assert.Single(device.StreamingVoices);
        Assert.Equal((2, 48000), (voice.Channels, voice.SampleRate));
        Assert.Equal(0.4f, voice.Volume, 3);

        browser.Dispose();
        Assert.Empty(device.StreamingVoices); // closing the browser releases its voice
    }

    [Fact]
    public void Stress_EightAnimatedBrowsers_AllKeepPainting()
    {
        const string Page = "<style>@keyframes s{to{transform:translateX(200px)}}div{width:50px;height:50px;background:#f80;animation:s .5s linear infinite alternate}</style><div></div>";
        var browsers = Enumerable.Range(0, 8).Select(_ => Open(Page, width: 256, height: 256)).ToList();
        try
        {
            foreach (var browser in browsers)
            {
                browser.SetFrameRate(60);
            }

            var taken = new int[browsers.Count];
            long bytes = 0;
            var watch = Stopwatch.StartNew();
            Pump(() =>
            {
                for (var i = 0; i < browsers.Count; i++)
                {
                    if (browsers[i].TryTakeFrame(ref _pixels, out var dirty))
                    {
                        taken[i]++;
                        bytes += (long)dirty.Width * dirty.Height * 4;
                    }
                }

                return watch.Elapsed > TimeSpan.FromSeconds(3);
            });

            TestContext.Current.TestOutputHelper?.WriteLine($"3 s, 8 browsers 256x256 at 60 fps: {string.Join(", ", taken)} frames taken, {bytes / 1024 / 1024} MB of dirty pixels, host working set {Process.GetProcessById(_service.HostProcessId!.Value).WorkingSet64 / 1024 / 1024} MB");
            Assert.All(taken, frames => Assert.True(frames >= 30, $"a browser delivered only {frames} frames in 3 s"));
        }
        finally
        {
            browsers.ForEach(b => b.Dispose());
        }
    }

    private BrowserInstance Open(string? html, int width = 64, int height = 64, bool transparent = false, BrowserSecurityProfile profile = BrowserSecurityProfile.TrustedLocal,
        BrowserCapabilities capabilities = BrowserCapabilities.None, Action<BrowserInstance>? register = null)
    {
        var browser = _service.Create(new BrowserOptions { Html = html, Url = "about:blank", Width = width, Height = height, Transparent = transparent, Profile = profile, Capabilities = capabilities });
        register?.Invoke(browser);
        var loaded = false;
        browser.LoadCompleted += (_, _) => loaded = true;
        Assert.True(Pump(() => loaded), $"the page did not load (state {browser.State}, {browser.Error}, host {_service.HostState}: {_service.HostError})");
        return browser;
    }

    /// <summary>Runs the game side's frame loop until the condition holds.</summary>
    private bool Pump(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < Timeout)
        {
            _service.Update();
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return false;
    }

    private JToken? Await(Task<JToken?> task)
    {
        Assert.True(Pump(() => task.IsCompleted), "JavaScript did not answer");
        return task.GetAwaiter().GetResult();
    }

    private static string Describe<T>(IEnumerable<T> items) => string.Join("; ", items);
}
