# Editor extensions

A project can extend the editor with its own C# code: dockable windows, items in the **Tools** menu, and sections in
the inspector for its components. An SDK built on the engine uses this for its control panel (sign in, check, build
and upload content) instead of a separate program.

## The editor project

`scripts.editorProject` in the `.mnvtproj` names a C# class library next to the game scripts:

```json
"scripts": {
  "project": "Scripts/MyGame.Scripts.csproj",
  "editorProject": "Editor/MyGame.Editor.csproj"
}
```

It references the scripts project and `MNVTEngine.Editor.Extensions` (engine assemblies with `Private="false"`, as
the scripts do). It may reference anything else it needs (HTTP contracts, say); those are copied next to it.

- **Building.** The editor builds the editor project (`ScriptProject.ForEditor`), which builds the scripts with it,
  into `Library/EditorAssemblies`, and watches both folders: editing either reloads both.
- **Loading.** The game code loads as usual; the editor assembly loads into the same collectible generation, so it
  sees the game's types (`ComponentInspector<Health>`) and unloads with them. Its components are not registered.
- **Never shipped.** Exports, `mnvt run` and the Player build `scripts.project` only (`ScriptProject.For`), so the
  editor code and what it references never reach a game.
- A project without `editorProject` works as before. Importing an [editor package](packages.md) with editor code
  creates one.

## API (`MNVTEngine.Editor.Extensions`)

No ImGui types: extensions draw through `IEditorGui`, which the editor implements (text is never a format string, so
a `%` is shown). The API depends on the engine only (enforced by `Architecture.Tests`).

| Type | What it is |
|---|---|
| `EditorWindow` | A dockable window. `Title`, `OnGui(IEditorGui)` every frame while open, `OnOpen`, `OnClose`. `Editor` gives the services. |
| `[EditorMenu("Group/Name")]` | On a window: an item in **Tools** that opens it; slashes make submenus. |
| `ComponentInspector<T>` | A section below the inspector's fields for components of `T`: `OnInspectorGui(gui, component)`. |
| `IEditorGui` | `Heading`, `Text`, `Muted`, `ErrorText`, `SuccessText`, `Paragraph`, `Button`, `TextField` (with `password`), `Checkbox`, `Selectable`, `Section`, `SameLine`, `Separator`, `Space`, `Progress`. |
| `IEditorServices` | `ProjectDirectory`, `Scene`, `Selected`, `Settings`, `Secrets`, `Assets(type)`, `ReadPrefab(guid)`, `OpenWindow<T>()`, `BuildBundleAsync(request)`, `Log(level, message)`. |

- **Settings** (`IExtensionSettings`): small strings in `ProjectSettings/Extensions/<assembly>.json`, written at every
  change and meant to be committed (an SDK keeps which uploaded item a prefab is).
- **Secrets** (`IEditorSecrets`): per user and project under `%LOCALAPPDATA%/MNVTEngine/Secrets`, encrypted with
  DPAPI for the Windows account. Never in the project. `Available` is false elsewhere and nothing is stored.
- **`ReadPrefab`** instantiates a prefab asset into a throwaway scene to read it (which components its root has).
- **`BuildBundleAsync`** packages a prefab as a content bundle ([content bundles](../architecture/content-bundles.md))
  in the background, into `Builds/bundles`; the task completes off the editor's thread, so poll it from `OnGui`.
  Background work in general: start a task, keep it in a field, check `IsCompleted` in `OnGui`.

## Lifetime

- One instance of each window and inspector per loaded generation. A reload calls `OnClose` on open windows and
  creates new instances: keep what must survive in `Settings` (or `Secrets`), not in fields or statics.
- Which windows are open is kept in `Library/EditorExtensions.open`, so they come back after a reload or a restart;
  where they dock is in the editor's layout.
- An extension that throws (constructor, `OnGui`, `OnInspectorGui`) is logged to the Console and skipped until its
  code reloads; it cannot take the editor down.
- As with game code, nothing static may keep the extension's types alive, or the old generation is not unloaded
  (the Console warns).

## Example

```csharp
using MNVTEngine.Editor.Extensions;

[EditorMenu("My Game/Health Tool")]
public sealed class HealthTool : EditorWindow
{
    public override string Title => "Health Tool";

    public override void OnGui(IEditorGui gui)
    {
        gui.Heading("Players");
        if (Editor.Selected?.GetComponent<Health>() is { } health && gui.Button("Heal"))
        {
            health.Points = 100;
        }
    }
}
```
