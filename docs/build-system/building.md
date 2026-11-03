# Building and Exporting Games

The `mnvt` command creates, validates, runs and exports projects. It is independent of the editor: it never
references editor code and only starts the editor as a separate process for `mnvt editor`.

```bash
scripts\mnvt.cmd new MyGame --template basic-2d     # or: dotnet run --project src/Tooling/MNVTEngine.CLI -- new ...
scripts\mnvt.cmd run MyGame
scripts\mnvt.cmd editor MyGame
scripts\mnvt.cmd validate MyGame
scripts\mnvt.cmd build MyGame --target win-x64 --configuration release
scripts\mnvt.cmd validate MyGame/Builds/release --run
```

`<project>` is a `.mnvtproj` file or the folder that contains one. Add `scripts/` to `PATH` to type `mnvt` anywhere.
Exit codes are 0 for success, 1 when there are errors, 64 for usage errors and 130 for Ctrl+C.

| Command | What it does |
|---|---|
| `new <Name> [--template id] [--path dir]` | Copies a template from `templates/` into `<dir>/<Name>` and checks that the new project loads. `--list` shows templates. |
| `run <project> [--frames N] [--screenshot f]` | Plays the project from its sources. Same as `MNVTEngine.Player`: stale C# scripts are compiled first. |
| `editor <project> [--frames N]` | Starts the editor from the engine sources and waits for it to close. |
| `validate <project>` | Runs every check a build runs (project, profiles, C# scripts, imports, strict `.mnvts` compile, scenes, prefabs, references) and writes nothing. |
| `validate <exported game> [--run [N]]` | Checks an exported game (see [Validation](#validation)). `--run` also starts it for N frames (default 120). This needs a GPU. |
| `build <project> [--profile p] [--configuration c] [--target t] [--output dir] [--run [N]]` | Exports a standalone game. |

## Templates

| Id | Contents |
|---|---|
| `empty-2d` | A camera in an empty scene. |
| `basic-2d` | A player moved by a C# component, a goal sprite, and a `.mnvts` mission that completes when the player reaches the goal. |

A template is a folder with a `template.json` file (`id`, `description`). Two kinds of tokens are replaced:

- In file names: `__Name__` and `__Identifier__`.
- In text files: `{{Name}}`, `{{Identifier}}` and `{{EngineRoot}}`.

The C# scripts project of `basic-2d` references the engine sources through `{{EngineRoot}}`, so a new project can live anywhere on disk.

## Build profiles

Profiles live in the `.mnvtproj` file. A project without profiles gets `debug` and `release`.

```json
"buildProfiles": [
  { "name": "debug",   "configuration": "Debug",   "target": "win-x64" },
  { "name": "release", "configuration": "Release", "target": "win-x64",
    "executableName": "MyGame", "outputDirectory": "Builds/release", "console": false }
]
```

| Field | Default | Meaning |
|---|---|---|
| `name` | required | Unique. Letters, digits, `_ - .` |
| `configuration` | `Release` | `Debug` or `Release` |
| `target` | `win-x64` | .NET runtime identifier. Only `win-x64` is supported for now. |
| `outputDirectory` | `Builds/<name>` | Relative to the project. It may not escape the project folder. |
| `executableName` | the project name without spaces or symbols | It must differ from the C# scripts assembly name. |
| `console` | true for Debug, false for Release | Shows a console window with the log. |

How the profile is chosen:

1. `--profile` picks a profile by name.
2. Otherwise `--configuration` picks the first profile with that configuration. If there is none, a profile is made for it.
3. Otherwise the `release` profile is used.

After that, `--configuration` and `--target` override the chosen profile's values.

## What a build does

The steps run in this order. Every step reports diagnostics as `file(line,col): error CODE: message`, and the build stops at the first step with errors.

1. **Project.** Loads and validates the project file and its profiles, then checks the output folder.
   The build refuses output folders that overlap the project, `Assets` or `Library`. It also refuses non-empty folders that do not contain a previous build, so a typo in `--output` cannot delete your files.
2. **C# scripts.** `dotnet build -c <configuration>` into `Library/Build/<profile>/Scripts`. The compiled components are loaded so that scenes can be checked against them, then unloaded.
3. **Content** (`ContentCompiler`). Assets are opened **read-only**, so a build never writes metas or caches into the project. Then:
   - Every importer runs: images are decoded, JSON is parsed, and so on.
   - Every `.mnvts` script is compiled strictly against the full runtime API. In the editor an unknown API function is only a warning; in a build it is an error (`MNV3014`), because the game would fail to link.
   - Every scene and prefab is loaded with the game's components.
   - Every asset reference must point to an asset in the project.
   - The content is written out: assets with their `.meta` files (so GUID references stay valid), bytecode per script GUID, and the project file without its development-only settings (`scripts`, `buildProfiles`).
4. **Host** (`GamePackager`). Runs `dotnet publish` on `src/Engine/MNVTEngine.GameHost` with `--self-contained`, the profile's RID, and `--artifacts-path Library/Build/<profile>/artifacts`. The artifacts path keeps builds separate from each other and from the engine's own `bin`/`obj`.
   The host is renamed to the game (`-p:MnvtGameName`), and Release builds ship no `.pdb` files.
   The game's C# assemblies are copied into `Content/Scripts`, except assemblies the host already ships, because the host's copy is always used at run time.
5. **Manifest.** Writes `Content/package.json`: name, profile, configuration, target, engine version, the scripts assembly, and the size and SHA-256 of every file.
6. **Validation** (`BuildValidator`). Validates the staged package (see [Validation](#validation)).
7. **Output.** Only now is the previous build replaced: the staged package is moved into the output folder. A failed build leaves the last good build untouched.
8. **Test run** (`--run [N]`, optional). Starts the exported game for N frames. The game must exit with code 0.

## Output layout

```text
Builds/release/
├── MyGame.exe               apphost; double-click to play
├── MyGame.dll               the game host (MNVTEngine.GameHost renamed)
├── MyGame.runtimeconfig.json / MyGame.deps.json
├── MNVTEngine.*.dll         engine runtime assemblies (no editor, no build tooling)
├── Newtonsoft.Json.dll, Silk.NET.*.dll, StbImage*.dll
├── glfw3.dll                native windowing library
├── coreclr.dll, System.*.dll, ...   the .NET runtime (self-contained)
├── Logs/game.log            written at run time
└── Content/
    ├── package.json         manifest with file hashes
    ├── MyGame.mnvtproj      project settings (window, startup scene, timestep)
    ├── Assets/...           scenes, prefabs, textures, scripts, each with its .meta
    ├── Bytecode/<guid>.mnvtb  verified .mnvts bytecode, run instead of the source
    └── Scripts/MyGame.Scripts.dll   C# game code (plus .pdb in Debug)
```

This differs from the layout originally proposed, which had separate `Dependencies/Managed` and `Dependencies/Native` folders. A self-contained .NET application resolves managed and native assemblies from its own folder through `deps.json`. Moving them would need custom probing and native-library resolvers for every package, for no benefit to the player.

Size is about 89 MB, mostly the .NET runtime. Trimming is not enabled because component discovery and serialization use reflection.

## Running an exported game

- `MyGame.exe` runs the package in `Content/` next to it. It never compiles anything, and it needs neither the .NET SDK, an installed .NET runtime, nor the editor. The build tests run the game with no `dotnet` on `PATH`.
- `.mnvts` scripts run from the build-time bytecode. Invalid bytecode disables that mission runner with an error ("rebuild the game"). It never falls back to the source.
- Optional arguments: `--frames N`, `--screenshot file.png`, `--debug-gl`.
- Exit codes:

  | Code | Meaning |
  |---|---|
  | 0 | OK |
  | 1 | GPU resources leaked |
  | 2 | Missing or damaged content |
  | 3 | The game's C# code failed to load |

- The log goes to the console (Debug builds) and to `Logs/game.log`. If that folder is not writable, a warning is printed and there is no log file.

### Platform requirements

- Windows 10/11 x64.
- A GPU and driver with **OpenGL 4.5**. `opengl32.dll` and the GPU driver come from Windows and the vendor; they are not packaged and cannot be.
- `glfw3.dll` is packaged. Validation fails if it is missing (`MNVB506`).
- Nothing else needs to be installed.

## Validation

`BuildValidator` runs these checks after every build, and on demand with `mnvt validate <exported game>`:

| Code | Check |
|---|---|
| MNVB501/502 | `Content/package.json` exists and loads (schema version 1; newer versions are rejected) |
| MNVB503 | `<exe>.exe`, `.dll`, `.runtimeconfig.json` and `.deps.json` are present |
| MNVB504 | The runtime config is self-contained (`includedFrameworks`) |
| MNVB505 | Every runtime and native asset listed in `deps.json` is present |
| MNVB506 | Required native libraries for the target are present (`glfw3.dll` on win-x64) |
| MNVB507 | No development-only assemblies (editor, build tooling, ImGui, MSBuild, Roslyn) |
| MNVB508 | Every file matches its manifest size and SHA-256 (detects damaged or modified files) |
| MNVB509 | Warning for files that are not part of the build (`Logs/` is ignored) |
| MNVB510 | The packaged project loads, and its startup scene exists |
| MNVB511 | Every script asset has bytecode that passes the verifier |
| MNVB512/513 | Asset index errors; the C# scripts assembly named in the manifest exists |
| MNVB520 | `--run`: the game started, rendered N frames and exited with code 0 |

## Diagnostic codes

| Range | Stage |
|---|---|
| MNVB1xx | Project, profile and output folder (`100` load, `101` unknown profile, `102` configuration, `103` target, `104` output folder, `105` name clash, `106` engine not found) |
| MNVB2xx | Content (`201` asset index, `202` unreadable, `203` import settings, `204`/`205` import errors and warnings, `206` missing referenced asset, `207`/`208` scene and prefab loading) |
| MNVB3xx, CSxxxx | C# scripts (`301` scripts project missing; compiler errors keep their `CS` codes and source positions) |
| MNV1xxx–3xxx | `.mnvts` errors from the compiler; see the [language reference](../scripting/language.md) |
| MNVB4xx | Packaging (`401` host project missing, `402` dotnet not found, `403` publish failed, `404` native dependencies of game code not packaged) |
| MNVB5xx | Package validation (table above) |

## Reproducible builds

The build uses deterministic compilation (`Deterministic`, `ContinuousIntegrationBuild`). Files are written in a fixed order, and the manifest contains no timestamps or machine paths. As a result, building the same sources twice gives an identical manifest, including the hash of every file. This holds for an incremental rebuild and for a rebuild after deleting `Library/Build`, and `PackagingTests.Rebuild_IsByteForByteIdentical` checks it.

## Engine location

Tooling builds games from the engine **sources**, which it finds in one of two ways:

- `MNVT_ENGINE_ROOT`, if set.
- Otherwise, the first parent folder of the running tool that contains `MNVTEngine.sln`.

The first build of each project and profile compiles the engine into `Library/Build/<profile>/artifacts`. Later builds are incremental and take about 5–10 s.

## Limitations

- Only `win-x64` is supported. Other runtime identifiers are rejected (`MNVB103`).
- If the game's C# scripts use NuGet packages with native libraries, those libraries are not packaged. The build warns with `MNVB404`.
- `.mnvts` sources are shipped next to their bytecode. The read-only asset index finds assets through their files. The runtime never reads the sources.
- There is no installer, icon or version resource, and no code signing.
