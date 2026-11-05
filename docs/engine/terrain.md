# Terrain (TerrainHeaven port)

`MNVTEngine.Terrain` is the world-building toolkit from [TerrainHeaven](https://github.com/borsuczyna/TerrainHeaven), ported from TypeScript to C# with its author's permission. It covers roads, intersections, rivers, terrain cuts, stairs, fences, polygon patches and the adaptive terrain mesher. All geometry is pure double-precision math, with no rendering or platform dependencies. Third-party code it carries is listed in `src/Engine/MNVTEngine.Terrain/THIRD-PARTY-NOTICES.md`.

TerrainHeaven's buildings are not part of the port. When a project file contains them, the loader skips them and logs a warning.

## Elements

`TerrainWorld` holds the elements and rebuilds them in dependency order. It replaces TerrainHeaven's `SceneManager`.

- Terrain tiles are rebuilt last, because roads, rivers and other cutters carve holes in them.
- `CutPoints` are free-standing circular terrain cuts.
- `GroupMesher` merges touching tiles into one mesh.
- `LodPreviewLevel` previews export LODs.

| Type (`ElementData.Type`) | Class | Notes |
|---|---|---|
| `terrain` | `Terrain` | Rectangular tile with adaptive meshing, smoothing, painted heights and four splat texture layers. |
| `terrainPolygon` | `PolygonTerrain` | Patch with any closed outline. Each vertex has its own height. |
| `road` | `Road` | Cubic spline road: lanes, sidewalks or curbs, crown, bridges with pillars. |
| `intersection` | `Intersection` | Three-way (T) or four-way cross with fixed outlets. Connected roads taper to the outlet width. |
| `river` | `RiverSpline` | River bed and water surface. |
| `terrainCutSpline` | `TerrainCutSpline` | Spline that carves a channel in the terrain. |
| `stairs` | `Stairs` | Stepped prism with optional railings. It cuts one ramp into the terrain. |
| `fence` | `Fence` | Plane or box panels. Posts sit at a fixed spacing and wherever the curve turns. |

Each element follows the same pattern:

- It exposes nodes. Connected elements share one node.
- `Update()` rebuilds `Geometry`: named `GeometryGroup`s of triangles with UVs.
- `GetProperties()` describes its inspector fields.
- `Serialize`/`Deserialize` read and write the project format.

## Projects

`TerrainProject` reads and writes TerrainHeaven's project JSON (version 1). It bundles:

- the `World`;
- `Settings` (`ProjectSettings`);
- `Foliage` (`FoliageStore`);
- `Meshes` (`MeshStore`, placed props).

```csharp
var project = new TerrainProject(logger);
project.Load(File.ReadAllText("world.terrain.json"));
project.World.Update();
File.WriteAllText("world.terrain.json", project.Save());
```

**Field names.**

- Element, settings and connection fields are camelCase, as TerrainHeaven writes them.
- Foliage and mesh records are PascalCase, the Unity exporter's contract.
- Dictionary keys, such as texture group names, are kept exactly as written.

**Validation.** Project files are untrusted input.

- Malformed JSON, an empty file or another version throws `InvalidDataException`.
- An unknown element type is skipped with warning 7110.
- A connection to a missing element or node is skipped with warning 7111.

**Texture paths** are normalized as TerrainHeaven does it (`TerrainProject.NormalizeTexturePath`):

- Backslashes become forward slashes, and a leading `./` is removed.
- A URL becomes its decoded file name.
- An embedded `data:` URL from old projects becomes `legacy-texture-<fnv1a>.png`.

`GetTexturePaths()` lists every texture a project references.

**Settings.** `ProjectSettings` stores the sky color, the time of day, the day/night cycle and its length, and the LOD switch distances. Clamping matches TerrainHeaven.

- `Evaluate()` returns a `SkyEnvironment` (linear colors) for the active mode: flat sky color, basic day/night, or enhanced visuals. The enhanced mode adds a sky dome, fog, sun glow, stars, clouds and a shadow-casting sun or moon.
- `Advance(delta)` moves the clock, in steps of at least 0.1 s, only while enhanced visuals are on.

**Foliage and props.**

- `FoliageStore` holds foliage presets and one layer of placed billboard instances per preset. An instance stores its scale and color as normalized 0..1 factors, so editing a preset resizes and recolors every instance.
- `MeshStore` holds prop assets and their placed instances. An asset references a mesh library file and has a scale and pivot calibration. An instance has a full Euler rotation and a uniform scale.
- Removing a preset or asset renumbers the layers after it.

**Prop meshes.**

- `MeshCalibration` applies an asset's scale, then its offset, to loaded `MeshGroup`s (triangle soups per material). `ComputeGroundedCenterOffset` centers a prop on X/Z and rests it on Y = 0.
- `MeshLodGenerator` builds LOD1 to LOD3 by decimating LOD0 to 40 %, 15 % and 5 % of its vertices, and caches the result per asset path. It uses `MeshSimplifier`, a port of three.js's SimplifyModifier (Melax edge collapse).
- The ratio counts welded vertices. TerrainHeaven counted soup vertices, which emptied every LOD of a closed mesh.

## Export

`ObjWriter` writes Wavefront OBJ and MTL text from triangle soups.

- Every vertex gets its own `v`/`vt`/`vn` line.
- Normals are "auto smoothed" at 30°: a vertex averages the face normals of neighbouring triangles, across every group, that lie within 30° of its own face.
- Group and material names are sanitized.

`UnityExporter.Export(project, folder, options, source)` writes the layout TerrainHeaven's Unity importer reads:

| Path | Contents |
|---|---|
| `Meshes/<kind>_<n>_lod<i>.obj/.mtl` | One pair per element and LOD. `options.Scale` is baked into the vertices. Cut splines are skipped: they have no geometry. |
| `Meshes/prop_<id>_lod<i>.obj/.mtl` | Each placed prop asset once per LOD, calibrated and decimated, unscaled. |
| `Textures/` | Every referenced texture. A missing texture is skipped with warning 7121. |
| `ControlMaps/<terrain>_control.png` | Splat weights, with the rows flipped so the image origin is bottom-left (Unity's UV convention). |
| `foliage.json`, `meshes.json` | Foliage store, plus prop assets and their placements (unscaled). |
| `manifest.json` | Scale, LOD count and distances, objects with their LOD files and texture per submesh, terrain paint layers, and the texture list. |

`ITerrainExportSource` supplies texture bytes and prop LOD0 meshes, so the exporter does not depend on where assets live.

## Editing helpers

These helpers have no UI. The editor wires them to its panels and shortcuts.

**`TerrainHistory`** gives snapshot undo and redo: each entry is a full `Save()`, and undo restores it with `Load()`.

- Wrap a drag or a brush stroke in `BeginAction`/`EndAction` so it becomes one entry.
- An edit that leaves the project unchanged adds no entry.
- `JumpTo(index)` restores any entry from the history list.

**`TerrainClipboard`** copies an element or only its settings.

- A copy carries every inspector value except positions, plus texture references, texture rotations and UV transforms.
- `PasteElement` spawns the copy 8 m in front of the camera. It keeps the source's span and direction for roads, fences and stairs, the outlet count for intersections, and the size for terrain tiles.
- `PastePropertiesOnto` applies the settings to another element of the same type.

**`ElementPresets`** stores named presets and one default per element type, in TerrainHeaven's preset JSON (`ToJson`/`Load`).

- `ApplyPreset` reads the inspector in several passes, so fields that appear only after a discriminator changes (such as a box fence's thickness) still get their values.
- `ApplyDefault` applies the type's default preset; call it when an element is placed.
