# Virtual file system and `.mnvtpack` packages

Exported games read their content from `.mnvtpack` packages through an engine-owned virtual file system (VFS), not
from a project folder. Development keeps working from the project folder, through the same VFS. This page covers the
audit that led to the design, the VFS API, the binary package format, compression, encryption, signatures, the build
pipeline, the separation of exported games from projects, diagnostics, measurements, and what the protection does
and does not achieve.

Code: `src/Engine/MNVTEngine.IO` (VFS and package format, no engine references), `MNVTEngine.Assets.AssetDatabase`
(asset reads), `MNVTEngine.Runtime` (`GameContent`, `GamePackage`, `EmbeddedPackageKey`, export guard),
`MNVTEngine.Build` (`ContentPackageWriter`, `ContentKeys`, `SigningKeys`, `BuildIdentity`, `PackageTools`,
`PackageBenchmark`, `BuildValidator`), `MNVTEngine.CLI` (`mnvt package ...`). Tests: `tests/MNVTEngine.IO.Tests`,
`tests/MNVTEngine.Build.Tests` (`PackagingTests`, `BuildPipelineTests`).

## 1. Audit: where the runtime read files before

The audit (November 2026) looked for every place runtime code touched the file system. The engine already funnelled
almost all game content through one class, so the VFS replaced storage underneath it instead of rewriting loaders.

| Consumer | How it read content | Now |
|---|---|---|
| Scenes (`SceneLoadOperation`, `SceneManager`) | `AssetDatabase.ReadAllBytes(path)` | VFS, unchanged call site |
| Prefabs, text assets (`PrefabProvider`) | `AssetDatabase.ReadAllBytes(guid)` | VFS |
| Materials, shaders (`ResourceCache`) | `AssetDatabase.ReadAllBytes(guid)` | VFS |
| Textures (`ResourceCache`, `SceneLoadOperation` workers, environment maps, window icon) | `AssetDatabase.ReadAllBytes(guid)` | VFS (worker threads) |
| Models (`ModelCache`) | `AssetDatabase.ReadArtifact(guid, "model")` | VFS: `Artifacts/<guid>/model` |
| Audio clips (`AudioSystem`) | `scene.AssetReader` → `AssetDatabase.ReadAllBytes` | VFS |
| Fonts (`GameRuntime.Loading`, UI) | `AssetDatabase.ReadAllBytes(guid)` | VFS |
| `.mnvts` scripts and bytecode (`MissionRunner`) | `ITextAssetReader`, `AssetDatabase.ReadCompiled` | VFS: `Bytecode/<guid>.mnvtb` |
| UI documents (`UIDocument`) | `AssetDatabase.ReadAllBytes(guid)` | VFS |
| Browser `mnvt://game/` (`BrowserModule`) | `AssetDatabase.ReadAllBytes(guid)` for paths under `Web/` | VFS (see [CEF](#cef)) |
| Precompiled SPIR-V (`VulkanGraphicsDevice` `PrecompiledShaders`) | `ContentArchiveSet.TryRead` | `GameContent.TryRead` → VFS |
| Game C# code (`GameHost.LoadScripts`) | `ContentArchiveSet.TryRead("Scripts/...")` | VFS, trusted mounts only |
| Project settings (`GameProject.LoadPackaged`) | `<Name>.mnvtproj` entry in the archive | `RuntimeConfig.json` entry (not a project file) |
| Asset index (`AssetDatabase.OpenPackaged`) | `assets.json` entry | VFS |
| Window icon (`GameApplication`) | archive, or `File.ReadAllBytes` in development | VFS / unchanged in development |

Files the runtime still reads directly, by design (they are not game content): the build manifest and its signature
(`<Game>_Data/package.json`, `package.sig`), `logging.json`, logs and crash reports, the driver's pipeline cache, the
debug UI layout, system fonts for the debug UI, native libraries and assemblies resolved by `HostLayout`, and the
editor's own work (importers, `.meta` files, `Library/`). Importing, writing metas and the asset cache are editing work
and stay on the project folder.

What existed before this work: content was already split per level (Unity-style) into `.mnvtpak` archives, each entry
encrypted whole with AES-256-GCM and deflated, one key embedded in plain hex in the host. That format had no chunking
(a 200 MB entry was read whole), no signatures, no per-package keys, no compression choice, no VFS, and the packaged
project was a `.mnvtproj` file inside the archive. `.mnvtpak` is gone; builds made with it must be rebuilt (the
manifest schema went from 1 to 2 and version 1 is refused with that message).

## 2. The virtual file system

```csharp
namespace MNVTEngine.IO;

public interface IVirtualFileSystem
{
    Stream OpenRead(string path);
    ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default);
    bool FileExists(string path);
    bool DirectoryExists(string path);
    bool TryGetFile(string path, out VirtualFileInfo file);     // path, length, asset GUID
    IEnumerable<string> EnumerateFiles(string directory, bool recursive = true);
    string? FindAsset(Guid assetGuid);                           // packaged assets by GUID
}

var vfs = new VirtualFileSystem();
var dlc = vfs.MountPackage("DLC1.mnvtpack", new PackageOpenOptions(keys, [publicKey], RequireSignature: true),
                           new MountOptions(Priority: 10));
var bytes = await vfs.ReadAllBytesAsync("Assets/Textures/UI/MainMenu.png", cancellationToken);
vfs.Unmount(dlc);
```

- **Providers** (`IFileSystemProvider`): `PackageFileSystemProvider` (a `.mnvtpack`), `PhysicalFileSystemProvider`
  (a folder: development), `MemoryFileSystemProvider` (tests, generated content, overrides). A separate "development
  provider" was not needed: development mounts the project's folders with the physical provider.
- **Mounting.** Mount points place a source under a virtual folder; higher `Priority` wins, and among equal priorities
  the latest mount. `Unmount` takes the handle or the display name. The file system owns mounted providers.
- **Paths** (`VirtualPath`): `/` separators, no leading `/`, no empty or `.` segments. `..` is resolved and refused when
  it leaves the root, as are rooted paths (`C:\`, `/x`, `\\server`), `:` (drives, alternate data streams), control
  characters and segments ending in `.` or space (Windows would alias them). Lookups ignore case on every platform
  (ordinal, case-insensitive), as on the Windows file systems games are made on, so a game behaves the same from a
  package and from a folder.
- **Threads.** Lookups work on an immutable snapshot of the mount table; mounting and unmounting swap it under a lock.
  A package is closed only after its last open stream is disposed. A lookup that races an unmount retries against the
  current table.
- **Errors.** `FileNotFoundException` (missing), `ArgumentException` (invalid path), `PackageException` (an
  `IOException` with a `PackageError`: `Malformed`, `UnsupportedVersion`, `IntegrityFailure`, `MissingKey`, `WrongKey`,
  `InvalidSignature`, `UntrustedSignature`).
- **Internals stay inside.** Offsets, chunks, nonces and keys never leave the package provider. Gameplay code sees
  `AssetRef`s, `AssetDatabase` and paths, never the VFS's storage.

`AssetDatabase` reads all asset data through `AssetDatabase.Content`: in exported games, the mounted packages; in
development (editor, Player, `mnvt run`), the project's `Assets` folder mounted at `Assets/` and
`Library/Artifacts` at `Artifacts/`. New: `AssetDatabase.OpenRead(guid)` (a stream, for large or streamed assets) and
`ReadAllBytesAsync(guid)`.

## 3. The `.mnvtpack` format (version 1.0)

Little-endian throughout. Designed for random access: the header and index are read when a package opens; file data
is read chunk by chunk when it is read, never the whole package.

```text
offset 0     Header (320 bytes, fixed)
320          Chunk data: every chunk of every file, stored back to back
info         Info block: public properties (plain)
index        Index block (encrypted when the package is)
signature    Signature block (when signed)
```

### Header

| Offset | Size | Field |
|---|---|---|
| 0 | 8 | Magic `MNVTPACK` |
| 8 | 2 | Major format version (1). Readers refuse other majors (`UnsupportedVersion`) |
| 10 | 2 | Minor version (0). New minors may only use reserved space; readers accept them |
| 12 | 4 | Header size (320) |
| 16 | 4 | Flags: 1 encrypted, 2 signed; other bits refused |
| 20 | 4 | Chunk size (4 KiB to 16 MiB; builds use 64 KiB) |
| 24 | 16 | Package GUID |
| 40 | 16 | Build id |
| 56 | 8 | Encryption key id (0 when not encrypted) |
| 64 | 32 | Key salt (HKDF salt of this package's data key) |
| 96 | 8 + 4 | Info offset, info length |
| 108 | 4 | Index plain length |
| 112 | 8 + 4 | Index offset, index stored length |
| 124 | 12 + 16 | Index nonce and authentication tag (encrypted packages) |
| 152 | 32 | SHA-256 of the info block |
| 184 | 32 | SHA-256 of the stored index block |
| 216 | 8 + 4 | Signature offset, signature length (0 when unsigned) |
| 228 | 60 | Reserved (zero) |
| 288 | 32 | SHA-256 of bytes 0..288 (the header's own checksum) |

The layout is strict: info starts after the header, the index right after the info, the signature right after the
index, all within the file; every offset is checked before it is used.

### Info block

`u16 count`, then `count` pairs of (`u16 length`, UTF-8 key, `u16 length`, UTF-8 value), sorted by key. Builds write
`kind` (globals, level, ...), `scene`, `game`, `target`, `profile`, `engine`. Readable without a key
(`mnvt package inspect`).

### Index block

```text
u32 entryCount, u32 chunkCount, u32 dependencyCount, u32 stringPoolLength
entries[entryCount]        88 bytes each
chunks[chunkCount]         80 bytes each
dependencies[]             16-byte asset GUIDs
string pool                UTF-8 paths
```

| Entry field | Size |
|---|---|
| path offset, path length (in the pool) | 4 + 4 |
| flags, reserved | 4 + 4 |
| asset GUID (zero for non-assets: bytecode, shaders, code) | 16 |
| length | 8 |
| first chunk, chunk count | 4 + 4 |
| first dependency, dependency count | 4 + 4 |
| SHA-256 of the content | 32 |

| Chunk field | Size |
|---|---|
| offset, stored length, plain length | 8 + 4 + 4 |
| codec (0 none, 1 LZ4, 2 Zstandard), flags, reserved | 1 + 1 + 2 |
| nonce, authentication tag (encrypted packages) | 12 + 16 |
| SHA-256 of the stored bytes | 32 |

Rules the reader enforces (`PackageIndexReader`): the sections add up exactly to the index size; every chunk lies in
the data region; a stored chunk is never larger than its plain size (codec 0: equal; compressed: smaller), and no
chunk is larger than the chunk size; a file's chunk count is `ceil(length / chunkSize)`, every chunk but the last is
full and together they hold exactly the file; paths are valid UTF-8, canonical virtual paths, and unique (as are
non-empty GUIDs); dependency ranges are in range. Anything else is `Malformed`. A fuzz test mutates indexes 1500
times (with fixed-up hashes, so the parser and not the hash check sees the damage) and accepts only
`PackageException`s.

Dependencies are asset GUIDs, so they can point into other packages. Files with identical content share their chunks
(the writer detects duplicates by content hash). Paths are kept in the index, encrypted when the package is, so a
package can be inspected by its owner; there is no human-readable name anywhere in an encrypted package.

### Reading a chunk

1. Read the stored bytes at the chunk's offset (`RandomAccess`, synchronous or asynchronous).
2. SHA-256 must match the index: corrupted or modified data is rejected before it reaches a decryptor or decompressor.
3. Encrypted: AES-256-GCM decryption with the chunk's nonce and tag; associated data is the package GUID and chunk
   index, so a chunk cannot be moved to another place or package.
4. Decompress into the caller's buffer, which must come out at exactly the plain size.

`PackageEntryStream` is seekable, keeps one decoded chunk (pooled buffers, no allocation per read), and decodes
straight into the caller's buffer when a read covers a whole chunk.

### Versioning

Major version changes are breaking and refused. Minor versions may add fields in reserved header space or new flags
(old readers refuse unknown flags, so a feature that changes reading is never silently ignored). Information that is
not needed to read content goes into the info block's key/value pairs, which old readers ignore.

## 4. Compression

Measured on 16.5 MB of real sample content (Showcase3D, SceneDemo, Platformer, UIShowcase, Browser3D assets, model
artifacts and managed assemblies), 64 KiB chunks, a chunk kept compressed only when that saves at least 5%:

| Codec | Stored | Decompression |
|---|---|---|
| LZ4 HC level 9 | 57.3% | 803 MB/s |
| LZ4 fast | 62.6% | 719 MB/s |
| Zstandard 3 | 49.4% | 557 MB/s |
| Zstandard 9 | 47.5% | 595 MB/s |
| Zstandard 19 | 45.1% | 517 MB/s |
| Deflate (optimal) | 49.1% | 446 MB/s |
| Brotli 11 | 42.6% | 198 MB/s |

Profiles (`"compression"` in a build profile, `PackageCompression`): `none`; `fast` = LZ4 HC (fastest decoding);
`balanced` = Zstandard 9 (default: nearly the smallest, decodes at about 600 MB/s); `small` = Zstandard 19 (a few
percent smaller, builds compress at a few MB/s). Brotli was smaller still but decodes 3x slower. Both codecs are pure
managed (ZstdSharp.Port, K4os.Compression.LZ4: MIT, ahead-of-time compatible); this .NET has no built-in Zstandard.
`"compressionByExtension": { ".wav": "fast" }` overrides the profile per file type. Already-compressed types (PNG,
JPEG, WebP, Ogg, MP3, FLAC, GLB, WOFF, MP4, ...) are stored as is without trying.

`mnvt package benchmark <exported game>` repacks a game's content with every profile and reports size, write time and
read time on the machine at hand (see [Measurements](#9-measurements)).

## 5. Encryption and keys

- **Algorithm.** AES-256-GCM (the BCL's), per chunk and for the index. No custom cryptography.
- **Keys.** The project's master key is `Settings/content.key` (32 random bytes, hex; created by `mnvt new` or the first
  build, committed with the project). It is never used directly: each package derives its own data key,
  `HKDF-SHA256(master, salt, "MNVTPACK v1 data key" || package GUID)`. The salt is `HMAC-SHA256(master, "salt" ||
  package GUID || build id)`. Package GUIDs derive from the build id and the file name, and the build id is a hash of
  everything the build is made from (`BuildIdentity`). So every package of every distinct build has its own key, and
  identical builds are reproducible.
- **Nonces.** Synthetic: `HMAC-SHA256(data key, "chunk" || index || SHA-256(chunk))`, first 12 bytes (the index uses
  "index"). A nonce repeats under a key only for an identical chunk at the same index, whose ciphertext is then
  identical too; different data never shares a nonce.
- **Key ids and rotation.** The header names the master key by id (the first 8 bytes of SHA-256 of a label and the
  key), so readers pick the right key from a `PackageKeyRing` and report a missing key by id instead of failing to
  decrypt. `mnvt package keys <project> --rotate` creates a new key and keeps the old one as
  `Settings/content.key.<id>.retired` for tools; shipped games keep working with the key they were built with.
- **What metadata is plain.** The header (sizes, GUIDs, key id, salt) and the info block (`kind`, `game`, `target`,
  ...). The bootstrap needs nothing more: the key id selects the key, the salt derives the data key, and the index is
  then decrypted and authenticated (wrong key: `WrongKey`; no key: `MissingKey`). File names, sizes and asset GUIDs are
  in the encrypted index.
- **How the game gets its key.** It is compiled into the game host as assembly metadata (`MNVTEngine.PackageKey`),
  masked: `EmbeddedPackageKey` XORs it with a mask derived from a per-build salt, so it does not appear as a
  recognisable string. This is obfuscation. Anyone who reads the engine code can unmask it, and the plaintext exists in
  the game's memory while it runs. Ahead-of-time builds compile the value into native code; that is not stronger.
- **Platform protected storage** (DPAPI, keychains) protects secrets per user or machine; a key that must decrypt the
  same packages on every player's machine cannot be bound to one machine, so it does not apply to offline games.
- **Server-assisted keys** fit the design (the reader takes any `PackageKeyRing`), but nothing provisions keys from a
  server yet, and offline games never need it. A game with an online backend could keep content packages' keys
  server-side and add them to the key ring after login; the game host would need a hook to defer opening those
  packages until then.
- **Unencrypted packages** (`"encrypt": false` in a build profile) are still signed and verified; useful to inspect a
  debug build with other tools.

## 6. Integrity and signatures

Checksums, encryption and signatures do different jobs:

| Mechanism | Detects | Does not |
|---|---|---|
| SHA-256 of header, info, index and every chunk | Damage and modification, before decoding | Stop someone who rewrites the hashes too |
| AES-GCM tags | Modification of encrypted data | Help without the key (the key ships) |
| ECDSA P-256 signature over the header | Any modification by someone without the private key: the signed header holds the info and index hashes, and the index holds every chunk hash | Hide anything |

- **Scheme.** ECDSA over NIST P-256 with SHA-256 (IEEE P1363, 64 bytes), from the BCL. Ed25519 would be the first
  choice, but this .NET has no Ed25519, and the alternatives (BouncyCastle, libsodium) would add a large managed or
  native dependency to every game. The signature block is `u16 algorithm (1)`, `u16 length (64)`, the SHA-256 of the
  signer's public key (SubjectPublicKeyInfo), the signature. It signs header bytes 0..288, which include the signature
  offset and length, so stripping or moving the signature breaks it, and clearing the "signed" flag fails the policy.
- **Keys.** Signing keys are PKCS#8 PEM files. The **development key** is per machine
  (`%LOCALAPPDATA%\MNVTEngine\Keys\development-signing-key.pem`, created by the first build). The **release key** is
  passed with `mnvt build --signing-key key.pem` or `MNVT_SIGNING_KEY`, and created with
  `mnvt package keys <project> --new-signing-key <file outside the project>`. Release builds signed with the development
  key warn (MNVB114). A key inside `Assets/` is refused (MNVB113), and validation fails if any shipped content holds a
  private key (MNVB518). Private keys never ship; the game embeds only the public key
  (`MNVTEngine.PackageVerificationKey`).
- **Policy.** Exported games require every package and the manifest to be signed by the embedded key
  (`RequireSignature`); unsigned, unknown-signer or invalid packages stop the game with exit code 2. Tools open
  packages with any policy: `mnvt package verify` reports an unknown signer as unverified authenticity.
- **Signed build manifest.** `<Game>_Data/package.json` lists each package's GUID and header hash and the build id, and
  `package.sig` holds its signature. The game verifies the manifest, opens exactly the listed packages and refuses any
  whose GUID, header hash or build id differ, so a package cannot be replaced, even by a validly signed one from
  another build.
- **Determinism.** ECDSA signatures are randomized, so the 64 signature bytes differ between otherwise identical
  builds. Everything else is reproducible, and the manifest identifies packages by header hash (which covers all but
  the signature), so the manifest of identical inputs is byte-identical.

## 7. Build pipeline

`mnvt build` (see [Building](../build-system/building.md)):

1. Resolve the profile, the modules and the content key; resolve the signing key.
2. Compile C# game code (JIT or AOT backend).
3. Compile content: import, compile `.mnvts` to bytecode, load scenes and prefabs, follow dependencies, strip unused
   assets (usage analysis), precompile shaders.
4. Compute the build id; write packages (`ContentPackageWriter`): `globals` (runtime configuration, asset index, C#
   code), `defaultresources` (SPIR-V), `levelN`, `sharedassets`, `resources`. Each file gets its asset GUID and
   dependencies; chunks are compressed, encrypted, hashed; each package is signed.
5. Publish the host with the masked key and the public key; the private runtime; crash handler; browser host.
6. Write and sign the manifest; validate everything (`BuildValidator`); swap the output in.

`mnvt package build <project>` runs steps 1-4 and writes the packages and a signed manifest without the host
(`Builds/<profile>-packages`), for inspecting what a build ships.

What never ships: the `.mnvtproj` (its runtime settings ship as `RuntimeConfig.json`, without `scripts` and
`buildProfiles`), `.meta` files (GUIDs travel in the encrypted asset index), source model files (their imported
artifacts ship), `Library/`, `Settings/` and keys, editor, importer, model-import, shader-compiler and build assemblies
(MNVB507), unused assets (stripped) and unused modules (MNVB517). `.mnvts` sources ship next to their bytecode
(encrypted) so the asset index stays complete; the runtime only reads bytecode.

## 8. Development and release

| | Development (editor, Player, `mnvt run`) | Exported game |
|---|---|---|
| Content source | Project folders through the VFS (`Assets/`, `Artifacts/`) | Signed, encrypted packages through the VFS |
| Names | Human-readable paths | Same paths, inside the encrypted index |
| Hot reload, importers, debug info | Available | Not shipped (debug UI only with the `devtools` module) |
| Signature policy | None | Manifest and every package must be signed by the embedded key |
| Package testing | `mnvt package build`, `inspect`, `verify`, `benchmark` | - |

### Exported games are not projects

`GameProject.Classify(path)` decides by content, not by a missing file name: a folder with a `*_Data` folder holding a
build manifest, a game executable with its data folder, a runtime configuration (`"kind": "mnvt.runtimeConfig"`, also
when renamed to `.mnvtproj` or extracted from a package) are exported games; a folder of `.mnvtpack` files or a
package file is packaged content. `GameProject.Load`/`FindProjectFile` throw `NotAProjectException` (a
`FileNotFoundException`) for those, so the editor's Project Hub, its command line and `mnvt` report "... is an exported
game (a game build), not an editable MNVTEngine project ... open the original project folder instead." Even with the
check bypassed, an extracted build is not the original project: there are no `.meta` files (opening it would assign
new GUIDs and break every reference), no C# sources, no build profiles, and models are imported artifacts. This is a
separate goal from protecting assets: it stops a build from posing as a project, not someone determined to rebuild one.

## CEF

Game web pages are assets under `Assets/Web`. `mnvt://game/<path>` maps to `Web/<path>`
(`BrowserSecurityPolicy.TryGetGameAssetPath`, which refuses anything outside `Web/`); the game reads the file through
`AssetDatabase` (so through the VFS and the packages) and sends the bytes to `MNVTBrowserHost.exe` over its pipe,
where `GameSchemeHandlerFactory` serves them. CEF never receives a file path, a package offset or file-system access.

## Mods and extra content

At startup, after the official packages, the game mounts extra content the runtime configuration allows
(`"content"` in the project file):

| | DLC | Mods |
|---|---|---|
| Where | `<Game>_Data/DLC/*.mnvtpack` | `Mods/*.mnvtpack` next to the executable |
| Switch | `"content": { "dlc": true }` (default on) | `"content": { "mods": true }` (default off) |
| Trust | Trusted: must be signed with the game's key | Untrusted: any or no signature, never serves `Scripts/` |
| Encryption | The project key, or a key of its own (below) | None (the game has no key for it) |
| Priority | 10 (over the game) | 100 (over everything) |

- **Asset indexes.** Extra packages carry `Index/<name>.json`; `AssetDatabase` merges every one over the game's own
  index (a GUID listed again takes the new path and settings; a path overridden by a higher mount serves the new
  bytes), so new assets load by GUID and path like the game's. Mods can therefore replace game assets, but never code.
- **Failures** are logged and skip that package; the game still starts.
- **Mod load order.** Mods load by file name, or in the order of `Mods/load-order.txt` when it exists: one package
  file name per line, `#` starts a comment, and only listed mods load. A later mod overrides files of an earlier one
  (same priority; the later mount wins). Missing or invalid entries are reported and skipped.
- **Mod manifest.** A mod may carry `mod.json` at its root: `{ "name": "Hats", "version": "1.0", "author": "...",
  "description": "...", "requires": ["Base >= 1.2"] }` (strings of 1-256 characters, at most 64 KiB). A mod whose required
  mod is not loaded before it, or has a version outside the constraint, is skipped. Constraints use `=`, `>=`, `>`,
  `<=`, `<` on versions of 1-4 numbers (`1.2` equals `1.2.0`); a constraint is never met by a non-numeric version. `requires` names a mod's manifest `name`, or its file name without extension
  when it has none. The startup log shows each mod's name and version; `ExtraContentResult.Mod` holds the manifest.
- **Authoring.** `mnvt package dlc <project> --folder Assets/DLC/Forest --name Forest` packages one folder's assets
  (models as artifacts, scripts with bytecode, textures with proxies) and its index, signed and encrypted. The main
  build strips those assets when nothing in the game references them.
- **DLC code.** C# for a DLC lives in a class library that references the game's scripts project, listed in the
  project file: `"content": { "dlcCode": { "Forest": "DLC/Forest/Forest.Scripts.csproj" } }`. `mnvt package dlc --name
  Forest` builds it and sets the package properties `dlc` (the name) and `scripts` (the assembly's file name).
  - **JIT games:** the assembly ships in the DLC as `Scripts/<Assembly>.dll`, next to the game's code. The game host
    loads it into the game's code context (so it sees the game's types) and registers its components, at startup or
    when `DownloadableContent.Mount` mounts the DLC later. DLC is trusted (signed with the game's key); mods never
    serve `Scripts/`. Only the main assembly is packaged: DLC code cannot bring NuGet packages of its own.
  - **Ahead-of-time games** cannot load code: the build compiles every `dlcCode` project into the executable and
    generates `GameComponents.RegisterDlc`, which registers a DLC's components once that DLC is mounted. Their DLC
    packages carry no assembly, only the properties. DLC whose code the executable does not contain (released after the
    game) logs error 7005: its content loads, its components do not, until a game update.
  - Extra content mounts before the game's code loads, so DLC code is registered before the first scene. A DLC
    assembly that fails to load is logged (`LOAD001`) and the game runs on.
- **Server-provided keys.** `--separate-key` encrypts the DLC with a key of its own, written next to the package for
  the game's server, never shipped. The game finds the package, reports it as waiting for its key
  (`DownloadableContent.WaitingForKey`), and mounts it when game code passes the key it got from the server:
  `DownloadableContent.Mount("Forest.mnvtpack", key, out var problem)`. A wrong key or a package not signed with the
  game's key is refused. Offline games never need this.
- `.mnvts` bytecode in any package runs in the sandboxed VM (no file, process or network access) and is verified when loaded.
- Not implemented: per-mod capability restrictions beyond code, and DLC code for ahead-of-time games released after
  the game itself.

## Streaming and memory

- **Audio.** Long clips are decoded while they play from a stream over the package (see [audio](../engine/audio.md#resource-lifetime)).
- **Textures.** Builds write, for every texture larger than 128 pixels, a 64-pixel proxy (`Artifacts/<guid>/proxy`,
  raw RGBA) and its reduced levels (`Artifacts/<guid>/mip1`, `mip2`, ...: the image at 1/2, 1/4, ... of its size, PNG,
  each box-filtered from the one above, down to the last level above 128 pixels). Exported games show the proxy at
  once and then load the level the screen needs (below); the level replaces the image inside the same `Texture2D`
  (`IGraphicsDevice.ReplaceTexture`), which keeps reporting its full size, so sprite sizes, source rectangles,
  materials and models that hold it stay valid. Scene loading does not wait for those textures. Development loads
  textures whole. Packages built before reduced levels existed switch between the proxy and the full image.
- **Level by drawn size.** Streamed textures have `Texture2D.TrackScreenScale` set, and renderers report how large
  they draw them (`ReportScreenScale`, the largest over the last 30 to 60 frames): sprites from their quad's pixel size
  against the source rectangle, 3D meshes from pixels per world unit at the mesh's nearest point against texels per
  world unit (the mesh's UV0 density, `GpuMesh.UVDensity`, times the texture size). The resource cache then keeps the
  smallest level at least as large as drawn: the proxy alone when the drawn size fits it, else level
  `floor(log2(1 / scale))`, else the full image. A texture drawn without a size report (custom draws, meshes without
  UVs) gets its full image. Sharper levels load on a worker; a coarser level replaces a sharper one once it loaded, the
  proxy at once. A texture drawn by several renderers keeps the largest size any of them reported.
- **Meshes.** Static meshes with levels of detail ship their full-detail geometry as its own entry, read on a worker
  only while a mesh is drawn up close and released 5 seconds after (see [models](../engine/models.md#levels-of-detail)).
- **Use tracking.** The Vulkan device stamps `Texture2D.LastDrawnFrame` whenever a texture is bound for drawing.
- **Memory pressure.** Every 15 frames the resource cache checks its budget (`"streaming": { "textureBudgetMB": N }`,
  0 = none), the system's memory load (the GC's high-load threshold) and the GPU's remaining budget
  (`VK_EXT_memory_budget`, under 128 MB). Loading a sharper level waits while it would exceed the budget or memory is
  short. Over it, textures not drawn for `"unusedSeconds"` (default 10) return to their proxy, least recently drawn
  first; drawing one again streams it back in. Textures in use keep the level their drawn size needs, so a budget
  smaller than one frame's textures is exceeded rather than thrashed.
- **Unloading.** After a scene change (once `unusedSeconds` passed), textures not drawn since are unloaded with the
  materials and models that use them (`ResourceCache.UnloadUnused`); they load again on use.
- **Lifetime.** Textures, materials and models are cached per runtime and released by these rules or when their asset
  changes; audio clips per scene. There is no explicit reference counting: drawing is the reference.

## 9. Measurements

`mnvt package benchmark <exported game>` (2026-11-13, Windows 11, 12 threads, NVMe, warm OS file cache on both
sides; release exports, Balanced, encrypted). "Plain files" are the same files on disk read through the physical
provider, the development path.

| Showcase3D (31 files, 3.86 MB; packages 2.65 MB) | Packages | Plain files |
|---|---|---|
| Open all packages (header, index, signature) | 2.75 ms | - |
| Models (5 artifacts, 1.07 MB) | 5.30 ms | 0.77 ms |
| Textures (7 files, 2.67 MB, already compressed: stored) | 3.58 ms | 1.68 ms |
| Scene (0.04 MB) | 0.19 ms | 0.14 ms |
| SPIR-V (11 files) | 0.96 ms | 1.50 ms |
| Everything, one thread | 10.6 ms | 5.3 ms |
| Everything, 12 threads | 3.4 ms | 1.9 ms |
| 2000 random 4 KB reads in a 1 MB file | 124.8 ms | 9.8 ms |
| 10000 lookups (exists + size) | 11.3 ms | 437 ms |

| Platformer (22 files, 0.13 MB) | Packages | Plain files |
|---|---|---|
| Everything, one thread | 1.4 ms | 2.8 ms |
| Audio (3 files) | 0.41 ms | 0.39 ms |

Compression profiles on the same content (repacked, unencrypted): Showcase3D None 3.19 MB / Fast 2.77 MB / Balanced
2.65 MB / Small 2.61 MB, all read in 4-10 ms; Platformer 0.14 / 0.07 / 0.07 / 0.07 MB.

What the numbers say:

- Reading whole assets costs about 2x the raw file read for compressible data (hash, decrypt, decompress at several
  hundred MB/s); for small files packages are faster than opening files (one open handle, in-memory index). Either way
  it is milliseconds per scene, and it runs on the loading threads, not the frame.
- Lookups are 40x faster than asking the file system.
- Small random reads are the weak case: each touches a 64 KiB chunk that must be decoded (about 60 µs). Streams keep
  the last decoded chunk, so sequential and nearby reads are cheap; a game that does many tiny random reads into big
  assets should read whole chunks or keep such files uncompressed (`compressionByExtension`).
- Memory: reading everything allocates the result arrays plus about 1% (streams use pooled chunk buffers); a package
  keeps only its index in memory.
- The codec comparison on a larger 16.5 MB corpus is in [Compression](#4-compression).

Not measured here: GPU upload and image decoding (identical on both paths), and cold-cache disk reads (packages read
fewer bytes, as they are smaller).

## 10. Diagnostics

The runtime diagnostics' `Content` provider (debug UI, `metrics` console command) reports: mounted sources, packages,
package content size, files (unsupported when a folder is mounted: counting would scan it), open streams, pending
asynchronous reads, bytes read and chunks decoded per frame, chunk cache hits, verification, decryption and
decompression time per frame (from every thread), total package opening time, failed reads and corrupted chunks.
Cached assets and their memory are the existing `assets.loaded` and `memory.assets` metrics of the resource cache.
Loading and streaming: `assets.loadTime` and `assets.loadTimeMax` (last 32 texture loads), `scene.loadTime`,
`textures.proxies`, `textures.fullResolution`, `textures.reduced`, `textures.pending`, `textures.downgrades`, `assets.unloaded`,
`memory.streamedTextures`; audio: `audio.streams`, `audio.underruns`.

## 11. What this protects, and what it does not

- **Does:** content cannot be browsed, copied or swapped with ordinary tools (file names are encrypted too); any
  modification is detected and stops the game; packages cannot be replaced by others, even validly signed ones; an
  exported game cannot be opened as an editable project, and contains no project file, metas, sources or keys of the
  developer.
- **Does not:** make extraction impossible. A game that runs offline must be able to decrypt its own content, so the
  key ships with it; anyone who reverse-engineers the executable can recover it, and anyone can capture decoded
  textures, meshes and audio from memory or from the GPU while the game runs. Signatures stop tampering by people
  without the private key, not someone who patches the verification out of the executable. Treat everything in a game
  as public; never put server credentials or other secrets in game content or code.

## 12. Remaining work

- Texture levels replace the whole image (the new level is uploaded with its own mipmaps), rather than adding or
  dropping mip levels of a resident image; 2D render targets drawn at a different size than the back buffer report a
  scale relative to the back buffer.
- Mesh streaming covers static meshes with levels of detail only (see [models](../engine/models.md#levels-of-detail)).
- Ahead-of-time games run only DLC code they were built with; JIT games load DLC code but not its NuGet dependencies.
- Linux and macOS targets do not exist yet (no GLFW/OpenAL/Vulkan-loader packaging or runtime packs for them); the VFS
  and package format are platform-independent.
