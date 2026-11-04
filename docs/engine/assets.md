# Assets

`MNVTEngine.Assets` indexes a project's `Assets/` folder. Every asset has a stable GUID that is independent of its path, so renaming or moving an asset never breaks references to it.

## Metadata

Each asset file has a sidecar `<file>.meta`. Commit meta files together with the assets:

```json
{
  "schemaVersion": 1,
  "guid": "0f5e0000-0000-4000-8000-000000000003",
  "settings": { "filter": "linear", "wrap": "clamp", "generateMipmaps": false, "pixelsPerUnit": 16.0 }
}
```

| Situation | Behavior |
|---|---|
| New file | The editor writes a meta with a new GUID and complete default settings. |
| Incomplete or outdated settings | Rewritten with defaults filled in. Unknown keys are dropped with a warning; valid values are kept. |
| Unreadable meta, or no valid `guid` | Reported as an error and the asset is skipped. The meta is never overwritten, because its GUID may still be recoverable. |
| Copied file + meta (duplicate GUID) | The copy gets a new GUID, with a warning. |
| Meta whose asset no longer exists | Reported as an orphan; not deleted automatically. |
| File renamed outside the editor together with its meta | Detected as a move; the GUID is kept. |
| Folders, `*.tmp`, dot-files | Not assets. |

## Asset database

```csharp
var editing  = AssetDatabase.OpenForEditing(assetsDir, libraryDir); // editor: writes metas + Library/AssetDatabase.json
var readOnly = AssetDatabase.OpenReadOnly(assetsDir);               // standalone games: reads metas only, writes nothing
```

Pass `ModelImporters.WithDefaults()` (from `MNVTEngine.Assets.Models`) to import 3D models; the editor, Player and build
do. `OpenReadOnly(assetsDir, artifactsLibrary: libraryDir)` reads imported model artifacts without importing anything.

- **Refresh** rescans the folder. A file is reimported only when its size or modification time changed *and* its SHA-256, normalized settings or importer version differs. A file that was touched but is identical only updates the cache.
- **Import cache:** `Library/AssetDatabase.json` stores each asset's type, status, messages, facts (e.g. texture width and height) and dependencies. It is derived data: if it is corrupt it is rebuilt.
- **Editing operations:** `Move`, `Delete`, `WriteText` and `WriteFiles` (atomic per file, one refresh for the batch), `UpdateSettings` (rewrites the meta, then reimports) and `Reimport`. `AssetsChanged` fires after any change; the texture cache and prefab provider use it to evict stale entries.
- **Dependencies:** importers collect every `"asset:<guid>"` string. `GetDependents` lists the assets that reference a given asset (the editor warns about them before a delete). `CollectDependencies` returns the transitive closure of a set of roots. `mnvt build` currently packages every asset (scripts can load scenes by path, so reachability is not provable) and fails on references to assets that do not exist.
- **Artifacts:** an importer can produce derived files (`AssetImportContext.Artifacts`). They are written to `Library/Artifacts/<guid>/<name>` only when the import succeeds, so a failed reimport keeps the last good one; they are deleted with the asset. `ReadArtifact(guid, name)` reads them, from the archive in exported games. Importers that opt in (`ReimportWhenDependenciesChange`) reimport when one of their **import inputs** (`AssetImportContext.ImportInputs`, files whose content the import read, such as a glTF's `.bin`) is added or reimported. When files are added, they also reimport if their last import reported problems. Plain dependencies (a model's textures and remapped materials) do not trigger a reimport.
- **Diagnostics:** missing, orphaned or corrupt metas, duplicate GUIDs, failed imports, and references to missing assets.
- **Asset paths** are a trust boundary: `AssetPaths.Resolve` rejects absolute paths and anything that leaves `Assets/`.

## Importers

| Importer | Extensions | Type | Validates and extracts |
|---|---|---|---|
| Texture | .png .jpg .jpeg .bmp .tga | Texture | Decodes the image; width and height. Settings: filter, wrap, mipmaps, pixels per unit |
| Scene / Prefab | .mnvt / .mnvtprefab | Scene / Prefab | Readable JSON with `gameObjects`; a prefab needs exactly one root; dependencies |
| Json | .json | Json | Valid JSON; dependencies |
| Shader / Script / Text | .glsl .vert .frag / .mnvts / .txt .md | Shader / Script / Text | Valid UTF-8; line count. Script compilation arrives with the `.mnvts` compiler (Milestone 6). |
| Audio | .wav .ogg | Audio | RIFF/WAVE or OggS signature. Decoding arrives with the audio subsystem. |
| Font | .ttf .otf | Font | TrueType/OpenType signature |
| Model | .fbx .gltf .glb | Model | Assimp (FBX) or SharpGLTF (glTF); writes the `model` artifact. See [models](models.md). Without the model importer (runtime), model files only get the Model type. |

Any other extension still gets a GUID, with type `Unknown`.

## References from gameplay data

```csharp
[AssetType(AssetTypes.Prefab)]
public AssetRef OrbPrefab;   // persisted as "asset:<guid>", or null when empty
```

A reference can name a part of an asset, a **sub-asset**: `"asset:<guid>#mesh/Body"` is the mesh `Body` of a model
(`AssetRef.SubAsset`). Dependencies and the scene preloader read the GUID part.

- **Built-in uses:** `SpriteRenderer.Texture` is an `AssetRef`.
- **Gameplay API:** `Prefab.Instantiate(prefab, position)` and `SceneManager.LoadScene(AssetRef)`.
- **At runtime:** `ResourceCache.GetTexture(AssetRef)` loads the texture with the sampler from its import settings.

## Prefabs

- **Format:** a `.mnvtprefab` is a scene document holding a single root object and its children.
- **Instantiating:** every object and component gets a fresh id. References inside the prefab (for example a turret's reference to its own barrel) are rewritten to point at the new copy, so any number of instances can coexist.
- **In a running scene:** all data is loaded before `Awake` and `OnEnable` run.
- **Limitation:** instances are unlinked copies. Prefab overrides and propagating prefab edits to existing instances are not implemented.
