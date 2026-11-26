# UI components and the design system

The UI framework builds on the retained UI in [ui-system.md](ui-system.md) and on [reactivity](reactivity.md). It adds
a code builder (`V`), fluent setters, reactive control flow, motion, a design system (tokens, theme, fonts, icons) and
the components games usually need: dialogs, toasts, tabs, list rows, form fields.

Code and `.mnvtui` documents share one look: the design tokens produce an ordinary `UITheme`, so document widgets
pick up the same styles as built ones.

## Building UI in code

```csharp
var name = Signal.Create("");
var busy = Signal.Create(false);

context.UseDesign();                       // theme from Design.Tokens, follows token changes
context.Mount(() => V.Card(
        V.Title("Join a room"),
        V.FormField("Display name", V.TextField(name, "Your name"), () => name.Value.Length > 24 ? "Too long" : null),
        V.Row(V.Spacer(),
              V.Button("Cancel", ButtonVariant.Quiet, Close),
              V.Button("Join", onClick: Join, icon: Icons.LogIn).Enabled(() => name.Value.Length > 0)))
    .Width(380).Padding(24));
```

- `UIContext.Mount(build)` builds content in its own reactive scope and adds it to `Root`. Calling it again disposes
  the old content first.
- **Factories** in `V`:
  - layout: `Column`, `Row`, `Stack`, `Box`, `Card`, `Spacer`, `Space`, `Divider`, `Scroll`;
  - text: `Text`, `Display`, `Title`, `Heading`, `Strong`, `Muted`, `Small`, `Caption`, `Label`, `Styled`, `Icon`, `Image`;
  - controls: `Button`, `IconButton`, `TextField`, `PasswordField`, `Switch`, `Checkbox`, `Slider`, `Tabs`, `ListItem`, `Spinner`, `FormField`, `ErrorMessage`, `EmptyState`;
  - control flow: `Show`, `Keyed`, `For`, `Pages`.
- **Fluent setters** (`ElementExtensions`) return the element with its own type:
  - layout: `Width`, `Height`, `Size`, `Fill`, `Grow`, `Padding`, `Margin`, `Gap`, `AlignItems`, `Justify`, `Center`, `Anchor`;
  - look: `Background`, `BackgroundImage`, `Border`, `Radius`, `Opacity`, `Foreground`, `Font`, `Class`;
  - state: `Visible`, `Enabled`, `Tooltip`;
  - events: `OnClick`, `OnKey`, `OnFocus`, `OnBlur`, `Action`.
- **Bindings.** Overloads that take a `Func<T>` or an `IReadable<T>` follow the signals they read. They stop when the
  element is disposed. `element.Bind(read, apply)` and `element.Effect(body)` do the same for anything else.
- **Two-way controls.** Controls that take a `Signal<T>` show the signal and write edits back: `TextField`, `Switch`,
  `Checkbox`, `Slider` and `Tabs`.
- **Disposal.** `UIElement.Dispose()` detaches the element and disposes its subtree: bindings, effects,
  `OnCleanup` callbacks and running animations. A disposed element is never reused.
- **Errors.** A binding that throws is logged by the scene's `UISystem` ("A UI binding or effect failed.") and the
  other bindings still run. Outside a scene, `ReactiveRuntime.ErrorHandler` decides.

## Control flow

| Region | Behaviour |
|---|---|
| `V.Show(when, then, otherwise)` | Only the shown branch exists. A change disposes the old branch and builds the other once; the same answer again does nothing. Takes no space when it shows nothing. |
| `V.Keyed(key, build)` | Rebuilds when the key changes (a screen, a loaded result). `.Crossfade()` fades the old content out and the new in; contents fill the region. |
| `V.For(items, key, build)` | Keyed list. A key keeps its element while it stays: reordering moves elements, and an item replaced under the same key updates through its item signal. New keys build; removed keys are disposed. Duplicate or null keys are errors. |
| `V.Pages(index, pages...)` | Each page is built on first show and kept, so switching back keeps its state (scroll, typed text). `.Slide(seconds, distance)` animates switches like a navigation stack: a higher index slides in from the right, a lower one from the left, the old page slides out the other way and the height eases between them (clip the parent to keep pages inside it). |

## Motion

```csharp
panel.AnimateOpacity(1f, Motion.Tween(0.2f, Easing.CubicOut))
     .AnimateTranslate(Vector2.Zero, Motion.Spring(320f, 0.8f))
     .After(2f, () => panel.Dispose());
```

- `Motion.Tween(seconds, easing)` animates over a fixed time.
- `Motion.Spring(stiffness, dampingRatio)` is a critically damped spring at `dampingRatio = 1`. Retargeting a running
  spring keeps its velocity.
- `Motion.Instant` sets the value at once.
- Animated properties:
  - `Opacity`;
  - `RenderScale`, around the element centre;
  - `Translate`, an offset in logical pixels;
  - background color.
- A new animation of the same property replaces the running one.
- `After(seconds, action)` runs on UI time and is cancelled when the element is disposed. Like events, the action runs
  in the scene's context (`SceneManager.LoadScene` after a fade works), and a throwing action is logged, not fatal.
- `Appear(offset, delay, seconds)` is an entrance: the element starts transparent and moved by `offset`, then fades and
  slides into place once it joins a context, also when a region (`Keyed`, `For`, `Show`) builds it later. Increasing
  delays stagger a list: `row.Appear(new Vector2(0, 12), delay: index * 0.05f)`.
- `RenderScale` and `Translate` are visual only: they move the drawing and clips, not the layout.
- **Pointer.** Hit testing follows both transforms, so the pointer hits what is drawn. `PointerEventArgs.Position` is
  in the layout space of the element handling the event, with the transforms of that element and its ancestors undone.
  Sliders, text selection and scroll bars therefore work inside moved or scaled panels. `ContextPosition` is the raw
  context point. `UIElement.ToLayout` and `ToContext` convert between the two.
- **Shrinking buttons.** A press counts as a click when released inside the button's unscaled bounds, so a button
  that shrinks while pressed keeps a click released near its edge.
- **Buttons** from `V.Button`:
  - spring to `Design.Tokens.HoverScale` on hover and to `PressScale` on press;
  - draw a ripple from the point pressed;
  - with `Loading = true`, show pulsing dots and refuse clicks;
  - `Glyph` draws an icon-font glyph before the text.

## Components

- **`Dialog.Open(context, title, body, dismissible, actions...)`** shows a modal card over a scrim in the overlay.
  - While it is open, `UIContext.FocusScope` keeps Tab inside the dialog.
  - Focus starts on the first focusable action.
  - Escape and a click on the scrim close it when it is dismissible.
  - `Close()` returns focus to where it was, fades out and disposes the dialog. Action buttons call `Close()` themselves.
- **`Toasts.Show(context, message, kind, seconds)`** shows a short message at the bottom centre with an icon for its
  kind: info, success, warning or error.
  - At most three toasts show at once; the oldest leaves first.
  - Toasts never take focus or the pointer.
- **`V.Tabs(selected, labels...)`** is a row of `Tab` buttons bound to an index. Left and Right move the selection.
  Combine it with `V.Pages(() => selected.Value, ...)`.
- **`V.ListItem(title, subtitle, onClick)`** is a focusable row with hover, pressed and `Selected` states.
  `.Leading(...)` and `.Trailing(...)` add an avatar, an icon or a button.
- **`V.FormField(label, field, error)`** adds a label above the field and an error message below it. The field gets
  the `.invalid` class while the error function returns text.
- **`V.EmptyState(glyph, title, text, action)`** and **`V.Spinner()`** cover empty and loading content.

## Data in layout documents

A `.mnvtui` element can bind its properties to names:

```json
{ "type": "Label", "name": "Score", "text": "0", "bind": { "text": "score", "visible": "showScore" } }
```

Game code provides the names through `UIDocument.Data`, a `UIData`:

```csharp
var data = GetComponent<UIDocument>()!.Data;
data.Set("score", () => coins.Value * 10)        // computed, one-way
    .Set("showScore", () => !paused.Value)
    .Set("playerName", playerName);             // Signal<string>: a bound text field writes edits back
```

- Names can be set before or after the layout loads, replaced, or removed. A property whose name is not set keeps
  its authored value.
- Values are converted to the property's type: numbers, text, booleans and enums. A failed conversion is logged like
  any failing binding.
- **Two-way binding.** A writable `Signal<T>` bound to the value of an input receives the user's edits. The inputs
  are a text field's `text`, a checkbox's or toggle's `checked`, a slider's `value` and a dropdown's `selectedIndex`.
- Bindable properties:
  - `text` and `placeholder`;
  - every serializable property (`visible`, `enabled`, `width`, `value`...);
  - the style shortcuts (`backgroundColor`, `textColor`, `opacity`...).
- Unknown properties or empty names are load errors with their JSON path.
- The editor's UI Editor shows the block under **Data bindings**, one `property = name` per line.
- Layouts loaded by hand bind with `new UIData().Apply(root)`.

## Design system

`DesignTokens` is one immutable record that defines the look:

- palette;
- text roles: font role, size and letter spacing;
- control heights and icon size;
- radii, border width and spacing scale (`Space1`..`Space8`);
- motion presets (`Fade`, `Hover`, `Slide`, `Open`, `Close`, `Press`) and the hover and press scales.

The defaults are `DesignTokens.Midnight()`, a dark violet-blue theme, and `MidnightHighContrast()`.

- `Design.Current` is a `Signal<DesignTokens>` and `Design.Tokens` reads it. Setting it (`Design.Current.Value = tokens with { Accent = ... }`)
  rebuilds the theme of every context that called `UseDesign()`.
- `UITheme.Design(tokens)` builds the theme, which is usable like any other `UITheme`, including for `.mnvtui`
  documents.
- **Style classes** (`StyleClass`, space-separated; `V` sets them):
  - text roles: `display`, `title`, `heading`, `body`, `strong`, `muted`, `small`, `caption`, `label`, `danger-text`, `success-text`;
  - surfaces: `card`/`panel`, `surface`, `backdrop`, `scrim`;
  - button variants: `primary`, `secondary`, `quiet`, `danger`, `live`;
  - fields: `invalid`.
- **Type selectors** for the new widgets: `Icon`, `Spinner`, `Tab`, `ListItem`, `Dialog`, `Toast`. States work as
  everywhere else, for example `Tab:checked`, `ListItem:hover`, `Button.primary:pressed`.

### Fonts and icons

`DesignFonts` loads the built-in faces from resources embedded in `MNVTEngine.UI`, on first use:

| Face | Role | Licence |
|---|---|---|
| Manrope Medium, ExtraBold | `Body`, `BodyStrong` | SIL Open Font License 1.1 |
| Space Grotesk SemiBold, Bold | `Display`, `DisplayStrong` | SIL Open Font License 1.1 |
| Lucide | `Icons` | ISC |

- Text faces cover Latin with Polish diacritics. For anything else they fall back to the engine's default font.
- The licence texts are embedded next to the fonts (`MNVTEngine.UI.Fonts.*-OFL.txt`, `lucide-LICENSE.txt`) and kept
  in `src/Engine/MNVTEngine.UI/Styling/Fonts/`.
- `Icons` holds the Lucide glyphs as string constants, for example `Icons.Search`, `Icons.User` and `Icons.Music`. Use
  them as `V.Icon(Icons.Mic)` or as `Button.Glyph`.

## Known limits

- **Threads.** Everything UI and reactive runs on the main thread. Work from other threads comes back through
  `UIContext.Dispatcher.Post`, which runs at the start of the next UI update.

## Nine-slice backgrounds

`Image(asset, source)` shows a texture asset; `source` (texels from the top-left) picks one frame of a sprite sheet,
and the image measures and keeps the aspect of that frame.

`BackgroundImage(texture, slice, scale)` draws a texture asset behind any element instead of its background colour
(the colour, when set, tints it). `slice` is the frame on each side in texels: the corners keep their size, the edges
stretch along one axis and the centre both ways, so one small frame fits any box (speech bubbles, panels, pixel-art
windows). `scale` is logical pixels per texel; use a whole number and a point-filtered texture for pixel art. When the
box is smaller than the frame, the corners shrink evenly. The math is `NineSlice.Pieces`; the painter draws it with
`UIPainter.DrawNineSlice`.

```csharp
V.Row(V.Text("Hi!")).Padding(8, 4)
    .BackgroundImage(new AssetRef(bubbleGuid), Thickness.All(4), scale: 2);
```

