# Third-party code in MNVTBrowserHost (the embedded browser)

Shipped in `Dependencies/Native/Chromium` of games with the `browser` module, with the license files in `licenses/`.

- **Chromium Embedded Framework (CEF) 156.0.15** (`libcef.dll`, `chrome_elf.dll`, `*.pak`, `icudtl.dat`,
  `v8_context_snapshot.bin`, `locales/`), BSD-3-Clause: Copyright (c) 2008-2020 Marshall A. Greenblatt, portions
  Copyright (c) 2006-2009 Google Inc. See `licenses/CEF-LICENSE.txt`.
- **Chromium 156.0.8078.80** (inside CEF): BSD-3-Clause and the licenses of the third-party code it contains (V8,
  Skia, ICU, FFmpeg in its LGPL configuration without proprietary codecs, SwiftShader, ANGLE, and others). The complete
  list is built into the browser: open `about:credits` (or `chrome://credits`) in a page.
- **CefSharp 156.0.150** (`CefSharp*.dll`), BSD-3-Clause: Copyright (c) The CefSharp Authors. See
  `licenses/CefSharp-LICENSE.txt`.
- **.NET runtime** (the host runs on the game's private runtime): MIT, Copyright (c) .NET Foundation and Contributors.

Proprietary codecs (H.264, AAC, MP3 in MP4 containers) are not part of these CEF builds: pages that need them must use
open formats (VP8/VP9/AV1, Opus/Vorbis, WebM). Distributing a build with proprietary codecs needs licenses of its own.
