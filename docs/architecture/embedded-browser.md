# Embedded browser (Chromium via CefSharp)

Web pages rendered into engine textures and shown on 2D sprites and 3D meshes, controlled from C#. This page records the
feasibility research (Phase 0) and the architecture chosen; the user-facing guide is [Browser](../engine/browser.md).

## Research (2026-11-12)

| Question | Finding |
|---|---|
| Current CefSharp | `CefSharp.OffScreen.NETCore` 156.0.150 (CEF 156, Chromium 156), with `CefSharp.Common.NETCore` and `chromiumembeddedframework.runtime` 156.0.15 |
| Target frameworks | `net6.0-windows7.0` (OffScreen) and `net6.0` (Common). A `net11.0-windows` app references them; verified: initialize, load, paint, run JavaScript, screenshot, all on .NET 11 |
| Platforms | win-x64, win-x86, win-arm64. No Linux or macOS packages: the engine's exports are win-x64 today |
| Managed/native | `CefSharp.Core.Runtime.dll` is C++/CLI (mixed mode, needs `Ijwhost.dll`). It runs on CoreCLR on Windows only |
| Native AOT | Not possible: C++/CLI assemblies cannot be compiled by NativeAOT (or any IL-to-native compiler). CefSharp must run on CoreCLR |
| Processes | CEF is multi-process: browser, renderer, GPU and utility processes. The subprocess is `CefSharp.BrowserSubprocess.exe` (runtimeconfig `rollForward: Major`, so .NET 11 runs it), or the host executable itself with `CefSharp.BrowserSubprocess.SelfHost.Main` |
| Threading | `Cef.Initialize` and `Cef.Shutdown` must run on the same thread (verified: a shutdown from another thread throws). CEF runs its own UI thread (`MultiThreadedMessageLoop`); paint callbacks arrive on it |
| Off-screen rendering | `ChromiumWebBrowser.Paint` delivers a BGRA buffer (premultiplied alpha) and the dirty rectangle. CEF's documentation: the traditional OSR path does not support accelerated compositing. A shared-texture OSR path (`OnAcceleratedPaint`, D3D11 handles) exists for Chromium's GPU process, which would need D3D11/Vulkan interop (`VK_KHR_external_memory_win32`) to reach the engine |
| Measured | 800x600 page: CEF initialized and page loaded in 0.7 s; a page updating every 50 ms gave 41 paints in 2 s (about 6 MB of dirty pixels) |
| Size | `libcef.dll` alone is 294 MB (281 MB published); with `chrome_elf`, `icudtl.dat`, `v8_context_snapshot.bin`, paks, SwiftShader and 51 MB of locales a CEF deployment is about 410 MB. In an export (en-US only, no SwiftShader, sharing the game's runtime) CEF and CefSharp measured 350 MB ([Browser](../engine/browser.md#exports)) |
| Licensing | CEF and CefSharp: BSD 3-clause; CEF's README requires its LICENSE.txt with binary redistributions. Chromium's third-party licenses are built in (`about:credits`). CEF builds do not include proprietary codecs (H.264, AAC). Exports ship [THIRD-PARTY-NOTICES.md](../../src/Engine/MNVTEngine.Browser.Host/THIRD-PARTY-NOTICES.md) and both licenses in `Dependencies/Native/Chromium/licenses` |
| Pitfalls found | `EvaluateScriptAsync` on the browser refuses to run before the main frame has a script context (use the frame's); a DevTools `Page.addScriptToEvaluateOnNewDocument` script only runs after `Page.enable`; `window.CefSharp` appears after document-start scripts; `JavascriptMessageReceived` can arrive after the sending frame was disposed; a named pipe without buffers makes every write wait for the reader |

## Architecture: a browser host process

The browser runs out of the game's process, in `MNVTBrowserHost.exe` (`MNVTEngine.Browser.Host`), which hosts CEF with
CefSharp.OffScreen and is also CEF's subprocess executable (self-hosting). The game talks to it through
`MNVTEngine.Browser`, which knows nothing of CEF:

```text
game process                                         MNVTBrowserHost.exe (CoreCLR, CefSharp)
  BrowserInstance API  ── named pipe (JSON) ──►       commands: create, navigate, input, JavaScript, close
  events, responses    ◄─ named pipe (JSON) ──        events: load, title, console, messages, crashes
  BrowserTexture       ◄─ shared memory ─────         frames: RGBA pixels and the dirty rectangle, per browser
  mnvt:// asset reads  ── pipe request/response ──►   scheme handler for the game's web assets
                                                      └─ CEF renderer, GPU and utility processes (same exe)
```

Why out of process rather than CefSharp in the game:

- **AOT games keep the browser.** C++/CLI cannot be AOT-compiled, but the game only needs the pipe and shared memory,
  which NativeAOT supports. The host stays a CoreCLR process in every build (on the game's private runtime; AOT games
  get one for it).
- **Isolation.** A Chromium crash ends the host, not the game (`OnBrowserCrashed`); CEF's one-initialization-per-process
  and same-thread shutdown rules live in a process built for them.
- **No C++/CLI in the engine.** Engine assemblies stay pure managed; only the host references CefSharp.

The cost is one copy of each dirty region into shared memory (a memcpy on CEF's thread) and IPC latency for commands;
both are small next to Chromium's own painting.

### Frames: CPU pixel buffers

The first implementation uses CEF's traditional OSR path:

1. **Host, CEF paint thread:** converts the dirty rectangle from BGRA to RGBA (and to straight alpha for transparent
   browsers, which the engine's alpha blending expects) into the browser's shared memory, and grows the shared dirty
   rectangle, under a named mutex held for the copy only.
2. **Game, main thread, once per frame:** if the mutex is free (otherwise this frame is skipped: never a wait), copies
   the dirty rectangle out and clears it, then `IGraphicsDevice.UpdateTexture` uploads just that rectangle into the
   browser's persistent texture through a staging buffer on the upload command buffer, without a GPU stall.

There is no queue: frames that arrive before the game reads them merge into one dirty rectangle (backpressure by
coalescing), so a slow game skips intermediate frames instead of piling them up. Hidden browsers are told so
(`WasHidden`) and stop painting.

Not implemented, and not claimed: GPU zero-copy. CEF's shared-texture path hands out D3D11 textures; importing them
into Vulkan needs `VK_KHR_external_memory_win32` with keyed mutex synchronization and a D3D11 device on the GPU the
engine uses. The texture sink in the game (`BrowserFrame` to `BrowserTexture`) is the only part that would change.

### Engine integration

| Part | Project | Depends on |
|---|---|---|
| Message names and framing, the shared frame, security policy and `mnvt://` path rules (used by both processes) | `MNVTEngine.Browser.Protocol` (module `browser`) | Newtonsoft.Json |
| API (`BrowserService`, `BrowserInstance`), host process and IPC client, bridge checks, input model, texture upload pass, the event system, diagnostics, `BrowserSurface` | `MNVTEngine.Browser` (module `browser`, 2D and 3D) | Protocol, Runtime |
| `BrowserSurface2D`: a browser on a `SpriteRenderer`, pointer mapping in 2D | `MNVTEngine.Browser.World2D` (module `browser.2d`) | Browser, World2D |
| `BrowserSurface3D`: a browser on a `MeshRenderer` (emissive), raycast with triangle UVs | `MNVTEngine.Browser.World3D` (module `browser.3d`) | Browser, World3D |
| CEF host | `MNVTEngine.Browser.Host` (`MNVTBrowserHost.exe`, not in a module: the build adds it with `browser`) | CefSharp.OffScreen.NETCore, Protocol |

Browser textures are ordinary `Texture2D`s registered in the `ResourceCache` under a runtime id
(`ResourceCache.SetRuntimeTexture`), so the existing sprite and mesh renderers draw `asset:<TextureId>`: no second
renderer, materials and shaders unchanged. Meshes whose UV origin is the bottom left (built-in primitives) get the page
uploaded bottom row first (`FlipVertically`). `MeshData` carries UV0 for the pointer raycast.

### Threads

- Game thread: the API, events and task completions. Pipe messages are read on a background thread and queued; the
  frame pump delivers them on the game thread, so `await browser.ExecuteJavaScriptAsync(...)` continues there.
- CEF threads (host): paint, load and JavaScript callbacks. They never call Vulkan; they only write shared memory and
  pipe messages.
- Render thread = game thread in this engine: the texture uploads (`BrowserTexturePass`, order 0, before anything draws).

### Bridge

The host registers `window.gameApi` with DevTools (`Page.enable`, then `Page.addScriptToEvaluateOnNewDocument`), so it
exists before any page script, in the top frame of documents whose origin the policy allows (the origin check is
generated from the policy into the script). Page messages go through CefSharp's `PostMessage` with a per-browser
secret that only the injected script holds, so a page calling CefSharp's API directly is ignored. The host checks the
origin again, then the game checks origin and capability once more before raising events or calling handlers.

### Security

Each browser has a profile: `TrustedLocal` (game UI, `mnvt://` assets and granted bridge capabilities),
`RemoteWeb` (http/https, the bridge off unless granted), `Restricted` (allowlisted domains only, no bridge). The host
enforces navigation, popups, downloads, permissions, `file://`, and external protocols in CEF's request, life span,
download and permission handlers; the game enforces bridge capabilities again on every message.

### Audio

Page audio goes through the engine mixer. The host enables audio (CefSharp.OffScreen adds `--mute-audio` by default)
and sets a CEF audio handler, so Chromium hands over each stream's planar float PCM (asked for as 48 kHz stereo)
instead of playing it. The host writes it as interleaved 16-bit samples into a lock-free shared-memory ring per browser
(`SharedAudio`, one second, one writer and one reader; a reader that falls a ring behind skips ahead) and tells the
game its name. Each frame the browser system copies new frames into an `IStreamingVoice` of the scene's audio device
(OpenAL queued buffers) at the browser's volume times its `AudioMixer` group, dropping audio when more than a quarter
second is queued.

## Alternatives considered

| Option | Verdict |
|---|---|
| CefSharp in the game process | Simplest, but NativeAOT games could not use the browser, and a Chromium crash would end the game |
| A native C++ CEF host | Best performance ceiling, but a C++ toolchain and CEF's C API to maintain; the managed host gives the same process boundary |
| Shared D3D11 textures into Vulkan | The fast path for 4K or many browsers; needs external memory interop and a D3D11 device. Future, behind the same texture sink |
| WebView2 | Off-screen rendering is not supported for games; tied to the system's Edge runtime |
