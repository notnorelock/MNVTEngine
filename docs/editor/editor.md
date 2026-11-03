# Editor

```bash
dotnet build samples/SceneDemo/SceneDemo.csproj   # builds the demo's components
dotnet run --project src/Editor/MNVTEngine.Editor -- samples/SceneDemo --scripts samples/SceneDemo/bin/Debug/net11.0/SceneDemo.dll
```

| Argument | Meaning |
|---|---|
| `<project>` | Project directory or `.mnvtproj` file. Opens the startup scene. |
| `--scripts Game.dll` | Registers the components of a compiled game assembly. Can be repeated. |
| `--play` | Enters Play Mode on start. |
| `--debug-gl` | Logs GL driver messages. |
| `--select Name`, `--frames N`, `--screenshot out.png` | For automation: select an object (or an asset by path), then exit after N frames and save a screenshot. |

## Projects

| Project | Role |
|---|---|
| `MNVTEngine.Editor.Services` | UI-free editor logic: `EditorSession` (scene file, dirty state, atomic save), `Selection`, `PlayModeController`, `MemberInspector`, `SceneEditing` (picking, delete), `TransformGizmo` (hit testing, drag math, drawing). It is unit-tested and must not reference ImGui; an architecture test enforces this. |
| `MNVTEngine.Editor` | Executable: ImGui backend, docked workspace, panels, dialogs. |

This replaces the nine Editor projects in the original proposal. They would have shared one ImGui context and the same services, so the only boundary that adds value is UI versus logic. Panels are folders in the exe.

## ImGui integration

- **Rendering:** ImGui renders through `IGraphicsDevice`, not raw OpenGL. `ImDrawVert` has exactly the `SpriteVertex` layout, so the built-in sprite shader and alpha blend are reused. Clip rects map to the new `DrawCommand.Scissor`, and geometry is uploaded through the new dynamic index buffer.
- **Textures:** any texture shown in the UI is registered with the ImGui renderer and gets an id. Scene and Game views are render targets that follow their panel's size under a stable id.
- **Input:** the engine's `InputState` edges are forwarded as ImGui events. Typed characters were added to `InputState` for this.
- **Layout:** docking is enabled. ImGui.NET doesn't wrap the DockBuilder API, so the default layout is built through P/Invoke calls into the bundled `cimgui` library. Window → Reset Layout rebuilds it.
- **Layout file:** stored per project in `Library/Editor/layout.ini`. `Library/` is gitignored.

## Panels

| Panel | Features |
|---|---|
| Toolbar | Play/Stop, Pause/Resume, Step; play time. Ctrl+P toggles Play. |
| Hierarchy | Tree in sibling order; click to select; drag onto an object to reparent (world position kept) or onto empty space to move to root. Right-click to create Empty, Sprite or Camera, or to delete. |
| Inspector | For assets: identity, import status, facts, preview, texture import settings (Apply reimports and refreshes every view), references and referenced-by. For objects: name, active state, Transform, every component with its enabled toggle and persisted members: float, int, bool, string, Vector2, Color, enum, asset path (drop from Project), GameObject/component reference (drop from Hierarchy; right-click to clear), asset reference (drop from Project, or right-click to pick an asset of the right type or clear it). Add Component lists the registry. Remove is in the header's context menu. Missing components show their type id and data read-only. |
| Scene | Editor camera: right or middle drag pans, wheel zooms at the cursor, F frames the selection. Click picks the topmost sprite (or empty object). W/E/R switch between move, rotate and scale gizmos. Delete removes the selection. Dropping a texture creates a sprite sized by its pixels-per-unit setting; dropping a prefab instantiates it at the cursor (also works onto Hierarchy nodes as a child). Draws the grid with axes, camera frames and the selection outline. |
| Game | The active scene through its main camera. While playing, keyboard and mouse reach the game only when this panel has focus; mouse coordinates are relative to the panel. |
| Project | Asset browser with type tags, texture thumbnails and failed imports in red (the tooltip shows why). Click an asset to inspect it, double-click a scene to open it. Drag assets onto fields, the Scene view or the Hierarchy; drag them onto a folder to move them. Drag a GameObject from the Hierarchy here to create a prefab. Right-click for Rename, Reimport and Delete; Delete warns about assets that reference it. Changes made outside the editor are picked up every 2 s. |
| Console | Log with Info/Warning/Error filters and counts. Hover an entry to see its exception. It follows new output only while scrolled to the bottom. |

## Scene files and unsaved changes

- **Saving:** Ctrl+S saves; a scene that was never saved prompts for a path relative to Assets. Writes go to `*.tmp` and are then swapped in, so a crash cannot truncate the scene.
- **Unsaved changes:** New, Open and Exit (including closing the window) ask Save / Don't Save / Cancel while the scene has unsaved changes.

## Play Mode

1. **Play** saves the edit scene to JSON in memory and loads it back into a new scene. The copy is exactly what a build would load, shares no objects with the edit scene, and keeps object ids, so the selection follows into the copy.
2. **While playing**, the copy runs on its own `GameClock` and `FixedTimestep`, which start at zero. `SceneManager.LoadScene` replaces only the copy. Edits made in the panels change the copy, and the toolbar says so.
3. **Stop** unloads the copy, running `OnDisable` and `OnDestroy`. The edit scene was never started, so nothing needs restoring and the unsaved-changes flag is unchanged.

## Limitations

- **No undo/redo** until Milestone 8.
- **One selected object** at a time.
- **No reload of game assemblies:** `--scripts` loads them once, into the default load context, so rebuilt game code is not picked up. Milestone 5 introduces collectible load contexts and reload.
- **Scene and Game views are sized in window coordinates.** On high-DPI displays they render at logical resolution, so they are slightly soft.
- **File dialogs are in-app,** listing the project's Assets; there are no native file dialogs.
