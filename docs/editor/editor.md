# Editor

```bash
dotnet run --project src/Editor/MNVTEngine.Editor -- samples/SceneDemo   # compiles and loads the project's C# scripts
```

| Argument | Meaning |
|---|---|
| `<project>` | Project directory or `.mnvtproj` file. Opens the startup scene. |
| `--play` | Enters Play Mode on start. |
| `--gpu-validation` | Enables the Vulkan validation layer (when installed) and logs its messages. |
| `--select Name`, `--frames N`, `--screenshot out.png` | For automation: select an object (or an asset by path), then exit after N frames and save a screenshot. |

## Projects

| Project | Role |
|---|---|
| `MNVTEngine.Editor.Services` | UI-free editor logic: `EditorSession` (scene file, dirty state, atomic save), `Selection`, `PlayModeController`, `MemberInspector`, `SceneEditing` (picking, delete), `TransformGizmo` (hit testing, drag math, drawing). It is unit-tested and must not reference ImGui; an architecture test enforces this. |
| `MNVTEngine.Editor` | Executable: ImGui backend, docked workspace, panels, dialogs. |

This replaces the nine Editor projects in the original proposal. They would have shared one ImGui context and the same services, so the only boundary that adds value is UI versus logic. Panels are folders in the exe.

## ImGui integration

- **Rendering:** ImGui renders through `IGraphicsDevice`, not raw Vulkan. `ImDrawVert` has exactly the `SpriteVertex` layout, so the built-in sprite shader and alpha blend are reused. Clip rects map to the new `DrawCommand.Scissor`, and geometry is uploaded through the new dynamic index buffer.
- **Textures:** any texture shown in the UI is registered with the ImGui renderer and gets an id. Scene and Game views are render targets that follow their panel's size under a stable id.
- **Input:** the engine's `InputState` edges are forwarded as ImGui events. Typed characters were added to `InputState` for this.
- **Menu bar:** on Windows, File / Edit / GameObject / Build / Window are a native Win32 menu (`NativeMenuBar`) on the
  window. Each menu is built when it opens, so enabled and checked states are current. A chosen item runs at the start
  of the next frame. Alt+letter opens a menu. Alt alone does not, because Alt + drag orbits the 3D Scene view.
  Elsewhere the same menus (`MenuEntry` lists) are drawn as an ImGui menu bar.
- **Layout:** docking is enabled. ImGui.NET doesn't wrap the DockBuilder API, so the default layout is built through P/Invoke calls into the bundled `cimgui` library. Window → Reset Layout rebuilds it.
- **Layout file:** stored per project in `Library/Editor/layout.ini`. `Library/` is gitignored. Windows are saved under
  stable ids (`###Hierarchy`), so their icon titles can change; layouts saved before the icons are migrated on load.
- **Icons:** [Lucide](https://lucide.dev) 1.69.0 (ISC, `src/Editor/MNVTEngine.Editor/Resources/Fonts/lucide.ttf`, embedded in the
  editor assembly) is merged into the editor font. Every glyph can go in any label (`$"{Icons.Save}  Save"`). `Icons.cs` lists the ones
  in use. To add one, find its name at lucide.dev and its codepoint in the `lucide-static` package's
  `font/codepoints.json`. Toolbars use `Gui.IconButton(icon, tooltip, active)`: a square button with the tooltip on
  hover. Dialog buttons (Save, Cancel) stay text.

## Panels

| Panel | Features |
|---|---|
| Toolbar | Play/Stop, Pause/Resume, Step; play time. Ctrl+P toggles Play. Script status (compiling / N components / N errors) and Recompile; see [C# scripting](../engine/csharp-scripting.md). |
| Hierarchy | Tree in sibling order, with tree lines and striped rows. Each row's icon shows what the object is: camera, light, renderer, audio, UI, physics, a script, a folder (an empty object with children), or a warning for a missing component. Icons on the right show its other components (their names on hover). Click to select; drag onto an object to reparent (world position kept) or onto empty space to move to root. **+** or right-click (or the GameObject menu) creates Empty, Header, Sprite, Camera, a 3D Object (each primitive) or a Light (directional, point, spot, ambient), or deletes. Search filters by name into a flat list. **Hierarchy Style** (right-click) adds a `HierarchyStyle` component: a colored label (a marker and a fill, or a gradient) or a section header (a full-width bar with the name centered). Edit its color in the Inspector. Games load it and ignore it. |
| Inspector | For assets: identity, import status, facts, preview, texture import settings (Apply reimports and refreshes every view), the material editor, the [model inspector](../engine/models.md#model-inspector) (preview, import settings, extract and remap materials and textures), references and referenced-by. For objects, laid out like Unity's: a header with the object's icon, active toggle and name; then a foldout per component (icon, enabled toggle, name, and a ⋮ menu, also on right-click, with Remove Component). Rows have the label on the left and the value on the right: numbers, ints, bools, strings, enums, vectors as X/Y/Z fields, colors as swatches (click for a picker), asset paths (drop from Project), object fields for GameObject/component references (drop from Hierarchy; right-click to clear) and asset references (drop from Project, or the ⊙ picker / right-click to choose an asset of the right type or clear it). Add Component lists the registry. Missing components show their type id and data read-only. |
| Scene | **2D / 3D** toggle in the toolbar (3D is chosen for scenes with meshes or lights). **3D:** the scene through the game's renderer (lights, shadows, skinned models) with a ground grid. Alt + left drag orbits, middle drag pans, right drag looks around and WASD/QE fly (Shift faster), wheel zooms, F frames the selection. Click picks the nearest mesh triangle, or a light or camera icon. W/E/R: move (axis arrows and plane squares), rotate (rings) and scale (local axes, center square for uniform). Ctrl snaps to 0.5 units, 15 degrees and 0.1 scale. Models dropped into the view land on the ground plane. **2D:** editor camera: right or middle drag pans, wheel zooms at the cursor, F frames the selection. Click picks the topmost sprite (or empty object). W/E/R switch between move, rotate and scale gizmos. Delete removes the selection. Dropping a texture creates a sprite sized by its pixels-per-unit setting; dropping a prefab instantiates it at the cursor (also works onto Hierarchy nodes as a child). Draws the grid with axes, camera frames and the selection outline. |
| Game | The active scene through its main camera. While playing, keyboard and mouse reach the game only when this panel has focus; mouse coordinates are relative to the panel. |
| Project | Asset browser with type tags, texture thumbnails and failed imports in red (the tooltip shows why). Click an asset to inspect it, double-click a scene to open it. Drag assets onto fields, the Scene view or the Hierarchy (a model becomes a GameObject hierarchy, see [models](../engine/models.md)); drag them onto a folder to move them. Drag a GameObject from the Hierarchy here to create a prefab. Right-click for Rename, Reimport and Delete; Delete warns about assets that reference it. Changes made outside the editor are picked up every 2 s. |
| Console | Log with Info/Warning/Error filters and counts. Hover an entry to see its exception. It follows new output only while scrolled to the bottom. |

## Scene files and unsaved changes

- **Saving:** Ctrl+S saves; a scene that was never saved prompts for a path relative to Assets. Writes go to `*.tmp` and are then swapped in, so a crash cannot truncate the scene.
- **Unsaved changes:** New, Open and Exit (including closing the window) ask Save / Don't Save / Cancel while the scene has unsaved changes.

## Undo and redo

- **Keys and menu:** Ctrl+Z undoes, and Ctrl+Y or Ctrl+Shift+Z redoes. The **Edit** menu names the step, for example "Undo Move" or "Redo Paint Tiles". In a text field, Ctrl+Z belongs to the field.
- **How it works (`UndoHistory`, Editor.Services):** each step is a snapshot of the edit scene, serialized exactly as it is saved, so every kind of edit is undoable.
  - Covered: transforms, gizmos, component properties, adding and removing components, create, delete, rename, reparent, prefab instances and tile painting.
  - New features need no per-command undo code.
- **Grouping:**
  - Edits are grouped until no mouse button is held and no widget is active. A whole gizmo drag, a dragged or typed number, or a tile stroke is therefore **one** step.
  - Setting a value to what it already was makes no step.
- **What is kept:** restoring keeps the scene file and the selection (by object id), and the history holds the last 100 steps.
- **Unsaved state:** undoing back to the saved state clears the unsaved `*` mark.
- **When it is cleared:** opening or creating a scene clears the history.
- **Play Mode:** undo is off while playing, because Play Mode edits are discarded on Stop anyway.

## Runtime diagnostics in Play Mode

The game's debug interface runs inside the Game view during Play Mode, with its own ImGui context: the HUD (F1), profiler (F2), console (F3), debug drawing (F4), warnings (F5) and all windows (F6). The Game view must have focus for these keys. Its draw counts are the game's own; the Scene view and the editor UI are not included. The editor's Profiler panel and the game's diagnostics share one profiler.

**Window → Diagnostics Settings** edits the project's `diagnostics` section: debug UI toggles, keys, the HUD's look, budgets and thresholds, and which windows and metric providers exist. Changes apply from the next Play. **Save to project** writes them into the `.mnvtproj`, so exported Debug builds use them too. See [Runtime diagnostics](../engine/diagnostics.md).

## Script Editor

The Script Editor is docked next to the Scene and Game views.

- **Opening files:** double-click a script or text asset in the Project panel, or press **Edit in Script Editor** in a script's inspector. Assets that open here include `.mnvts`, `.json`, shaders, `.mnvtanim` and `.mnvtmat` (`.mnvtanimator` opens in the Animator). The **C# scripts** list opens files from `Scripts/`.
- **Saving:** Ctrl+S saves the file while the Script Editor has focus (and saves the scene otherwise). Tabs mark unsaved files.
- **Changes from outside:** a clean tab reloads when the file changes on disk; a tab with unsaved edits keeps them and shows a warning.
- **Checking:**
  - `.mnvts` is compiled as you type; click a diagnostic to jump to its line.
  - Saving a `.cs` file triggers the usual recompile and hot reload. Its compile errors are listed under that file.
- **New files:** **+ C# component** creates `Scripts/<Name>.cs` with a component skeleton, and **+ Mission** creates `Assets/Missions/<Name>.mnvts`.
- **Open externally** hands the file to the program Windows associates with it (for example Visual Studio or VS Code).
- **Limitations:** there is no syntax highlighting or autocomplete, and closing a tab discards unsaved edits without asking.

## UI Editor

The UI Editor edits game UI layouts (`.mnvtui`, see [game UI](../ui/ui-system.md)). It is docked next to the Scene and Game views.

- **Opening layouts:**
  - Double-click a `.mnvtui` asset in the Project panel, or press **Edit in UI Editor** in its inspector.
  - **Create > UI Layout** (right-click empty space in the Project panel's file list) makes a full-screen layout in the current folder and opens it.
  - `--edit Assets/UI/Menu.mnvtui` opens one on start.
- **Element tree** (left). Click to select. **Add** puts the widget chosen in the list into the selected element; **Duplicate**, **Delete**, **Up** and **Down** act on the selected element. The root cannot be deleted.
- **Preview** (center). It is drawn with the game's UI renderer at the chosen resolution, with real fonts and images. Click an element to select it; the selected one is outlined.
- **Inspector** (right):
  - name, text, placeholder, and font (an asset path such as `Fonts/Title.ttf`, or `asset:<guid>`);
  - every stored property of the widget, including the **Action** name that game code binds;
  - style values (a checkbox sets or clears each one), and per-state styles (hover, pressed, focused, disabled, checked).
  - Units take `120`, `50%` or `auto`, and asset fields take a path; press Enter to apply.
- **Saving and undo:** Ctrl+S saves and Ctrl+Z / Ctrl+Y undo and redo while the window has focus. A drag or a typed value becomes one step. **Save*** marks unsaved changes.
- **Limitations:** no drag and drop in the tree or preview, and quitting the editor does not ask about an unsaved layout.

## Animator

The Animator window edits animator controllers (`.mnvtanimator`, see [animator controllers](../engine/models.md#animator-controllers)) as a graph, docked next to the UI Editor.

- **Open:** double-click a `.mnvtanimator` asset in the Project panel, or press **Edit in Animator** in its inspector. **Create > Animator Controller** in the Project panel makes a new one with an Idle state. `--edit <file.mnvtanimator>` opens one at startup.
- **Parameters** (left): add a float, int, bool or trigger, rename it (conditions follow), set its default, delete it (its conditions go too).
- **Graph** (middle):
  - States are nodes: the default state has an orange border. During Play Mode, a state a running `ModelAnimatorController` of this asset is in is green, with a bar showing how far it has played.
  - **Any State** is the teal node.
  - Drag nodes to move them. Middle-drag or Alt+drag pans, and **Frame** brings the nodes back into view.
  - Right-click the graph for **Add State**. Right-click a state for **Make Transition** (then click the target state), **Set as Default State** and **Delete**.
  - Click an arrow to select its transition; Delete removes it.
- **Inspector** (right): a state's name (transitions follow renames), clip, speed and looping. A transition's fade, exit time and conditions; each condition offers the comparisons its parameter's type allows.
- **Clip names:** pick a model in the toolbar to choose clips from its animations; otherwise type them.
- The toolbar shows whether the controller is valid; hover the problem count for the runtime's messages. **Ctrl+S** saves, **Ctrl+Z/Y** undo and redo while the window has focus.
- Node positions are saved under `"editor"` in the file; the runtime ignores them.

## Terrain

The Terrain window (docked next to the Inspector) edits the terrain world of a `WorldTerrain` component in the Scene view, with TerrainHeaven's tools. See [terrain](../engine/terrain.md) for the world itself.

**Open a world:**

- Select a GameObject with a `WorldTerrain` and press **Edit terrain in Scene view**.
- Or double-click a `.mnvtterrain` asset in the Project panel: this selects the WorldTerrain of the open scene that uses it.
- Or start the editor with `--edit <file.mnvtterrain>`.
- A WorldTerrain without a world gets one from **Create world asset**: `Assets/Terrain/<name>.mnvtterrain`, holding a 60 × 60 tile.

**Saving.** Every finished action is saved to the `.mnvtterrain` file at once and recorded in the world's own history. **Ctrl+Z/Y** undo and redo it while editing; scene undo resumes when editing stops (✕). Editing stops when Play starts or the scene changes.

**Tools.** Left drag uses the chosen tool. Alt, middle and right drag still move the camera.

| Tool | What it does |
|---|---|
| Select | Click a node or surface to select it. Drag a node to move it on its height; Shift + drag moves it vertically. Ctrl + drag moves a whole element. |
| Road, River, Cut spline, Fence, Stairs | Drag from start to end on the ground plane. Ctrl curves the element, with handles at the thirds. Shift snaps the end to 45° steps. An end dropped on a free node of another element connects to it, as a road onto an intersection outlet. |
| Intersection, Terrain tile, Cut point | Click to place. |
| Polygon | Click the points, then press Enter to finish. Esc cancels. |
| Height | Raise, lower or smooth painted heights. Ctrl inverts raise and lower. |
| Texture | Paints the selected layer of every tile, with its shape, opacity, hardness, spacing and rotation. The layer textures and tilings are set here; drop textures from the Project panel. |
| Foliage | Scatters the checked presets (density, minimum spacing, maximum height difference). Ctrl + drag erases. Presets are edited in the same list. |
| Props | Places the chosen prop asset at its spacing, with optional random rotation. Ctrl + drag erases. Drop a model from the Project panel to add an asset; set its scale and pivot calibration, or use **Center on ground**. |

**Selected element.**

- The selected element shows its TerrainHeaven properties (sections, numbers, toggles, choices and buttons).
- **Textures and UVs** sets each group's texture (drop one from the Project panel), rotation and UV offset/scale.
- Delete removes the element. Copy / Copy properties / Paste properties, and Ctrl+C / Ctrl+V in the Scene view, paste it in front of the camera.
- **Presets** saves the element's settings under a name, applies them, or makes them the default for new elements of that type. Presets are per project, kept in `Library/Editor/terrain-presets.json`.

**Other sections.**

- **World settings:** LOD preview, LOD switch distances, sky color, enhanced visuals, day/night cycle and hour.
- **History:** click an entry to jump back to it.

## Material inspector

Selecting a `.mnvtmat` in the Project panel shows it in the Inspector ([format](../graphics/materials-and-lighting.md)):

- **Preview.** A sphere drawn with the material by the engine's 3D renderer. 2D sprite materials are previewed on sprites in the scene instead.
- **Shader.** Standard, Unlit, Sprite (2D), or a custom shader: a fragment asset path and, for meshes, a vertex asset path; press Enter to apply.
- **Render settings.** Blend; cull, depth test and depth write ("Default" follows the shader); render-queue override. The values in effect are shown below them.
- **Properties.**
  - Standard and Unlit list their documented properties: colors (emission is HDR), sliders, numbers and texture slots.
  - Custom shaders list the uniforms and samplers their GLSL declares.
  - Texture slots show a thumbnail. Drag a texture onto one from the Project panel; **x** clears it, and **Reset** returns a value to the shader's default.
- **Saving.** Every edit is written to the file when it ends, so the scene and Game view update at once. **Undo**/**Redo** in the inspector step through the edits.

**Create > Material** (right-click the Project panel's file list) makes a new white Standard material in the current folder.

## Project Hub and building

- **Project Hub.** Starting the editor without a project, or **File → Project Hub**, opens the hub: recent projects (right-click to remove one), New Project from a template, and Open. The recent list is kept in `%APPDATA%/MNVTEngine/recent-projects.json`.
- **Build menu (`BuildController`).** Build Settings (profile, output folder), Build (Ctrl+B), Build and Run (Ctrl+Shift+B), Run Last Build, Validate Project, Open Build Folder.
  - Work runs on a background task while the editor stays usable. The toolbar shows "Building (N s)", and diagnostics go to the Console.
  - Builds use the files on disk, so unsaved scene changes are offered for saving first.
- **Same pipeline.** Everything `mnvt` does is available here; the command line remains for automation and CI.

## Diagnostics panels

- **Profiler:** frame time and the cost of each engine region; see [profiling](../engine/profiling.md).
- **Script Debugger:** breakpoints and stepping for `.mnvts` scripts in Play Mode; see the [language reference](../scripting/language.md#debugging).
- Automation flags: `--focus <panel>` brings a panel forward, and `--break <script>:<line>` sets a breakpoint at startup. Combine them with `--play --frames N --screenshot file.png`.

## Play Mode

1. **Play** saves the edit scene to JSON in memory and loads it back into a new scene. The copy is exactly what a build would load, shares no objects with the edit scene, and keeps object ids, so the selection follows into the copy.
2. **While playing**, the copy runs on its own `GameClock` and `FixedTimestep`, which start at zero. `SceneManager.LoadScene` replaces only the copy. Edits made in the panels change the copy, and the toolbar says so.
3. **Stop** unloads the copy, running `OnDisable` and `OnDestroy`. The edit scene was never started, so nothing needs restoring and the unsaved-changes flag is unchanged.

## Limitations

- **Undo/redo covers the edit scene only.** Asset changes (import settings, renames, deleted files) are not undone.
- **One selected object** at a time.
- **Scene and Game views are sized in window coordinates.** On high-DPI displays they render at logical resolution, so they are slightly soft.
- **File dialogs are in-app,** listing the project's Assets; there are no native file dialogs.
