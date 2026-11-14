# Engine versioning

Every engine build has one version, `MAJOR.MINOR.PATCH.BUILD`, generated from one configuration file, the build date,
the Git history and a build counter. All assemblies, packages, manifests and reports of one logical build carry the
same version and the same build id.

```text
1.1411.140.4-dev+sha.45dbb7891093.dirty
│ │    │   │  │       │            └ built from a modified working tree
│ │    │   │  │       └ engine commit (12 hex digits)
│ │    │   │  └ prerelease label of the channel (none for release)
│ │    │   └ BUILD: build number of the pipeline build (0: development build)
│ │    └ PATCH: commits in the Git history (git rev-list --count HEAD)
│ └ MINOR: build date as DDMM (14 November)
└ MAJOR: engine generation, set by hand
```

The date component is not a SemVer minor version: it is MNVTEngine's own scheme. Version numbers do not grow
monotonically across days (`1.0301...` after `1.3112...`). PATCH, the commit count, is what orders builds over time.

## Components

| Component | Source | Notes |
|---|---|---|
| MAJOR | `major` in `eng/version.json` | Only maintainers change it, for a new engine generation or a deliberate breaking change. It is never derived from dates or commits. |
| MINOR | Build date, `DDMM` | Four digits in readable versions (`0301` for 3 January). Numeric fields drop the leading zero (`301`). The date comes from the build timestamp in `dateTimezone` (default UTC, so builds are reproducible across machines). |
| PATCH | `git rev-list --count HEAD` | Or `--first-parent` with `"patchStrategy": "GitFirstParentCount"` (a merge counts once). Works on any branch and in a detached HEAD. See [without Git](#without-git-metadata). |
| BUILD | Build counter | One number per pipeline build; 0 for development builds. See [build numbers](#build-numbers). |

Each component goes up to 65534, the limit of a .NET `AssemblyVersion` field. A PATCH above that fails the build
with a request to raise MAJOR or count first-parent commits.

## Configuration: `eng/version.json`

This is the only place version settings live; no project file holds a version.

```json
{
  "major": 1,
  "minorStrategy": "DateDDMM",
  "patchStrategy": "GitCommitCount",
  "buildStrategy": "Incremental",
  "dateTimezone": "UTC",
  "buildCounterScope": "VersionIdentity",
  "channel": "development",
  "prerelease": null,
  "missingGit": "Fallback"
}
```

| Setting | Values |
|---|---|
| `major` | 0 to 65534 |
| `minorStrategy` | `DateDDMM` |
| `patchStrategy` | `GitCommitCount`, `GitFirstParentCount` |
| `buildStrategy` | `Incremental` (local counter; CI can supply the number) |
| `dateTimezone` | `UTC` or a system time zone id (IANA such as `Europe/Berlin`, or Windows) |
| `buildCounterScope` | `VersionIdentity`: one counter per `MAJOR.MINOR.PATCH`, so numbers restart at 1 on a new day or commit. `Global`: one counter for every build. |
| `channel` | `development`, `nightly`, `preview`, `beta`, `release`: the channel of pipeline builds |
| `prerelease` | `null` for the channel's label (`dev`, `nightly`, `preview`, `beta`, none for `release`), or a label such as `beta.1` |
| `missingGit` | `Fallback` (PATCH 0, marked, see below) or `Error` |

The file is validated before any version is generated. Unknown or mistyped settings, invalid values, duplicate keys
and malformed JSON fail the build, with every problem listed (error `MNVB120` in `mnvt build`, `MNVV001` from the
generator).

## Development builds and pipeline builds

| | Development build | Pipeline build |
|---|---|---|
| How | `dotnet build`, `dotnet test`, the IDE, `scripts\mnvt.cmd` | `mnvt build`, `mnvt package build`, `mnvt package dlc`, `scripts\build.cmd`, CI |
| BUILD | 0, nothing allocated | Next number of the counter, or CI's |
| Label | `dev` | The configured channel's |
| Build id | Derived from the source state; the same until the source changes | New for every build |
| Timestamp | When this source state was first built | Start of the build |
| Rewrites the version | Only when the source state changes: commit, date, dirty state | Every build |

Development builds stay incremental. Their version files are only rewritten when the source state changes: a new
commit, a new day, or a clean tree becoming dirty. Otherwise nothing recompiles. A build number in every development
build would rebuild every project every time.

Example, three exports of a game on 14 November at commit 140:

```text
mnvt build MyGame   ->  1.1411.140.1-dev
mnvt build MyGame   ->  1.1411.140.2-dev
mnvt build MyGame   ->  1.1411.140.3-dev
git commit; mnvt build MyGame   ->  1.1411.141.1-dev    (VersionIdentity scope: a new identity starts at 1)
dotnet build (editor)           ->  1.1411.141.0-dev
```

## Build numbers

- **One allocation per logical build.** `mnvt build` allocates when it starts and passes the session to every
  `dotnet publish` it runs: game host, engine assemblies, crash handler and browser host. The packages, manifest and
  build report get the same session. MSBuild never allocates, so nested builds and parallel projects cannot take
  extra numbers.
- **The state lives in `artifacts/build-state/build-counter.json`.** Git ignores this folder, and
  `MNVT_BUILD_STATE_DIR` can move it. It holds the last number of each scope and the last 200 attempts: build id,
  version, kind (`game:<name>`, `packages:<name>`, `dlc:<game>/<name>`, `engine`), start and end time, and result.
- **Parallel builds never collide.** Allocation takes an exclusive lock on `build-counter.json.lock` (other processes
  wait up to a minute), reads the counter, increments it and writes it back atomically.
- **Results.** A build is recorded as `started` when it allocates, then as `succeeded`, `failed` or `canceled`. A
  build that never reports back, such as a killed process, stays `started`. Its number is never reused.
- **A damaged state file is reported, not reset.** Resetting it could hand out the same numbers twice. Delete the
  file to restart the numbers on purpose.
- **CI supplies authoritative numbers** with `MNVT_BUILD_NUMBER`. The local counter is then not used.

## Version, build and environment identity

| | Fields | Same for two builds when... |
|---|---|---|
| Version identity | MAJOR, MINOR, PATCH, BUILD, label | the counter or CI gives the same number |
| Build identity | build id (UUID), timestamp, commit, dirty state | never, except in reproducible mode |
| Build environment | host OS and architecture, .NET SDK, MSBuild, runtime, CI provider and run id; for games also configuration, target, profile, scripting and graphics backend, modules | the same machine and settings build it |

The build id tells apart two builds with the same four-part version, for example two CI runs that were both given
build number 12.

## What carries the version

### Assemblies

Every project under `src/` gets the session's values, through `Directory.Build.targets`.

| .NET field | Value | Example |
|---|---|---|
| `AssemblyVersion` | `MAJOR.0.0.0` | `1.0.0.0` |
| `FileVersion` | `MAJOR.MINOR.PATCH.BUILD`, numeric | `1.301.140.4` (3 January) |
| `InformationalVersion` (Windows: ProductVersion) | version, label, source | `1.0301.140.4-dev+sha.45dbb7891093.dirty` |
| `Version` (MSBuild, NuGet) | file version and label | `1.301.140.4-dev` |
| `AssemblyMetadata` | `MNVTEngine.BuildId`, `MNVTEngine.Commit`, `MNVTEngine.BuildTimestamp` | |

`AssemblyVersion` stays the same within a MAJOR generation, so game code compiled against one engine build binds to
another without redirects. Exactly one numeric field carries each part.

Tests and samples are not engine assemblies. They keep the SDK's defaults.

### Runtime API

MNVTEngine.Core compiles the session's `EngineVersion.g.cs`:

```csharp
EngineVersion.Version              // "1.1411.140.4-dev"
EngineVersion.Major / Minor / Patch / Build, MinorText ("1411")
EngineVersion.InformationalVersion // "1.1411.140.4-dev+sha.45dbb7891093"
EngineVersion.CommitHash, Branch, CommitSubject, IsDirtyBuild, SourceProblem
EngineVersion.BuildId, BuildTimestampUtc, BuildConfiguration, Channel, Prerelease
EngineVersion.IsDevelopmentBuild   // Build == 0
EngineVersion.Summary, Describe()  // one line, and the block that mnvt --version prints
EngineVersion.MetadataJson         // the complete metadata
```

The same metadata ships as `MNVTEngine.version.json` next to `MNVTEngine.Core.dll` in development outputs. Tools can
read it without loading the engine.

### Exported games

Games carry their engine build. Nothing at run time needs Git or the repository.

- **Executable.** The executable, the crash handler and every engine assembly carry the `FileVersion` and
  `ProductVersion` of the export's session. With the AOT backend this means the native executables.
- **Manifest.** The signed `package.json` holds `engineVersion` and `engine`: version, file version, build id,
  commit, dirty state, timestamp and channel.
- **Packages.** Every `.mnvtpack` has the properties `engine` (version) and `engineBuild` (build id).
  `mnvt package inspect` shows them.
- **Build report.** `build-report.json` holds `engineVersion` and `engine`, the complete metadata including the
  build context: configuration, target, profile, scripting and graphics backend, modules.
- **Logs.** `game.log` starts with
  `MNVTEngine 1.1411.140.4-dev+sha.45dbb7891093, build 7b4e..., built 2026-11-14 13:00:00 UTC (Release)` and
  `MyGame (release), built with MNVTEngine 1.1411.140.4-dev, jit, win-x64`.
- **Crash reports.** They name the engine version with its build id.

The game's content build id (`buildId` in the manifest, which derives package GUIDs and keys) depends on the engine's
source revision, not on its build number. Unchanged content keeps its identity across builds.

## Command line

```text
> mnvt --version
MNVTEngine 1.1411.140.4-dev

Commit:        45dbb7891093
Branch:        main
Build:         4
Build ID:      7b4e2c1d-...
Built:         2026-11-14 13:00:00 UTC
Configuration: Release
Channel:       development
Dirty:         false
```

- `mnvt version` prints the same block.
- `mnvt version --json` prints the complete metadata.
- `mnvt version --verbose` adds the assembly fields, the build host, and what the next build of the engine tree would
  get: configuration, live Git state and the last recorded build.

The generator also runs as a command, used by `eng/Version.proj`, `scripts/build.cmd` and CI:

```text
dotnet artifacts/versioning-tool/MNVTEngine.Versioning.dll resolve --root . --out <session> [--allocate] [--configuration Release]
dotnet artifacts/versioning-tool/MNVTEngine.Versioning.dll complete --root . --session <session> --result succeeded
dotnet artifacts/versioning-tool/MNVTEngine.Versioning.dll show --root . [--json]
```

## Editor

**Help > About MNVTEngine** shows the version, build number, commit, branch, build date, configuration, build id and
dirty state. It has a button that copies them for bug reports. The Player (`mnvt run`) logs the same first line as a game.

## MSBuild integration

- `MNVTEngine.Versioning` (`src/Tooling`) is the generator. It references no engine project, so it can run before
  the engine is built. `eng/Version.proj` builds it into `artifacts/versioning-tool/`, with its own intermediate
  folder so it never races the solution's build of the same project. It is rebuilt only when its sources change.
- `Directory.Build.targets` calls `eng/Version.proj` for every project under `src/`, through the MSBuild task with
  the same global properties. MSBuild runs a target once per project instance in a build, so a solution build, a
  single project with its references, and parallel nodes all get one result.
- **Development builds.** `eng/Version.proj` runs the generator into `artifacts/version/dev/<Configuration>/`,
  rewriting files only when they change.
- **Pipeline builds.** The build passes `-p:MnvtVersionSession=<folder>` (or the `MNVT_VERSION_SESSION` environment
  variable), and the session is only read.
- A session folder holds `version.json` (metadata), `version.props` (MSBuild properties) and `EngineVersion.g.cs`.
  Never edit generated version files.

**Engine pipeline build.** `scripts\build.cmd [Debug|Release] [dotnet build arguments]` allocates a session, builds
the solution with it and records the result.

## CI overrides

Each environment variable overrides the matching setting. `BuildOptions.VersionVariables` sets them for one
`GameBuilder` build.

| Variable | Effect |
|---|---|
| `MNVT_BUILD_NUMBER` | Authoritative BUILD (1 to 65534); no local counter |
| `MNVT_VERSION` | All four components, `MAJOR.DDMM.PATCH.BUILD` (release automation) |
| `MNVT_PATCH` | PATCH where Git cannot count (shallow clone, source archive) |
| `MNVT_BUILD_TIMESTAMP` | Build time, ISO 8601. MINOR follows it. |
| `SOURCE_DATE_EPOCH` | Build time in Unix seconds (the reproducible-builds convention) |
| `MNVT_BUILD_ID` | The build id (GUID) |
| `MNVT_CHANNEL`, `MNVT_PRERELEASE` | Release channel, prerelease label |
| `MNVT_REPRODUCIBLE` | `true`: see below |
| `MNVT_BUILD_STATE_DIR` | Folder of the build counter |
| `MNVT_CI_PROVIDER`, `MNVT_CI_RUN_ID` | CI name and run id, for CIs that are not detected |

The CI provider and run id are detected for GitHub Actions, Azure Pipelines, GitLab, Jenkins and TeamCity. Only these
two values are recorded: no other environment variable, no secret and no path reaches the metadata.

The repository's own CI is `.github/workflows/build.yml` (windows-latest). Every push to `main` and every pull
request runs `scripts\build.cmd Release` with `MNVT_BUILD_NUMBER` set to the run number, then the test suite. The
hosted runners have no Vulkan 1.4 GPU, so GPU tests and the export tests that launch the game skip there. Running
the workflow by hand (Actions, "Build", "Run workflow") also publishes a GitHub release `v<version>` on the chosen
channel. Its asset `MNVTEngine-<version>-win-x64.zip` holds the source tree at that commit plus the prebuilt editor and
CLI (`bin/Release`). Exports compile GameHost from those sources, so users still need the .NET SDK. The zip has no
`.git` folder, so engine builds made from it get PATCH 0 unless they set `MNVT_PATCH` ([Without Git metadata](#without-git-metadata)).
Releases are made only by hand: nothing tags or publishes on its own.

The minimal form for another pipeline:

```yaml
- uses: actions/checkout@v4
  with: { fetch-depth: 0 }            # full history: PATCH is the commit count
- run: scripts\build.cmd Release
  env:
    MNVT_BUILD_NUMBER: ${{ github.run_number }}
    MNVT_CHANNEL: nightly
```

## Release channels

Channels and labels change only the label, never the four numbers:

```text
1.1411.140.4-dev       development (default)
1.1411.140.4-nightly   MNVT_CHANNEL=nightly
1.1411.140.4-beta.1    MNVT_CHANNEL=beta MNVT_PRERELEASE=beta.1
1.1411.140.4           MNVT_CHANNEL=release
```

A release build that must have an exact version passes `MNVT_VERSION` (and `MNVT_CHANNEL=release`). The versioning
system never publishes, never creates Git tags, and never commits or pushes.

## Reproducible builds

`MNVT_REPRODUCIBLE=true` requires a fixed time (`MNVT_BUILD_TIMESTAMP` or `SOURCE_DATE_EPOCH`) and, for pipeline
builds, a fixed number (`MNVT_BUILD_NUMBER` or `MNVT_VERSION`). The build id is then derived from the version,
commit, dirty state and build context. Two builds of the same source with the same inputs give identical metadata.
Together with the deterministic compiler and content pipeline, a re-export is byte for byte identical
(`PackagingTests.Rebuild_IsByteForByteIdentical`). No other timestamp goes into compiled code or assets.

## Without Git metadata

| Situation | Result |
|---|---|
| No `.git` (source archive), or no `git` on PATH | PATCH 0, commit `unknown`, informational version `+source.unknown`, and the reason in the metadata (`source.problem`), in `mnvt version` and as build warning `MNVB121`. With `"missingGit": "Error"` the build fails instead. |
| Shallow clone | The commit is known, but the count is not trusted. PATCH falls back as above, with "fetch the full history, or set MNVT_PATCH". |
| `MNVT_PATCH` set | Used as PATCH, and the metadata says so |
| Detached HEAD | Counted normally. The branch is the CI's (`GITHUB_REF_NAME`, `BUILD_SOURCEBRANCHNAME`, `CI_COMMIT_REF_NAME`, `BRANCH_NAME`) or `(detached)`. |
| Modified working tree, including untracked files that are not ignored | `dirty: true`, `.dirty` in the informational version, "modified working tree" in `mnvt --version` and the About window. A dirty build is never shown as an exact revision. |

The generator only reads the repository: it uses `git rev-parse`, `rev-list`, `symbolic-ref`, `log` and `status` with
`GIT_OPTIONAL_LOCKS=0`. It never writes to the repository.

## Migration

- **Before this system, every assembly was `1.0.0.0`.** Manifests of earlier exports have no `engine` object; the
  runtime reads them as before (`EngineBuildStamp` is optional).
- **A new engine generation** is a change of `major` in `eng/version.json`, committed like any other change.
  `AssemblyVersion` becomes `<major>.0.0.0`; games are rebuilt against the new engine.
- **Changing the counter scope** to `Global` continues from 0 in a scope of its own. Numbers of the old scope stay
  in the state file.

Code: `src/Tooling/MNVTEngine.Versioning`, `eng/Version.proj`, `Directory.Build.targets`,
`src/Engine/MNVTEngine.Core/EngineVersion.cs`, `src/Tooling/MNVTEngine.Build/PipelineVersion.cs`. Tests:
`tests/MNVTEngine.Versioning.Tests` (dates, configuration, Git cases, counter concurrency, overrides, MSBuild
integration) and `tests/MNVTEngine.Build.Tests/EngineVersionExportTests.cs` (2D, 3D and AOT exports, CLI).
