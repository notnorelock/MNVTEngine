# Embedded browser

Web pages (Chromium 156, through CEF and CefSharp) rendered into textures and shown on sprites and meshes: in-game
menus and HUDs made with HTML, terminals and screens in 3D worlds, help pages, store pages. Pages talk to C# through an
explicit, capability-checked bridge. Windows x64 only. How it works and why: [Embedded browser architecture](../architecture/embedded-browser.md).

## Modules

| Module | Profiles | Brings | Included when |
|---|---|---|---|
| `browser` | 2D, 3D | `MNVTEngine.Browser` (C# API), `MNVTBrowserHost.exe`, CefSharp and CEF (about 360 MB) | a surface below is used, or C# uses `MNVTEngine.Browser` types |
| `browser.2d` | 2D | `BrowserSurface2D` | a scene or prefab has the component |
| `browser.3d` | 3D | `BrowserSurface3D` | a scene or prefab has the component |

A game without browser content does not ship any of it; `build-report.json` has a `browser` section when it does.

## A page on a sprite or a mesh

1. Put the page's files in `Assets/Web` (`index.html`, `.css`, `.js`, images). They are served at `mnvt://game/`:
   `Assets/Web/ui/menu.html` is `mnvt://game/ui/menu.html`. The whole folder ships with the game (pages load files by
   URL, which the build cannot follow).
2. 2D: add **BrowserSurface2D** to a GameObject (it adds a SpriteRenderer; set the sprite's **Size** in world units
   to the page's aspect). 3D: add **BrowserSurface3D** next to a **MeshRenderer** (a Quad, or any UV-mapped mesh).
3. Set **Url**, **Page Width/Height** (pixels), **Profile** and **Capabilities**.

| Field | Meaning |
|---|---|
| Url | The first page: `mnvt://game/...`, `https://...` (profile permitting) or `about:blank` |
| Page Width, Page Height | Page and texture size in pixels (up to 8192) |
| Transparent | The page's transparent areas stay transparent (overlays); otherwise the background is white |
| Frame Rate | Chromium's paint rate, 1 to 120 (lower costs less) |
| Profile, Allowed Domains, Capabilities | [Security](#security) |
| Muted | Page audio off (Chromium stops producing it) |
| Volume, Audio Group | Page audio level and its `AudioMixer` group (default `Browser`); see [Audio](#audio) |
| Interactive | Mouse and keyboard reach the page |
| Emissive, Brightness (3D) | Shown as a display that lighting does not darken (on by default) |
| Double Sided (3D) | Pointer hits on the back of the triangles too |

The page appears after its first paint. While the mouse is over the page, mouse input goes to the page, not to gameplay
scripts; a click on the page gives it the keyboard until a click elsewhere or Escape. Built-in primitives have their UV
origin at the bottom left and models (glTF) at the top left: `BrowserSurface3D` uploads the page the right way up for
either, and maps the pointer through the mesh's UVs, so curved screens and model displays work.

The Inspector warns when the profile does not allow the URL or the `mnvt://` file does not exist, and in Play mode shows
the page's state, texture, paints and uploads, with **Reload** and **Crash renderer** (recovery test) buttons.

## C# API

```csharp
using MNVTEngine.Browser;

public sealed class Shop : Component
{
    private BrowserInstance? _page;

    public override void Start()
    {
        _page = GetComponent<BrowserSurface2D>()!.Browser; // or open one yourself:
        // _page = GameObject.Scene.GetService<BrowserService>()!.Create(new BrowserOptions { Url = "mnvt://game/shop.html", Width = 800, Height = 600, Profile = BrowserSecurityProfile.TrustedLocal, Capabilities = BrowserCapabilities.Messages | BrowserCapabilities.Requests });
        _page.MessageReceived += message =>
        {
            if (message.Name == "buy")
            {
                var item = message.DataAs<string>();
                _page.PostMessage("bought", new { item, coins = 90 });
            }
        };
        _page.RegisterRequestHandler("inventory", _ => new[] { "sword", "shield" });
        _page.Crashed += _ => _page.Reload();
    }

    private async void ShowTitle()
    {
        var title = await _page!.ExecuteJavaScriptAsync("document.title"); // JToken; throws BrowserScriptException
        Log.Info($"Page: {title}");
    }
}
```

`BrowserInstance` (one per browser; dispose browsers you create yourself, surfaces dispose theirs):

| Member | Does |
|---|---|
| `Navigate(url)`, `LoadHtml(html)`, `Reload()`, `Stop()`, `GoBack()`, `GoForward()` | Navigation; `Reload` after a crash recreates the page (and restarts the browser host if it stopped) |
| `ExecuteJavaScriptAsync(script)` | The last expression's value as JSON; completes on the game thread |
| `PostMessage(name, data)` | Calls the page's `gameApi.onMessage` listeners |
| `RegisterRequestHandler(name, handler)` | Answers `gameApi.request(name, data)`; sync or `Task` handlers; an exception rejects the page's promise |
| `Resize(w, h)`, `SetVisible`, `SetFocus`, `SetMuted`, `SetFrameRate`, `SetZoomLevel` | Page settings; hidden pages stop painting |
| `SendMouseMove/Button/Wheel`, `SendKey`, `SendText` | Input in page pixels (surfaces do this for you) |
| `State`, `Error`, `Url`, `Title`, `IsLoading`, `CanGoBack`, `TextureId`, `TextureWidth/Height`, `FramesPainted`, `FramesUploaded` | State |
| `LoadStarted`, `LoadCompleted`, `LoadFailed`, `TitleChanged`, `AddressChanged`, `ConsoleMessage`, `MessageReceived`, `Blocked`, `Crashed` | Events, raised on the game thread |

`new AssetRef(browser.TextureId)` shows a browser on any sprite or material (`SetMaterialTexture`). Events and task
completions are delivered by the browser system each frame, after the components' `Update`.

## JavaScript bridge

Pages the policy trusts get `window.gameApi` before their own scripts run (top frame only):

```js
gameApi.sendMessage('buy', 'sword');                 // MessageReceived in C# (capability Messages)
gameApi.log('menu opened');                          // ConsoleMessage, level "Bridge" (Messages)
const items = await gameApi.request('inventory');    // a registered handler (capability Requests)
gameApi.onMessage((name, data) => { ... });          // BrowserInstance.PostMessage
window.addEventListener('gameapiready', ...);        // fired once gameApi exists
```

Data is JSON (Newtonsoft on the C# side, 16 MB per message). There is no generic call into C#: only the message names
and request handlers the game registers exist, and pages without the bridge cannot reach them (each message carries a
per-browser secret only the injected bridge knows; the host and the game both check the page's origin again).

## Security

| Profile | Loads | Bridge |
|---|---|---|
| `TrustedLocal` | `mnvt://` (game files, `LoadHtml`), `data:`, `about:blank`; http/https only on **Allowed Domains**. Scripts, images and requests only from those too | with **Capabilities**, for `mnvt://` pages and allowlisted hosts |
| `RemoteWeb` | http/https (any site, or only **Allowed Domains** when set), `data:` | off; `RemoteBridge` capability gives it to remote pages |
| `Restricted` | https on **Allowed Domains** and `about:blank` only | never |

Always refused: popups and new windows (reported as `Blocked("popup", url)`; navigate yourself if wanted), downloads,
permission prompts (camera, microphone, location, notifications), `file://`, `javascript:` and other application
protocols, `alert`/`confirm`/`prompt` dialogs (suppressed, logged as console warnings). `mnvt://game/` paths never
leave `Assets/Web`: `..`, encoded dots or separators, backslashes and drive letters are rejected. Give remote content
the `RemoteWeb` or `Restricted` profile and no capabilities unless you control the site.

## Audio

Page audio plays through the engine's audio, not straight to the speakers: Chromium hands the decoded sound (48 kHz
stereo) to the browser host, which writes it into a shared-memory ring per browser; each frame the game moves it into a
streaming voice of the scene's audio output. Its volume is the browser's **Volume** times its **Audio Group** in the
scene's `AudioMixer` (default `Browser`: add a `Browser` group to the mixer, under Master, to control all page audio;
an unknown group plays through Master). Pages may play without a click first.

| Control | Effect |
|---|---|
| **Volume**, `BrowserInstance.Volume` | This browser's level, before its group |
| **Audio Group**, `BrowserInstance.AudioGroup` | The mixer group (`AudioMixer.SetVolume("Browser", 0.5f)`, `SetMuted`) |
| **Muted**, `SetMuted` | Chromium stops producing audio for the browser (saves the work) |

Page audio is not positional (no 3D attenuation). When the game runs slower than the page, more than a quarter second
of queued audio is dropped so sound stays in sync with the page. Requires the `audio` module (the `browser` module
brings it).

## Diagnostics

| Metric (overlay section `browser`) | |
|---|---|
| Browsers, paints, uploads, upload size, upload time, texture memory | measured each frame |
| Browser host memory | working set of `MNVTBrowserHost.exe`; Chromium's renderer and GPU processes are not counted |
| Browser GPU zero-copy | Unsupported (pages reach the GPU through CPU buffers) |

Measured (RTX 2060 SUPER machine, pages animating at 60 fps, game pumping at 60 Hz, 5 s): 8 browsers of 256x144 got
about 54 frames per second each; 4 of 1280x720 about 49 each (105 MB/s of dirty pixels, 0.69 ms per game frame to copy
them out of shared memory); one 1920x1080 page 52. Chromium's processes (the host and its renderer, GPU and utility
processes) used 630 to 960 MB together. `Stress_EightAnimatedBrowsers_AllKeepPainting` in `MNVTEngine.Browser.Tests`
checks that every browser keeps delivering frames.

Console commands: `browsers` (host state, every browser's page, texture, paints and uploads) and
`browser.reload <id>`. Page console messages are logged at Debug level; `mnvt://` files that do not exist as warnings.

## Exports

The browser follows the game's layout:

```text
Game.exe, MNVTBrowserHost.exe (+ .dll, .runtimeconfig.json)   next to each other
Dependencies/Managed          CefSharp.dll, CefSharp.Core.dll, CefSharp.OffScreen.dll (with the game's assemblies)
Dependencies/Native/Chromium  libcef.dll, chrome_elf.dll, paks, icudtl.dat, v8_context_snapshot.bin, locales/en-US.pak,
                              CefSharp's C++/CLI assemblies with Ijwhost.dll, THIRD-PARTY-NOTICES.md, licenses/
Dependencies/Runtime          the game's private .NET runtime, which the host runs on
```

CEF has a folder of its own because the game loads every library directly in `Dependencies/Native` at startup, and
CefSharp's C++/CLI assemblies need `Ijwhost.dll` next to them. The host is framework-dependent: a JIT game's runtime
serves both; an AOT game, which has no runtime, gets one in `Dependencies/Runtime` for the host only (CefSharp's C++/CLI
needs CoreCLR). Framework pruning keeps what the host and CefSharp reference. Left out: CEF's translations other than
`en-US` (51 MB), and its `vulkan-1.dll` and SwiftShader (software WebGL), so CEF's Vulkan loader never stands in for the
system's. Measured for `samples/Browser2D` (release, win-x64): whole game 445 MB with the JIT backend, 460 MB with AOT;
`Dependencies/Native/Chromium` 350 MB (`libcef.dll` alone 281 MB). `MNVB406` when the host cannot be published or the
package lacks it. Development runs (editor, Player, tests) use the engine's build output of
`src/Engine/MNVTEngine.Browser.Host`, or `MNVT_BROWSER_HOST`.

## Limits

- Windows x64 only (CEF and CefSharp have no other packages the engine uses).
- No software WebGL fallback (SwiftShader is not shipped): WebGL needs Chromium's GPU process.
- `<select>` dropdowns and other popup widgets are not drawn (CEF paints them separately).
- No IME composition; a held key does not repeat.
- Pages with video use open codecs only (VP8/VP9/AV1, Opus/Vorbis).
- Each page update is copied once in memory and uploaded to the GPU; GPU zero-copy (shared D3D11 textures) is not
  implemented. Many large pages at 60 fps cost CPU time: lower **Frame Rate** or hide browsers not on screen.
- Overlapping interactive surfaces: the first one under the mouse takes the input.

Samples: `samples/Browser2D` (a page and a transparent overlay on sprites, C# and JavaScript talking both ways; F5
reload, F6 crash the renderer, F7 run JavaScript) and `samples/Browser3D` (a monitor and a spinning cube with pages,
picking through UVs).
