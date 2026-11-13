# Building and Exporting Games

The `mnvt` command creates, validates, runs and exports projects. It is independent of the editor: it never
references editor code and only starts the editor as a separate process for `mnvt editor`.

```bash
scripts\mnvt.cmd new MyGame --template basic-2d     # or --profile 2d|3d; or: dotnet run --project src/Tooling/MNVTEngine.CLI -- new ...
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
| `build <project> [--profile p] [--configuration c] [--target t] [--output dir] [--signing-key key.pem] [--run [N]]` | Exports a standalone game. |
| `package build <project> [--profile p] [--output dir]` | Builds only the content packages and their signed manifest (`Builds/<profile>-packages`). |
| `package inspect <file.mnvtpack> [--project p] [--entries]` | Header, signature, encryption, contents and sizes of a package. Contents need the key: the project's (`--project`) or that of the game the package sits in. |
| `package verify <file.mnvtpack \| exported game> [--project p] [--trust key.pem]` | Checks signatures and every chunk; for a game folder, runs the full validation. |
| `package analyze <project> [--profile p]` | What each package of a build would hold: sizes before compression, duplicate content, stripped assets. |
| `package extract <file.mnvtpack> --output dir [--project p]` | Developer tool: writes a package's files to a folder. The result is not a project and cannot be opened as one. |
| `package keys <project> [--rotate] [--new-signing-key file.pem]` | Shows the encryption key id and signing keys; rotates the encryption key; creates a release signing key (outside the project). |
| `package benchmark <exported game> [--project p] [--no-repack]` | Read times from the packages against the same files on disk, per asset type, and each compression profile. |

## Templates

| Id | Contents |
|---|---|
| `empty-2d` | 2D: a camera in an empty scene. |
| `empty-3d` | 3D: a perspective camera, a directional light and ambient light. |
| `basic-2d` | 2D: a player moved by a C# component, a goal sprite, and a `.mnvts` mission that completes when the player reaches the goal. |

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
| `compression` | `balanced` | Package compression: `none`, `fast` (LZ4), `balanced` (Zstandard 9), `small` (Zstandard 19). See [compression](../architecture/virtual-filesystem.md#4-compression). |
| `compressionByExtension` | none | Per file type, e.g. `{ ".wav": "fast" }`. Already-compressed types (PNG, Ogg, ...) are stored as is. |
| `encrypt` | true | Encrypt the packages. Off writes plain packages (still signed and verified), e.g. to inspect a debug build. |

How the profile is chosen:

1. `--profile` picks a profile by name.
2. Otherwise `--configuration` picks the first profile with that configuration. If there is none, a profile is made for it.
3. Otherwise the `release` profile is used.

After that, `--configuration` and `--target` override the chosen profile's values.

## What a build does

The steps run in this order. Every step reports diagnostics as `file(line,col): error CODE: message`, and the build stops at the first step with errors.

1. **Project.** Loads and validates the project file and its profiles, then checks the output folder.
   The build refuses output folders that overlap the project, `Assets` or `Library`. It also refuses non-empty folders that do not contain a previous build, so a typo in `--output` cannot delete your files.
   It also loads the content key from `Settings/content.key` and the signing key (see [Packages, encryption and signing](#packages-encryption-and-signing)).
2. **C# scripts.** `dotnet build -c <configuration>` into `Library/Build/<profile>/Scripts`. The compiled components are loaded so that scenes can be checked against them, then unloaded.
3. **Content** (`ContentCompiler`). Asset files that have no `.meta` yet (for example, added with the file explorer) get one, as they would in the editor, with warning `MNVB209`: commit it. 3D models are imported into `Library/Artifacts` (`MNVB220` import errors, `MNVB221` warnings); the game ships each model's artifact instead of its source file (`MNVB222` when a model has none). Then assets are opened **read-only**:
   - Every importer runs: images are decoded, JSON is parsed, and so on.
   - Every `.mnvts` script is compiled strictly against the full runtime API. In the editor an unknown API function is only a warning; in a build it is an error (`MNV3014`), because the game would fail to link.
   - Every scene and prefab is loaded with the game's components.
   - Every asset reference must point to an asset in the project.
   - [Usage analysis](../engine/modules.md#usage-analysis) decides which assets ship (reachable from the startup scene, project settings, names in code, and `"alwaysInclude"`) and which Auto modules the game needs. Unused assets are left out and counted in the log; unreferenced scenes, prefabs and audio are reported as warning `MNVB223`.
   - The shipped assets are written into signed, encrypted `.mnvtpack` packages: the asset index (GUID, path, type and import settings of every shipped asset, so GUID references stay valid), the assets (each with its GUID and dependencies), bytecode per script GUID, and `RuntimeConfig.json`: the project's settings without development-only ones (`scripts`, `buildProfiles`), with every optional module listed as on or off as the build resolved them, marked as a runtime configuration so it is never taken for a project.
4. **Host** (`GamePackager`). Runs `dotnet publish` on `src/Engine/MNVTEngine.GameHost` for the profile's RID with `--artifacts-path Library/Build/<profile>/artifacts`, which keeps builds separate from each other and from the engine's own `bin`/`obj`.
   - The host is renamed to the game (`-p:MnvtGameName`) and carries the package key, masked (`-p:MnvtPackageKey`), and the public key packages and manifest must be signed with (`-p:MnvtVerificationKey`). Release builds ship no `.pdb` files.
   - The in-game debug UI is the `devtools` module (`-p:MnvtDevTools=true`: `MNVTEngine.DebugUI`, ImGui.NET, `cimgui.dll`). The project switch `"modules": { "devtools": true | false }` decides; without one, Debug builds include it and Release builds do not. `diagnostics.includeInRelease` from older projects is read as `"devtools": true` and saved as the switch. The manifest records `devTools`, and validation fails with MNVB508 if debug UI files appear in a build without it. See [Runtime diagnostics](../engine/diagnostics.md#build-modes).
   - The host is built with the game's modules only: the build generates `ModuleBootstrap.g.cs` and `.g.props` from the resolved modules and passes them as `-p:MnvtBootstrap` (see [Generated bootstrap](../engine/modules.md#generated-bootstrap)).
   - Publishing also copies a private .NET runtime from the runtime pack into `Dependencies/Runtime`.
   - The crash handler, `MNVTCrashHandler.exe`, is published next to the game: framework-dependent on the private runtime for JIT builds, compiled ahead of time for AOT builds (see [Crash reporting](../engine/crash-reporting.md)). `MNVB405` when that fails.
   - With the `browser` module, `MNVTBrowserHost.exe` is published next to the game on its private runtime (AOT games get a `Dependencies/Runtime` for it: CefSharp needs CoreCLR), CefSharp into `Dependencies/Managed` and CEF with CefSharp's C++/CLI assemblies into `Dependencies/Native/Chromium`, with the `en-US` locale only and the CEF and CefSharp licenses; `build-report.json` gets a `browser` section with the bytes it added. `MNVB406` when that fails. See [Embedded browser](../engine/browser.md#exports).
   - The packager then moves every other managed assembly to `Dependencies/Managed` and every native library to `Dependencies/Native`, and removes `deps.json`.
   - **Framework pruning** (`FrameworkPruner`, on unless the build profile sets `"pruneFramework": false`): a runtime-pack assembly stays when the host, a shipped dependency or the game's C# code references it, directly or through other assemblies. Through a type-forwarding facade (`netstandard`, `mscorlib`) only the assemblies of the forwarded types actually named count. Whole files only, no IL trimming. The runtime itself, `createdump` and the DAC (crash dumps) and the assemblies CoreLib loads by name stay; the alternative GCs and natives whose assembly went are removed. The framework `deps.json` is rewritten to list exactly what remains. Games that load framework assemblies by name at run time turn it off.
   - The game's C# assemblies go into the globals package under `Scripts/`, except assemblies the host already ships, because the host's copy is always used at run time.
   - The packages are completed: index, signature, header.
5. **Manifest.** Writes `<Game>_Data/package.json`: name, profile, configuration, target, engine version, build id, the game's modules, the scripts assembly, each package's GUID and header hash, and the size and SHA-256 of every other file; and its signature, `package.sig`.
6. **Validation** (`BuildValidator`). Validates the staged game (see [Validation](#validation)).
7. **Output.** The previous build is first moved aside in one step, which fails cleanly if a file in it is in use (the game is running, or a terminal is open in the folder). Then the staged game is moved in, and the previous build is deleted. If anything fails, the previous build is put back.
8. **Report.** Writes `<output>.build-report.json` next to the game folder (see [Build report](#build-report)).
9. **Test run** (`--run [N]`, optional). Starts the exported game for N frames. The game must exit with code 0.

## Output layout

```text
Builds/release/
├── MyGame.exe                   apphost; double-click to play
├── MyGame.dll                   the game host (MNVTEngine.GameHost renamed); carries the masked package key and the public signing key
├── MyGame.runtimeconfig.json
├── Dependencies/
│   ├── Managed/                 MNVTEngine.*.dll, Newtonsoft.Json, Silk.NET.*, StbImage*, Microsoft.Extensions.*
│   ├── Native/                  glfw3.dll, soft_oal.dll (OpenAL Soft, LGPL), vma.dll
│   └── Runtime/                 private .NET runtime
│       ├── host/fxr/<version>/hostfxr.dll
│       └── shared/Microsoft.NETCore.App/<version>/   coreclr.dll, System.*.dll, ...
├── MyGame_Data/
│   ├── globals.mnvtpack          runtime configuration, asset index, C# game code (+ .pdb in Debug)
│   ├── defaultresources.mnvtpack engine-generated: precompiled SPIR-V of every shader the game uses
│   ├── level0.mnvtpack           the startup scene and the assets only it uses
│   ├── level1.mnvtpack ...       one per further scene, in path order
│   ├── sharedassets.mnvtpack     assets used by two or more scenes (only when there are any)
│   ├── resources.mnvtpack        assets no scene references: loaded by path/GUID at run time (only when there are any)
│   ├── package.json              build manifest: build id, packages (file, kind, entries, GUID, header hash), hash of every other file
│   └── package.sig               signature of package.json
└── Logs/game.log                written at run time
```

How each part is found at run time:

- **Runtime.** The apphost is built with `AppHostDotNetSearch=AppRelative` and `AppHostRelativeDotNet=Dependencies/Runtime`, so it only looks for .NET in `Dependencies/Runtime` and never uses a .NET installed on the machine. If that folder is removed, the game refuses to start. It does not fall back to an installed .NET.
- **Managed and native dependencies.** `Program.Main` in the host uses only the base class library. It first installs resolvers for `Dependencies/Managed` and `Dependencies/Native` (`HostLayout`), then calls into engine code.
  Native libraries in `Dependencies/Native` are loaded up front, because Silk.NET's GLFW loader probes fixed paths next to the executable and reuses an already loaded library.
- **Content.** The host reads `<its assembly name>_Data/package.json`, verifies its signature, and mounts every package it lists in its [virtual file system](../architecture/virtual-filesystem.md), refusing any package that is not the one listed. Renaming the `.exe` does not break the game. Reads go to whichever package holds the file, so engine code never needs to know the layout.
- **Not a project.** A build has no project file, `.meta` files or sources, and the editor and `mnvt` refuse to open it as a project ("... is an exported game (a game build), not an editable MNVTEngine project").

Size is about 89 MB, mostly the .NET runtime. Trimming is not enabled because component discovery and serialization use reflection.

## Scripting backends

`--scripting-backend jit|aot` (or `"scriptingBackend"` in a build profile) chooses how the game's C# runs:

- **`jit`** (default): IL assemblies on a private .NET runtime, as described above.
- **`aot`**: NativeAOT. One native executable holds the engine modules and the game's C#, with no .NET runtime and no IL; about 25-32 MB instead of about 82 MB for the samples. It needs the MSVC linker on the build machine. Game components are registered from generated code, and reflection-heavy assemblies are preserved whole. Hot reload does not apply.
- `il2cpp` is refused (MNVB111): MNVTEngine has no IL-to-C++ backend, and Unity's IL2CPP cannot be reused.

See [IL2CPP and ahead-of-time compilation](../architecture/il2cpp-aot-pipeline.md).

## Build report

Every build writes `<output>.build-report.json` beside the game folder (it does not ship). `mnvt build analyze <project> [--profile p] [--configuration c] [--json file]` produces the same report without packaging: it compiles the C# scripts, analyses the content and resolves the modules exactly as a build does, and prints it as text.

| Field | Contents |
|---|---|
| `profile`, `buildProfile`, `configuration`, `target`, `engineVersion` | What was built |
| `scriptingBackend` | `jit` (CoreCLR, IL assemblies); see [IL2CPP / AOT](../architecture/il2cpp-aot-pipeline.md) |
| `devTools` | Whether the in-game debug UI ships |
| `modules.enabled` | Each module with `inclusion` (`always`, `explicit`, `automatic`, `dependency`, `available`), reason and usage evidence |
| `modules.excluded` | Each module left out, with the reason |
| `assemblies`, `nativeLibraries` | Included files with size and owning module; files of excluded modules with the reason |
| `framework` | Pruning: assemblies kept, files and bytes removed; `trimming` is always `none` |
| `shaders` | Precompiled stages and their sources (built-in shaders, material paths) |
| `assets` | Shipped assets with size, owning module and what keeps them; stripped assets with size |
| `dynamicDependencies` | What the static analysis cannot see (`MNVB223` notes) |
| `sizes` | `total`, `framework`, `managed`, `native`, `content` in bytes (null for `analyze`) |
| `diagnostics` | Warnings and errors of the build |

## Content layout

Content is split over several packages, like the data folder of other engines, instead of one file holding everything. The build decides where each asset goes from the references its importer finds:

| Package | Contents | Rule |
|---|---|---|
| `globals` | Runtime configuration (`RuntimeConfig.json`), asset index, C# game code | Always; read first |
| `defaultresources` | SPIR-V of the built-in sprite shader and of every material's shader, compiled at build time | Generated by the engine, not authored. The game compiles no shaders at run time, logged as `Shader stages: N precompiled, 0 compiled at run time`. shaderc is only loaded if a shader is missing (for example one built by game code) |
| `levelN` | A scene plus every asset only that scene references, with the bytecode of its scripts | `level0` is the startup scene, then the other scenes in path order. References are followed transitively but stop at other scenes, so a door to the next level does not pull that level's assets in |
| `sharedassets` | Assets referenced by two or more scenes | Only when there are any |
| `resources` | Shipped assets no scene references: prefabs and scenes named in code or scripts, `alwaysInclude` assets | Only when there are any |

- **Index.** The asset index records each asset's package (`"package"`), and `mnvt validate` checks it against where the asset really is, and that each level holds exactly one scene (MNVB516). Scene and file names are only in the encrypted index, never in the plain-text manifest or package headers.
- **Unique entries.** Every file lives in exactly one package, and opening fails if two packages hold the same file.
- **Shader errors fail the build.** Every material's shader is compiled and linked as the device would (MNVB210). The engine's built-in shaders are precompiled too; one failing is reported as MNVB212 (a toolchain problem) instead of stopping the build tooling.
- **Older builds** (`.mnvtpak` archives, manifest schema 1) are refused with "built with an older MNVTEngine; rebuild it".

## Packages, encryption and signing

The format, keys and limits are described in [Virtual file system and packages](../architecture/virtual-filesystem.md). In short:

- **Chunks.** Files are split into 64 KiB chunks, each compressed (profile `compression`), encrypted with AES-256-GCM and hashed on its own. Reading a file decodes only the chunks it touches; nothing is extracted to disk, and C# game code is loaded from bytes into its collectible load context. Identical files are stored once.
- **Integrity.** Every chunk hash is checked before decryption and decompression. Any modified byte is detected; a damaged header, index or wrong key stops the game with exit code 2, a damaged chunk fails when it is read, naming the files that use it. `mnvt validate` and `mnvt package verify` read every chunk.
- **Encryption key.** `Settings/content.key` (32 random bytes, hex), created by `mnvt new` or by the first build with warning `MNVB107`. **Commit it.** Each package derives its own data key from it, the package GUID and the build id. `mnvt package keys <project> --rotate` rotates it (the old key is kept as `.retired` for tools; shipped games are unaffected).
- **Signing key.** Packages and the manifest are signed with ECDSA P-256. Release builds should use your release key: `--signing-key key.pem` or `MNVT_SIGNING_KEY`; create one with `mnvt package keys <project> --new-signing-key <path outside the project>` and keep it private, backed up and out of version control. Without it, builds are signed with this machine's development key (`%LOCALAPPDATA%\MNVTEngine\Keys`), and Release builds warn (MNVB114). The game carries only the public key and refuses anything not signed with it.

**What this protects, and what it does not:**

- **It does:** assets, scripts and C# game code, and their names, cannot be browsed, copied or swapped with normal tools; modified or replaced content is rejected; a build cannot be opened as a project.
- **It does not:** the key must ship inside the game, so the game can decrypt its own content. Anyone willing to reverse-engineer `MyGame.dll` can recover the key and decrypt everything, or capture assets from memory or the GPU. Signatures stop tampering only by people who cannot also patch the executable.
- **No real fix exists.** This is true of every game engine's asset encryption, and no client-side scheme can avoid it.
- **Keep secrets out of the game.** Never put secrets such as server credentials or API keys in game content or game code.

## Running an exported game

- `MyGame.exe` runs the content in `MyGame_Data/`. It never compiles anything, and it needs neither the .NET SDK, an installed .NET runtime, nor the editor. The build tests run the game with no `dotnet` on `PATH`.
- `.mnvts` scripts run from the build-time bytecode. Invalid bytecode disables that mission runner with an error ("rebuild the game"). It never falls back to the source.
- Optional arguments: `--frames N`, `--screenshot file.png`, `--gpu-validation`, `--profile report.txt` (see [profiling](../engine/profiling.md)).
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
- A GPU and driver with **Vulkan 1.4**. `vulkan-1.dll` (the loader) and the GPU driver come from the vendor; they are not packaged and cannot be. Without them the game logs the reason and exits with code 4.
- Nothing else needs to be installed: the .NET runtime, `glfw3.dll` and `vma.dll` (and `soft_oal.dll` when the game uses audio) ship in `Dependencies/`. The shader compiler (shaderc) does not: games run on SPIR-V precompiled at build time, and the validator rejects shaderc in a package (MNVB507). Without an audio device the game runs silently.
- Shaders compile on first run and are cached with the driver's pipeline cache in `%LOCALAPPDATA%\MNVTEngine\GpuCache\<game>`.

## Validation

`BuildValidator` runs these checks after every build, and on demand with `mnvt validate <exported game>`:

| Code | Check |
|---|---|
| MNVB501/502 | `<Game>_Data/package.json` exists and loads (schema version 2; older and newer versions are rejected) |
| MNVB503 | `<exe>.exe`, `.dll` and `.runtimeconfig.json` are in the root |
| MNVB504 | The private runtime is complete: `hostfxr`, `hostpolicy`, `coreclr`, `System.Private.CoreLib` and the framework `deps.json` for the version in the runtime config |
| MNVB505 | Every assembly reference of the host and of `Dependencies/Managed` resolves to the private runtime or `Dependencies/Managed`; every shipped assembly is valid |
| MNVB506 | The native libraries of the game's modules are in `Dependencies/Native` (`glfw3.dll` and `vma.dll`; `soft_oal.dll` with audio; `cimgui.dll` with dev tools). Builds without a module list need all of them |
| MNVB507 | No development-only assemblies (editor, build tooling, ImGui, MSBuild, Roslyn) |
| MNVB508 | Every file matches its manifest size and SHA-256 (packages are checked by identity, MNVB514) |
| MNVB509 | Warning for files that are not part of the build (`Logs/` is ignored) |
| MNVB519 | The executable carries a verification key, and `package.sig` is a valid signature of the manifest by it |
| MNVB514 | Every package listed opens with the executable's key (or the given keys), is signed by the executable's key, is exactly the package listed (GUID, header hash, build id), and no file is in two packages |
| MNVB515 | Every file of every package reads back: chunk hashes, authentication tags, decompression |
| MNVB518 | No shipped content contains a private key |
| MNVB510 | The runtime configuration loads, and its startup scene exists |
| MNVB511 | Every script asset has bytecode that passes the verifier |
| MNVB512 | The asset index loads, and every indexed asset is in the packages |
| MNVB513 | The C# scripts assembly named in the manifest is in the content |
| MNVB516 | Layout: globals first and only once, the index and runtime configuration in globals, each level holds one scene, each asset is in the package the index names |
| MNVB517 | No assembly or native library of a module the game does not have (with dev tools, what the debug UI references is allowed) |
| MNVB520 | `--run`: the game started, rendered N frames and exited with code 0 |

## Diagnostic codes

| Range | Stage |
|---|---|
| MNVB1xx | Project, profile, output folder and key (`100` load, `101` unknown profile, `102` configuration, `103` target, `104` output folder, `105` name clash, `106` engine not found, `107` key created, `108` invalid key file, `109` a project from before [project profiles](../engine/project-profiles.md) mixes 2D and 3D content, `110` warning: building a project from before profiles with its inferred profile, `111` unknown or unavailable scripting backend, `112` signing key unreadable, `113` signing key inside `Assets/`, `114` warning: Release build signed with the development key) |
| MNVB2xx | Content (`209` created a missing `.meta`, `201` asset index, `202` unreadable, `203` import settings, `204`/`205` import errors and warnings, `206` missing referenced asset, `207`/`208` scene and prefab loading, `220`/`221` model import errors and warnings, `222` model without an artifact, `223` warning: an unreferenced scene, prefab or audio clip is not shipped) |
| MNVB3xx, CSxxxx | C# scripts (`301` scripts project missing; compiler errors keep their `CS` codes and source positions) |
| MNV1xxx–3xxx | `.mnvts` errors from the compiler; see the [language reference](../scripting/language.md) |
| MNVB4xx | Packaging (`401` host project missing, `402` dotnet not found, `403` publish failed, `404` native dependencies of game code not packaged, `405` crash handler not published, `409` previous build in use) |
| MNVB5xx | Validation (table above) |
| MNVB6xx | [Engine modules](../engine/modules.md#project-switches): `601`-`604`, `609` catalog errors, `605` module of the other profile, `606` unknown module, `607` required module disabled, `608` content of a module the project does not have, or a used module that is disabled or of the other profile, `610` info: enabled as a dependency |

## Reproducible builds

The build uses deterministic compilation (`Deterministic`, `ContinuousIntegrationBuild`), a fixed file order, a build id hashed from the inputs (package GUIDs, key salts and synthetic nonces derive from it) and a committed key. The manifest contains no timestamps or machine paths. As a result, building the same sources twice gives an identical manifest, including the hash of every file and each package's header hash. The 64 ECDSA signature bytes of each package and of `package.sig` differ between builds (ECDSA signing is randomized); everything else in a package is identical. This holds for an incremental rebuild and for a rebuild after deleting `Library/Build`, and `PackagingTests.Rebuild_IsByteForByteIdentical` checks it.

## Engine location

Tooling builds games from the engine **sources**, which it finds in one of two ways:

- `MNVT_ENGINE_ROOT`, if set.
- Otherwise, the first parent folder of the running tool that contains `MNVTEngine.sln`.

The first build of each project and profile compiles the engine into `Library/Build/<profile>/artifacts`. Later builds are incremental and take about 5–10 s.

## Limitations

- Only `win-x64` is supported. Other runtime identifiers are rejected (`MNVB103`).
- If the game's C# scripts use NuGet packages with native libraries, those libraries are not packaged. The build warns with `MNVB404`.
- `.mnvts` sources are stored (encrypted) next to their bytecode, so the asset index stays complete. The runtime never reads them.
- There is no installer, icon or version resource, and no Authenticode signing of the executable (packages and the manifest are signed; see above).
