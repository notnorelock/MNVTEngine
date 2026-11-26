# Content bundles (`.mnvtbundle`)

A content bundle is content made outside the game (by players or creators) and loaded while the game runs: a world,
an avatar, a decoration. It is the engine's counterpart of a Unity asset bundle, built for untrusted content: it
carries no code, cannot replace anything the game ships, and its prefabs create only the components the game allows.
The same file type also carries [editor packages](../editor/packages.md) (kind `mnvt.package`): kits with code that
people import into projects in the editor, never loaded by games.

Code: `src/Engine/MNVTEngine.Bundles` (`ContentBundleFile`: format, policy, checks; no rendering or scenes, so servers
and backends can check bundles), `MNVTEngine.IO.PrefixedFileSystemProvider`, `MNVTEngine.Assets.AssetDatabase.MountBundle`,
`MNVTEngine.Scenes` (`ContentBundles`, `ContentBundle`, `ComponentRegistry.Where`), `MNVTEngine.Runtime.ContentBundleHost`,
`MNVTEngine.Build.BundleBuilder`, `mnvt package bundle`, the editor's prefab menu. Tests: `MNVTEngine.Bundles.Tests`, `Build.Tests` (`BundleBuilderTests`),
`IO.Tests` (`PrefixedFileSystemProviderTests`), `Assets.Tests` (`AssetDatabaseBundleTests`), `Scenes.Tests`
(`RestrictedRegistryTests`).

## Format

A `.mnvtpack` package (see [virtual file system](virtual-filesystem.md)), unsigned, with exactly:

| Entry | Contents |
|---|---|
| `bundle.json` | `{ "schemaVersion": 1, "kind": "borealise.world", "name": "Lounge", "root": "<guid>", "properties": { "sdk": "1" } }` |
| `bundle-index.json` | `{ "schemaVersion": 1, "assets": [{ "guid", "path", "type", "importer", "settings" }] }` |
| `Assets/<path>` | Each asset's file, at its path inside the bundle |
| `Artifacts/<guid>/<name>` | Imported forms of the bundle's own assets (optional) |

- **Kind** says what the bundle is for, in the game's terms; a game loads only the kinds it expects.
- **Root** is the prefab the content starts from; the game instantiates it.
- **Properties** are up to 64 short strings (names up to 64 characters, values up to 256), for tools and games: SDK
  version, author, a game-specific format version.

## Encryption

Creators build plain bundles; the server that delivers them may encrypt them (AES-256-GCM, the package format's own
encryption: index and every chunk, so names, structure and data are unreadable, and any change fails to open).
`ContentBundleFile.Write(..., key)` writes one, `ContentBundleFile.Open(file, policy, key)` and
`ContentBundles.Load(file, options, key)` read one: the file stays encrypted on disk and is decrypted as it is read.
`ContentBundlePolicy.RequireEncrypted` refuses plain bundles, for games whose server always encrypts. How keys reach
the game is the game's business (Borealise: a key per version, given to signed-in players by its backend).

## What a bundle may hold

`ContentBundleFile.Open(file, policy)` checks everything and reports **every** problem at once
(`ContentBundleException.Problems`):

- Only the entries above. Anything else (`Scripts/`, `Bytecode/`, another top folder) refuses the bundle: no C#, no
  `.mnvts` bytecode, nothing the engine would treat as code.
- Assets of the types the policy allows. The default (`ContentBundlePolicy.DataTypes`) is plain data: textures, audio,
  fonts, prefabs, JSON, text, animations, materials, animator controllers, particle systems. Scripts are refused
  whatever the policy says. Scenes, models, shaders, UI layouts, terrain and web content are not in the default set; a
  game that wants them adds them to `AllowedAssetTypes` knowingly.
- Bounded: `MaxBytes` (unpacked files, default 64 MB) and `MaxAssets` (default 2048); manifest and index at most 4 MB.
- Consistent: every asset listed is in the package, GUIDs and paths are unique, artifacts belong to listed assets, the
  root is a prefab in the bundle, and the kind is the one the policy asks for.

## Loading at run time

```csharp
var options = new ContentBundleLoadOptions(
    new ContentBundlePolicy { Kind = "borealise.world", MaxBytes = 32 * 1024 * 1024 },
    AllowComponent: type => type == typeof(Tilemap) || type == typeof(SpriteRenderer) || type == typeof(SceneDescriptor));
using var bundle = ContentBundles.Load(downloadedFile, options);
var instance = bundle.Instantiate(scene, parent);
foreach (var problem in instance.Problems) { Log.Warning(problem); }
```

- **Isolation.** `AssetDatabase.MountBundle` mounts the package with a `PrefixedFileSystemProvider`: its `Assets/`
  appear under `Assets/Bundles/b<n>/` and its `Artifacts/` next to the game's, at a lower priority, untrusted. Its
  manifest and index are not visible. The bundle's assets live in a layer of their own: they load by GUID like the
  game's, refreshes and the asset cache never see them, and a bundle that lists a GUID the database already has (the
  game's or another bundle's) is refused, so a bundle can never replace a game asset.
- **References.** A bundle's prefabs may reference the game's own assets by GUID (an SDK's shared tilesets, say):
  those are read from the game, not packaged.
- **Components.** `ContentBundle.Instantiate` reads the root prefab with `ComponentRegistry.Where(AllowComponent)`: a
  registry with only the allowed types and `Transform`. A component of any other type stays an inert
  `MissingComponent` (its data kept, nothing runs) and is reported in `ContentInstance.Problems`. Allow components that
  are data or presentation, never ones that act for the player (network, input, scripting).
- **Unloading.** `ContentBundle.Dispose` removes the bundle's assets and closes the file once open streams close;
  caches are told through `AssetsChanged`. Destroy its instances first.
- Works in development (Player, editor play mode) and in exported games alike.

## Building

```bash
mnvt package bundle <project> --root Assets/Worlds/Lounge.prefab --kind borealise.world --name Lounge --provided BorealiseSdk
```

or, in the editor, **Build content bundle...** in a prefab's menu in the Project panel (built in the background; the
result is logged). `BundleBuilder` packages the root prefab and everything it references, except assets in the
`--provided` folders (relative to Assets, comma separated), which the game already ships. References are read from the
assets themselves (`asset:` strings in prefabs, clips, materials and other JSON documents), so nothing has to be
imported first. A referenced script or an
asset type the policy refuses fails the build (`MNVB123`); a reference to an asset not in the project warns
(`MNVB124`); a missing or non-prefab root, or a bad kind or name, is `MNVB122`. The file goes to `Builds/bundles/`
(or `--output`), named after the bundle. Its package identity comes from the content, so rebuilding an unchanged
bundle gives the same file; the builder opens the written file with the policy before reporting success.

## Not implemented

- Signatures for bundles (a creator or server key): games trust a bundle's origin through the server that delivered it
  (and, when it encrypts, through its key: GCM refuses changed data).
- Per-type limits (texture dimensions, audio length) beyond the total size.
- Editor-side validation of components against what a particular game allows (the game reports them when loading).
