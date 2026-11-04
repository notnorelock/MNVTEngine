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
   It also loads the content key from `Settings/content.key` (see [Content encryption](#content-encryption)).
2. **C# scripts.** `dotnet build -c <configuration>` into `Library/Build/<profile>/Scripts`. The compiled components are loaded so that scenes can be checked against them, then unloaded.
3. **Content** (`ContentCompiler`). Asset files that have no `.meta` yet (for example, added with the file explorer) get one, as they would in the editor, with warning `MNVB209`: commit it. Then assets are opened **read-only**:
   - Every importer runs: images are decoded, JSON is parsed, and so on.
   - Every `.mnvts` script is compiled strictly against the full runtime API. In the editor an unknown API function is only a warning; in a build it is an error (`MNV3014`), because the game would fail to link.
   - Every scene and prefab is loaded with the game's components.
   - Every asset reference must point to an asset in the project.
   - Everything is written into the encrypted content archive: the asset index (GUID, path, type and import settings of every asset, so GUID references stay valid), every asset, bytecode per script GUID, and the project file without its development-only settings (`scripts`, `buildProfiles`).
4. **Host** (`GamePackager`). Runs `dotnet publish` on `src/Engine/MNVTEngine.GameHost` for the profile's RID with `--artifacts-path Library/Build/<profile>/artifacts`, which keeps builds separate from each other and from the engine's own `bin`/`obj`.
   - The host is renamed to the game (`-p:MnvtGameName`) and carries the content key (`-p:MnvtContentKey`). Release builds ship no `.pdb` files.
   - Debug builds include the in-game debug UI (`-p:MnvtDevTools=true`: `MNVTEngine.DebugUI`, ImGui.NET, `cimgui.dll`); Release builds leave it out unless the project sets `diagnostics.includeInRelease`. The manifest records `devTools`, and validation fails with MNVB508 if debug UI files appear in a build without it. See [Runtime diagnostics](../engine/diagnostics.md#build-modes).
   - Publishing also copies a private .NET runtime from the runtime pack into `Dependencies/Runtime`.
   - The packager then moves every other managed assembly to `Dependencies/Managed` and every native library to `Dependencies/Native`, and removes `deps.json`.
   - The game's C# assemblies go into the archive under `Scripts/`, except assemblies the host already ships, because the host's copy is always used at run time.
5. **Manifest.** Writes `<Game>_Data/package.json`: name, profile, configuration, target, engine version, the scripts assembly, and the size and SHA-256 of every file.
6. **Validation** (`BuildValidator`). Validates the staged game (see [Validation](#validation)).
7. **Output.** The previous build is first moved aside in one step, which fails cleanly if a file in it is in use (the game is running, or a terminal is open in the folder). Then the staged game is moved in, and the previous build is deleted. If anything fails, the previous build is put back.
8. **Test run** (`--run [N]`, optional). Starts the exported game for N frames. The game must exit with code 0.

## Output layout

```text
Builds/release/
├── MyGame.exe                   apphost; double-click to play
├── MyGame.dll                   the game host (MNVTEngine.GameHost renamed); carries the content key
├── MyGame.runtimeconfig.json
├── Dependencies/
│   ├── Managed/                 MNVTEngine.*.dll, Newtonsoft.Json, Silk.NET.*, StbImage*, Microsoft.Extensions.*
│   ├── Native/                  glfw3.dll, soft_oal.dll (OpenAL Soft, LGPL)
│   └── Runtime/                 private .NET runtime
│       ├── host/fxr/<version>/hostfxr.dll
│       └── shared/Microsoft.NETCore.App/<version>/   coreclr.dll, System.*.dll, ...
├── MyGame_Data/
│   ├── content.mnvtpak          encrypted: asset index, assets, .mnvts bytecode, C# game code (+ .pdb in Debug)
│   └── package.json             manifest with the hash of every file
└── Logs/game.log                written at run time
```

How each part is found at run time:

- **Runtime.** The apphost is built with `AppHostDotNetSearch=AppRelative` and `AppHostRelativeDotNet=Dependencies/Runtime`, so it only looks for .NET in `Dependencies/Runtime` and never uses a .NET installed on the machine. If that folder is removed, the game refuses to start. It does not fall back to an installed .NET.
- **Managed and native dependencies.** `Program.Main` in the host uses only the base class library. It first installs resolvers for `Dependencies/Managed` and `Dependencies/Native` (`HostLayout`), then calls into engine code.
  Native libraries in `Dependencies/Native` are loaded up front, because Silk.NET's GLFW loader probes fixed paths next to the executable and reuses an already loaded library.
- **Content.** The host opens `<its assembly name>_Data/content.mnvtpak`. Renaming the `.exe` does not break the game.

Size is about 89 MB, mostly the .NET runtime. Trimming is not enabled because component discovery and serialization use reflection.

## Content encryption

`content.mnvtpak` is a single archive with one AES-256-GCM encrypted, authenticated entry per file. The entry list (index) is encrypted too.

- **Compression.** Entries are deflated when that makes them smaller.
- **Nothing on disk.** Reading is random-access, in memory only. Nothing is extracted to disk, and C# game code is loaded from bytes into its collectible load context.
- **Integrity.** A modified byte anywhere is detected. A damaged index or wrong key stops the game with exit code 2. A damaged entry fails only when it is read, with an error naming the entry. `mnvt validate` reads and authenticates every entry.
- **Reproducibility.** Nonces are derived from the key, the entry path and a hash of the entry's content. Identical input gives an identical archive, and a nonce is never reused for different data.
- **Key.** The key is `Settings/content.key` (32 random bytes, hex). It is created by `mnvt new`, or by the first build with warning `MNVB107`. **Commit it**: then every machine builds identical content, and patches stay compatible. Delete it to rotate the key.

**What this protects, and what it does not:**

- **It does:** assets, scripts and C# game code cannot be browsed, copied or swapped with normal tools, and modified content is rejected.
- **It does not:** the key must ship inside the game, so the game can decrypt its own content. Anyone willing to reverse-engineer `MyGame.dll` can recover the key and decrypt everything.
- **No real fix exists.** This is true of every game engine's asset encryption, Unity's included, and no client-side scheme can avoid it.
- **Keep secrets out of the game.** Never put secrets such as server credentials or API keys in game content or game code.

## Running an exported game

- `MyGame.exe` runs the content in `MyGame_Data/`. It never compiles anything, and it needs neither the .NET SDK, an installed .NET runtime, nor the editor. The build tests run the game with no `dotnet` on `PATH`.
- `.mnvts` scripts run from the build-time bytecode. Invalid bytecode disables that mission runner with an error ("rebuild the game"). It never falls back to the source.
- Optional arguments: `--frames N`, `--screenshot file.png`, `--debug-gl`, `--profile report.txt` (see [profiling](../engine/profiling.md)).
- Exit codes:

  | Code | Meaning |
  |---|---|
  | 0 | OK |
  | 1 | GPU resources leaked |
  | 2 | Content missing, damaged or not decryptable |
  | 3 | The game's C# code failed to load |

- The log goes to the console (Debug builds) and to `Logs/game.log`. If that folder is not writable, a warning is printed and there is no log file.

### Platform requirements

- Windows 10/11 x64.
- A GPU and driver with **OpenGL 4.5**. `opengl32.dll` and the GPU driver come from Windows and the vendor; they are not packaged and cannot be.
- Nothing else needs to be installed: the .NET runtime, `glfw3.dll` and `soft_oal.dll` ship in `Dependencies/`. Without an audio device the game runs silently.

## Validation

`BuildValidator` runs these checks after every build, and on demand with `mnvt validate <exported game>`:

| Code | Check |
|---|---|
| MNVB501/502 | `<Game>_Data/package.json` exists and loads (schema version 1; newer versions are rejected) |
| MNVB503 | `<exe>.exe`, `.dll` and `.runtimeconfig.json` are in the root |
| MNVB504 | The private runtime is complete: `hostfxr`, `hostpolicy`, `coreclr`, `System.Private.CoreLib` and the framework `deps.json` for the version in the runtime config |
| MNVB505 | Every assembly reference of the host and of `Dependencies/Managed` resolves to the private runtime or `Dependencies/Managed`; every shipped assembly is valid |
| MNVB506 | Required native libraries for the target are in `Dependencies/Native` (`glfw3.dll`, `soft_oal.dll` on win-x64) |
| MNVB507 | No development-only assemblies (editor, build tooling, ImGui, MSBuild, Roslyn) |
| MNVB508 | Every file matches its manifest size and SHA-256 |
| MNVB509 | Warning for files that are not part of the build (`Logs/` is ignored) |
| MNVB514 | The archive opens with the key embedded in the executable (or the given key) |
| MNVB515 | Every archive entry decrypts and authenticates |
| MNVB510 | The packaged project loads, and its startup scene exists |
| MNVB511 | Every script asset has bytecode that passes the verifier |
| MNVB512 | The asset index loads, and every indexed asset is in the archive |
| MNVB513 | The C# scripts assembly named in the manifest is in the archive |
| MNVB520 | `--run`: the game started, rendered N frames and exited with code 0 |

## Diagnostic codes

| Range | Stage |
|---|---|
| MNVB1xx | Project, profile, output folder and key (`100` load, `101` unknown profile, `102` configuration, `103` target, `104` output folder, `105` name clash, `106` engine not found, `107` key created, `108` invalid key file) |
| MNVB2xx | Content (`209` created a missing `.meta`, `201` asset index, `202` unreadable, `203` import settings, `204`/`205` import errors and warnings, `206` missing referenced asset, `207`/`208` scene and prefab loading) |
| MNVB3xx, CSxxxx | C# scripts (`301` scripts project missing; compiler errors keep their `CS` codes and source positions) |
| MNV1xxx–3xxx | `.mnvts` errors from the compiler; see the [language reference](../scripting/language.md) |
| MNVB4xx | Packaging (`401` host project missing, `402` dotnet not found, `403` publish failed, `404` native dependencies of game code not packaged, `409` previous build in use) |
| MNVB5xx | Validation (table above) |

## Reproducible builds

The build uses deterministic compilation (`Deterministic`, `ContinuousIntegrationBuild`), a fixed file order, content-derived archive nonces and a committed key. The manifest contains no timestamps or machine paths. As a result, building the same sources twice gives an identical manifest, including the hash of every file and of the archive. This holds for an incremental rebuild and for a rebuild after deleting `Library/Build`, and `PackagingTests.Rebuild_IsByteForByteIdentical` checks it.

## Engine location

Tooling builds games from the engine **sources**, which it finds in one of two ways:

- `MNVT_ENGINE_ROOT`, if set.
- Otherwise, the first parent folder of the running tool that contains `MNVTEngine.sln`.

The first build of each project and profile compiles the engine into `Library/Build/<profile>/artifacts`. Later builds are incremental and take about 5–10 s.

## Limitations

- Only `win-x64` is supported. Other runtime identifiers are rejected (`MNVB103`).
- If the game's C# scripts use NuGet packages with native libraries, those libraries are not packaged. The build warns with `MNVB404`.
- The archive is read into memory entry by entry, and the build holds each asset in memory while writing it. This suits 2D games; streaming very large assets would need chunked entries.
- `.mnvts` sources are stored (encrypted) next to their bytecode, so the asset index stays complete. The runtime never reads them.
- There is no installer, icon or version resource, and no code signing.
