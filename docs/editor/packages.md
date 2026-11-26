# Editor packages

An editor package adds a kit to a project, as a Unity package does: an SDK's components and its control panel, a tool
set, shared art. It is a `.mnvtbundle` of kind `mnvt.package`. Unlike a [content bundle](../architecture/content-bundles.md)
it carries C# code, which compiles into the project and runs in the editor, so a person imports it because they trust
its author; games never load one (`ContentBundleFile.Open` refuses it).

Code: `MNVTEngine.Build.EditorPackageFile` (format and checks), `EditorPackageImporter` (plan and import),
`mnvt package editor` / `mnvt package import`, the editor's **File > Import Package...** (`PackageImportDialog`).
Tests: `Build.Tests` (`EditorPackageTests`).

## What a package holds

A `.mnvtpack` package with `bundle.json` and files in three areas, each in a folder named after the package:

| Entry | Imported to |
|---|---|
| `bundle.json` | `{ "schemaVersion": 1, "kind": "mnvt.package", "name": "MyKit", "version": "1.2", "description": "..." }` |
| `Assets/MyKit/...` | `Assets/MyKit/...`, with the `.meta` files, so GUIDs (and references to them) survive |
| `Scripts/MyKit/...` | next to the game's C# project (`scripts.project`): `.cs` sources compiled with the game code |
| `Editor/MyKit/...` | next to the editor project (`scripts.editorProject`, [editor extensions](extensions.md)): `.cs` sources for windows and inspectors |

- **Own folders only.** Every file is under `<Area>/<Name>/`, so a package never writes into the project's own files
  and can be upgraded or removed as a whole.
- **Sources, not projects.** `Scripts/` and `Editor/` hold `.cs` files (and `.md` notes); `.csproj`, `.props`,
  `.targets`, solutions and project files are refused, so a package cannot change how the project builds. What its
  code references must be available to the project's C# projects (the engine modules a project compiles against).
- Names: letters, digits, `.`, `_`, `-` (up to 64, starting with a letter). At most 20 000 files and 512 MB.

## Importing

**File > Import Package...** in the editor (packages in Downloads and on the Desktop are listed; or type a path), or
`mnvt package import <project> --file MyKit.mnvtbundle`. The editor shows the plan first:

- what is added, which files replace files of the project's (amber), which files of an earlier version are removed;
- that it contains code, which runs in this editor;
- problems that block the import: an asset GUID that belongs to another asset of the project (the database would
  reassign it and break references), a damaged receipt.

Importing writes the files and records them in `ProjectSettings/Packages/<Name>.json` (the version and every file).
C# projects the package's code needs and the project lacks are created and set in the project file:
`Scripts/<Game>.Scripts.csproj` (`scripts.project`, as the Basic templates', so an Empty project works too) and
`Editor/<Game>.Editor.csproj` (`scripts.editorProject`; it references the scripts project and
`MNVTEngine.Editor.Extensions`). The editor
refreshes the assets and recompiles; the package's windows appear in **Tools**.

**Upgrading** is importing a newer version: files it still has are replaced, files only the old version had are
deleted (from the receipt), the project's own files are kept. Commit the receipt with the files.

## Making a package

Lay the files out in a folder as they are imported, then:

```bash
mnvt package editor <folder> --name MyKit --version 1.2 [--description "..."] [--output MyKit.mnvtbundle]
```

`<folder>` holds `Assets/MyKit/`, `Scripts/MyKit/` and `Editor/MyKit/` (any may be missing); `bin/` and `obj/` are
skipped. The written file is checked as an import checks it. An SDK usually has a small script that copies its
sources into such a folder and runs this.
