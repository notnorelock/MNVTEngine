# Code obfuscation

Exported games can have their compiled C# obfuscated, so a shipped build is harder to read and modify. It is
off by default and turned on per build with `mnvt build --obfuscate` (seed from `MNVT_OBFUSCATION_SEED`).

Obfuscation raises the cost of reading a build. It does **not** make one impossible to reverse: the string
decoder and its key live in the same assembly, and a determined reader can still recover structure. Treat it as a
deterrent, not a lock.

## What it does

The obfuscator ([`MNVTEngine.Obfuscation`](../../src/Tooling/MNVTEngine.Obfuscation), a Mono.Cecil tool vendored) rewrites the game's compiled assembly:

- **Renames** types, namespaces, methods and non-serialized members to meaningless names.
- **Encrypts** string literals in method bodies into one per-module blob, decoded on first use.
- **Flattens control flow**: every method it can prove safe is rewritten into a state machine — its basic blocks are
  emitted in a shuffled order with random state numbers, and a `switch` dispatcher on a `state` local drives them, so
  the IL no longer follows the execution order while computing the same result. Always on.
- **Hides integer constants**: each literal becomes a small arithmetic identity (`(v ^ k) ^ k`, `(v - k) + k` or
  `(v + k) - k`, varied per site) so the value is not a literal in the stream. Always on.

New names are a keyed hash of each symbol's original identity, so the same seed gives the same names every build.
The `--obfuscate-scheme unicode` option draws names from the four accented capital I's (`Ì Í Î Ï`), giving
VRChat-style names such as `ÌÍÌÎÍÌÍÍÍÍÏ`; the default `letters` scheme uses short ASCII names.

## What it keeps

The engine finds parts of a game by name, so those names are never renamed:

- **Serialized members** — public fields (unless `[DontSerialize]`) and `[SerializeField]` members. Scene and
  prefab data stores them by name.
- **Component lifecycle** (`Awake`, `Update`, …) — these are virtual overrides of the engine's `Component`, kept
  automatically because the engine assembly is a reference, not a renamed assembly.
- **Reflection and serializer targets** — names passed to `Type.GetType`, `GetMethod`, `Activator.CreateInstance`
  and the members of types handed to a JSON serializer.
- **`[ComponentId]` / `[ComponentAlias]`** — the scene id a component is saved under is an attribute string, which
  neither renaming nor string encryption touches, so those components need no remap.

## JIT vs. ahead-of-time

| | JIT export | Ahead-of-time (NativeAOT) export |
|---|---|---|
| What ships | the managed assembly (IL) | one native executable, no IL |
| Type names | **renamed**; the shipped scenes and prefabs are remapped to the new ids ([`SceneRemapper`](../../src/Tooling/MNVTEngine.Build/SceneRemapper.cs)) | **kept** (the generated AOT bootstrap names the component types), so scenes are not remapped |
| Members and strings | renamed (the serialized members of components **and the data types nested in them**; scene/prefab data keys are remapped at every depth) / encrypted | renamed (private; serialized member names kept, like type names) / encrypted — hidden in the native build's metadata and data |

For AOT the obfuscator runs **before** the native compile, so ILC compiles the obfuscated IL. The build always
reads the original assembly for asset-usage analysis first, so encrypting string literals never hides the asset
references a game names in code.

## The rename map

Each obfuscated build writes a rename map (old name → new) next to the build, **outside** the game. It reveals
every original name, so it must not ship; keep it with the build that produced it to read a crash log from that
build.

## DLC against an obfuscated game

A JIT game export obfuscated with `--obfuscate` also records its rename map under `Library/Build/Obfuscation`. When a
DLC is then obfuscated, its build **pins** those names: the game is obfuscated alongside the DLC with its symbols
forced to their recorded names, so the DLC's calls into the game's own (renamed) classes, methods and fields are
rewritten to match and resolve at runtime. Build the game before its DLC; a game build that does not obfuscate the IL
clears the map, so a DLC is never pinned against names the shipped game does not use. (AOT DLC code is compiled into
the game's native build, which already protects it.)

Obfuscation needs the engine assemblies as references (passed automatically) to decide what is safe to rename.

See [versioning](versioning.md) for how a build is identified and [the virtual file system](virtual-filesystem.md)
for how content is packaged.
