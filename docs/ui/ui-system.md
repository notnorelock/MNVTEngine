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
| 1 | UI project: elements, context, units, layout engine, input capture, hit testing, events, focus | done |
| 3 | Widgets and styling | done |
| 4 | UI renderer: cached geometry, CPU clipping, statistics | done |
| 5 | Window: title, icon, sizes, fullscreen modes, VSync, DPI, project settings, runtime API | done |
| 6 | Splash and loading screens, staged scene loading with real progress | done |
| 7 | Editor integration (Play Mode), UI layouts (`.mnvtui`), diagnostics | done |
| 8 | Samples and documentation | done (`samples/UIShowcase`) |

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

## UI

Game code creates a context in the current scene and builds a tree of elements:

```csharp
var ui = UI.CreateContext();
var menu = ui.Root.AddChild(new Panel { Width = UIUnit.Percent(40), Layout = LayoutMode.Column, Gap = 8,
    Anchor = new Vector2(0.5f, 0.5f), Pivot = new Vector2(0.5f, 0.5f), StyleClass = "panel" });
menu.AddChild(new Button { Text = "Play", Action = "play" });
ui.OnAction("play", _ => SceneManager.LoadScene("Scenes/Level1.mnvt"));
```

- **`UISystem`.** One per scene, attached by the runtime (and by the editor in Play Mode). It owns the contexts. Each frame, before gameplay, it updates them from top to bottom by `SortOrder` and takes the keyboard and mouse they use away from gameplay (`InputState.CaptureForUI`). The debug overlay still comes first.
- **`UIContext`.**
  - `Root` holds the tree; `Overlay` holds dropdown lists and tooltips.
  - Coordinates are logical pixels: window pixels divided by `Scale`. High-DPI framebuffers are handled through `FramebufferScale`.
  - The context tracks focus (Tab and Shift+Tab), hover and pressed state.
  - Pointer events carry the keyboard modifiers (`Shift`, `Control`, `Alt`): Shift-click extends a text field's selection.
  - Text fields undo and redo (Ctrl+Z, Ctrl+Y or Ctrl+Shift+Z). Typing and deleting merge into one step per word, at most 100 steps are kept, and setting `Text` from code clears the history (`TextEditor.Undo`, `Redo`, `CanUndo`, `CanRedo`).
  - It raises `ActionInvoked`; `OnAction(name, handler)` binds behaviour by name.
- **Units.** `UIUnit`: pixels, percent of the parent and auto (`"120"`, `"50%"`, `"auto"`). `Thickness` is used for margin and padding.
- **Layout (`LayoutEngine`).**
  - Modes: `Absolute` (anchor, pivot and offsets), `Row`, `Column` and `Grid`.
  - Flex grow and shrink, down to min/max sizes.
  - Justify and align, gap, and a measure cache.
  - Layout runs only when something changed; element counts are taken in the same pass.
- **Events.** `PointerEntered`, `PointerExited`, `PointerPressed`, `PointerReleased` and `PointerClicked` bubble from the target to the root. `KeyPressed` and text input go to the focused element (`GotFocus`/`LostFocus`). The wheel scrolls the innermost `ScrollView` under the pointer. In a scene's UI, event handlers and actions run in the scene's context (`Time`, `Input` and `SceneManager` work), and a handler that throws is logged like a component callback (event 2000) instead of breaking the UI or the game.
- **Widgets.**
  - Display: `Panel`, `Label`, `Image`, `Separator`, `Tooltip`, `ProgressBar` (determinate or indeterminate).
  - Toggles: `Button`, `Checkbox`, `Toggle`, `RadioButton` (grouped).
  - Selection: `Slider`, `ScrollView` (wheel, drag, scrollbar), `Dropdown`.
  - Text: `TextField` and `TextArea` with a caret, selection, clipboard, max length, validation, placeholder and password.
- **Styling.**
  - Precedence: an element's own `Style`, then `StateStyles` (hover, pressed, focused, disabled, checked), then the context's `UITheme`.
  - Theme selectors: `"Button"`, `"Button:hover"`, `".panel"`, `".panel:focused"`.
  - `UITheme.Dark` and `UITheme.Light` are built in.
- **Fonts.**
  - `UI.LoadFont(asset, loaded, failed)` loads on a worker and calls back on the game thread, so it is safe to touch elements in the callback. `UI.LoadFontAsync` returns a task; do not touch elements from its continuation.
  - Elements without a font use the default font (`Font.LoadDefault()`).
- **Rendering (`UIRenderer`).**
  - Each context keeps its mesh cached. It is rebuilt only when something visible changed, the atlas was cleared, or the scale changed.
  - Text and shapes share the glyph atlas. Clipping is done on the CPU, so a typical UI is one draw call.
  - The UI draws after the scene and before the debug overlay.
- **Layouts (`.mnvtui`).**
  - `UILayoutSerializer` saves and loads trees as JSON (schema 1). It writes only values that differ from the defaults.
  - Widget types come from an explicit registry: `UILayoutSerializer.Register<MyWidget>()`.
  - Properties are public settable values of supported types. Fonts and images are `asset:<guid>` references.
  - Unknown types, keys, properties, states and bad values are errors that give their JSON path. Newer schema versions are rejected.
  - Delegates are never serialized: behaviour binds through `Action` names.
  - `UI.LoadLayout(asset)` loads a layout asset. The importer records font and image dependencies, so exports pack them.

### Layouts in scenes

Layouts are made in the editor's [UI Editor](../editor/editor.md#ui-editor). A `UIDocument` component shows one in a scene:

- **Settings.** `Layout` (a `.mnvtui` asset), `SortOrder` and `Scale`.
- **Lifecycle.** It loads in `Awake`, into its own context. Disabling the component hides the context; destroying it removes the context. A broken layout is logged and leaves an empty context.
- **Binding behaviour.** Game code binds actions in `Start`:

```csharp
var ui = GetComponent<UIDocument>()!.Context!;
ui.OnAction("resume", _ => ui.Visible = false);
```

Examples:

- `samples/UIShowcase/Assets/UI/PauseMenu.mnvtui`, shown by the "Pause Menu" object and bound in `Scripts/PauseMenu.cs`. Press P in the game to open it.
- `samples/Platformer/Assets/UI/Hud.mnvtui` and `Pause.mnvtui`, bound in `Scripts/GameUI.cs`:
  - The HUD shows collected coins and the mission's current objective, and a "Level complete" card with **Play again** and **Quit**.
  - Escape opens the pause menu (**Resume**, **Restart level**, **Quit**) and pauses the game with `Time.TimeScale = 0`.

## Window

Settings in the project's `window` section:

- `title`, `width`, `height`, `vsync`, `resizable`;
- `mode`: `windowed`, `borderless`, `fullscreen` or `borderlessFullscreen`;
- `icon`: a PNG asset path;
- `minWidth`, `minHeight`, `maxWidth`, `maxHeight`, `x`, `y`, `topMost`.

They are validated when the project loads. At runtime, `Application.Window` exposes:

- `Title`, `Mode`, `VSync`, `Resizable`, `TopMost`, `Position`;
- `Width`/`Height` and `FramebufferWidth`/`FramebufferHeight`;
- `SetSize`, `Center` and `SetIcon(asset)`.

`Application.Quit()` closes the game; in the editor, it stops Play Mode. Play Mode has no game window of its own (`Application.HasWindow` is false), so game code checks it before using `Application.Window`.

## Splash and loading screens

- **Staged loading.** Scenes load over several frames (`SceneLoadOperation`):
  - Workers read the scene file and decode the textures it references.
  - The game thread uploads the textures and deserializes the scene, within 6 ms per frame.
  - Progress counts completed steps. It is indeterminate until the amount of work is known, and is never invented.
- **While loading.** Gameplay is frozen and the window keeps presenting. The scene and its UI are not drawn until the load has finished; the loading screen appears at full opacity (no fade-in) and fades out over the loaded scene.
- **Failures.** A failed startup load ends the game with exit code 2. A failed later load is logged and keeps the current scene. A newer `SceneManager.LoadScene` replaces a load in progress.
- **`splashScreen`** (project file, off by default):
  - `background`, `backgroundImage`, `logo`, `title`, `subtitle`;
  - `minimumDuration`, `fadeIn`, `fadeOut`;
  - `skippable` (click, Space, Enter or Escape, once loading is done), `showProgress`, `showEngineBranding`.
- **`loadingScreen`** (project file, on by default):
  - `title`, `background`, `foreground`, `accent`, `logo`, `font`;
  - `showProgress`, `showPercentage`, `tips`, `tipSeconds`.
  - It is shown for scene loads that are not hidden by the splash.
- **Game code.**
  - `LoadingScreen.Options` changes the defaults.
  - `LoadingScreen.Show(options)` returns a handle for the game's own work: `Report(progress, task)`, then `Dispose()` to fade out.
- Both screens are standalone UI contexts, so they draw before any scene exists.

## Editor and diagnostics

- **Play Mode.** Play scenes get a `UISystem` laid out to the Game view. The view draws the game UI over the scene, with the same input capture as the runtime.
- **Metrics.** UI metrics are reported only when the scene has a UI; otherwise they are `Unavailable`:
  - `ui.elements`, `ui.visible`, `ui.drawCalls`, `ui.quads`;
  - `ui.layoutPasses`, `ui.rebuilds`, `ui.textLayouts`;
  - `ui.glyphs`, `ui.atlasPages`.
- **Debug overlay UI window** (`window ui on`, runtime and Play Mode). Per context, it shows:
  - statistics, and the focused, hovered and pressed element;
  - the element tree with bounds, state, layout and clip;
  - outlines of all elements, the focused one, the hit-test target and the selected one;
  - the glyph atlas pages.

## Known limits

- **Samples.** `samples/UIShowcase` covers every widget, async fonts, the window API, the splash and the loading screen in one project. The separate samples listed in the specification were not created.
