# Game UI, text and window

This document starts with the Phase 0 audit and plan, then describes the system as it is built.

## Audit (Phase 0, 2026-11-04)

| Area | What exists | Consequence |
|---|---|---|
| Rendering | `IGraphicsDevice` (Vulkan 1.4 backend), `SpriteBatch`/`Renderer2D` with the sprite shader, `DrawCommand` with scissor, streaming vertex buffers | UI geometry uses the sprite vertex format and shader: no new shader, one material |
| Textures | Created once, with no update API | Added `IGraphicsDevice.UpdateTexture`: a staging copy in the frame's upload commands, so a glyph atlas grows without a GPU wait |
| Text | None in the engine; only ImGui's font in the debug UI. `.ttf`/`.otf` were recognized as `Font` assets but never loaded | New text stack in `MNVTEngine.Graphics/Text` |
| Input | `InputState`: keys, mouse, wheel, OS text input (`KeyChar`, layout- and IME-aware), a capture used by the debug overlay | Added a game-UI capture layer below the overlay (`CaptureForUI`, `ForUI`) |
| Window | `GameWindow`: title, size, VSync, resizable, hidden; project `window` settings (title, width, height, vsync, resizable) | Needs icon, fullscreen and borderless, min/max size, position, top-most, and a runtime API |
| Scenes | Per-scene systems through `scene.Systems` (for example `AudioSystem.Attach/For`), and a thread-static current scene for facades | `UISystem` follows the same pattern; `UI.CreateContext()` uses the current scene |
| Assets | GUID asset references, asset types (`Font` included), content archives | Fonts and images are referenced by GUID; UI layouts become a new asset type |
| Diagnostics | Metric providers, debug overlay windows | UI statistics go into a provider and a window |

### Dependencies (verified 2026-11-04)

| Package | Version | Use | Notes |
|---|---|---|---|
| StbTrueTypeSharp | 1.26.13 (2026-08) | TrueType and CFF (OTF) parsing and rasterization | Pure C#, MIT, same author as StbImageSharp (already used), no native code |
| HarfBuzz | not added | Complex-script shaping and ligatures | Not needed for Latin, Polish, Greek or Cyrillic; would add a native library. Possible later extension |
| FreeType | not added | | Native; stb's anti-aliased rasterization is sufficient for UI sizes |

stb_truetype skips GPOS kerning inside extension lookups (type 9), which current fonts use (Source Sans 3 does). The engine reads those itself (`GposKerning`).

### Decisions

- **Bitmap glyphs, not SDF/MSDF.** Glyphs are rasterized at the exact pixel size they are drawn at (UI scale and DPI included), so text is sharp, and the sprite shader is reused. SDF would only pay off for heavy zooming of world-space text; it can be added behind the atlas later.
- **One atlas texture for text and UI shapes.** Glyphs, a white block and generated corner pieces share 1024x1024 pages, so a whole UI is usually one draw call.
- **Clipping on the CPU.** Rectangles are clipped by trimming quads and their UVs, so scroll views do not split batches.
- **Built-in font.** Source Sans 3 Regular (SIL OFL 1.1), embedded in `MNVTEngine.Graphics`, is used when no font is set.
- **Project placement.**
  - `MNVTEngine.Graphics/Text`: fonts, atlas, layout and text drawing, usable by debug tools, world labels and loading screens without the UI.
  - `MNVTEngine.UI`: elements, layout, input, widgets, styling, the UI renderer and serialization. It depends on Graphics, Input and Scenes, never on ImGui or Vulkan.

## Phases

| Phase | Content | Status |
|---|---|---|
| 0 | Audit and plan | done |
| 2 | Fonts (TTF and CFF OTF), fallback, GPOS kerning, glyph atlas with in-place updates, layout (wrapping, alignment, spacing, caret and hit testing), text drawing, built-in font | done |
| 1 | UI project: elements, context, units, layout engine, input capture, hit testing, events, focus | |
| 3 | Widgets and styling | |
| 4 | UI renderer: cached geometry, CPU clipping, statistics | |
| 5 | Window: title, icon, sizes, fullscreen modes, VSync, DPI, project settings, runtime API | |
| 6 | Splash and loading screens, staged scene loading with real progress | |
| 7 | Editor integration | |
| 8 | Samples and documentation | |

Text came first because every widget depends on it.

## Text

- **`Font`.** Loads `.ttf` (glyf outlines), `.otf` (CFF) and the first face of a `.ttc`. WOFF is rejected with a clear message. It exposes the family and style from the name table, and the weight and italic flag from OS/2. Missing glyphs go to `Fallbacks` in order, then to the font's own "missing" glyph. `Font.LoadDefault()` returns the built-in font.
- **`GlyphAtlas`.**
  - One rasterization per (font, glyph, quarter-pixel size).
  - Pages are filled shelf by shelf, from a CPU copy.
  - `Flush()` uploads one dirty rectangle per page and frame.
  - When `MaxPages` are full, the atlas is cleared and `Generation` increments.
- **`TextLayout`.** Lays out Unicode code points, including surrogate pairs:
  - kerning from the kern table and from GPOS;
  - letter, word and tab spacing, and line height;
  - word wrap at spaces (long words break where they overflow) and left, center or right alignment;
  - measurement, and caret and hit-test queries for editing.
- **`TextDrawing`.** `SpriteBatch.DrawText` draws world-space or pixel-camera text.
- **Limits.**
  - No complex shaping (Arabic, Indic) and no ligatures.
  - Glyphs of one size are cached per quarter pixel.
  - Color emoji fonts are not supported (glyphs are monochrome coverage).
