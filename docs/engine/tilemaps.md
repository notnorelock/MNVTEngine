# Tilemaps

## `Tilemap` (Scenes)

A sparse grid of tiles from one sprite sheet. Only painted cells are stored and drawn.

| Setting | Meaning |
|---|---|
| `Texture` | Sheet texture. Use the `point` filter for pixel art. |
| `TileWidth`, `TileHeight`, `Columns` | The sheet grid. Sheet index `n` is at column `n % Columns`, row `n / Columns` from the image's top-left, the same convention as [animation clips](animation.md). |
| `CellSize` | Cell size in local units (default 1 × 1). |
| `Color`, `SortingOrder` | Tint and draw order, on the same scale as `SpriteRenderer`. |
| `Cells` | Painted cells as `{ "x", "y", "tile" }`, saved sorted by row then column for stable diffs. |

**Placement and API:**

- Cell (0, 0) has its bottom-left corner at the GameObject's position. +x is right and +y is up, and the transform's rotation and scale apply.
- `GetTile(x, y)` returns the sheet index at a cell, or -1 for an empty cell.
- `SetTile(x, y, tile)` paints a cell; a negative `tile` clears it.
- `WorldToCell` and `CellToWorld` convert between world positions and cells. `SourceRect(tile)` gives a tile's rectangle in the sheet.
- `Changed` is raised on every edit.

**Rendering.** `SceneRenderer` draws the painted cells through the sprite batch with per-cell view culling, so the counters report tiles together with sprites.

## `TilemapCollider2D` (Physics2D)

Collides with the `Tilemap` on the same GameObject.

- **Shapes.** Each row's contiguous run of solid cells becomes one box. Characters therefore slide along floors without catching on the seams between tiles, and a 100-tile floor is one shape, not 100.
- **Ignored tiles.** `IgnoredTiles` lists sheet indices that never collide, such as decoration or background.
- **Body type.** Without a `Rigidbody2D` the body is static. A kinematic `Rigidbody2D` makes the map a moving platform.
- **Changes.** Painting at run time rebuilds the collider at the next physics step. For example, digging a hole lets bodies fall through.
- **Shared settings.** The usual collider settings (`Friction`, `Bounciness`, `IsTrigger`, `Offset`) apply to all of its boxes.

## Editing

Select a GameObject with a `Tilemap`. Its inspector shows the sheet as a tile palette:

- **Choosing the brush.** Click a tile to choose it and turn painting on, or choose **Eraser**.
- **Toggling painting.** Press **B** in the Scene view, or use the **Paint in Scene view** checkbox.
- **Painting.** Left drag paints the brush tile and Ctrl + left drag erases. A fast drag fills every cell along its path (`TilePainter.Stroke`).
- **Cursor.** The cell under the cursor is outlined.
- **Selection.** While painting, clicks do not change the selection. Turn painting off to use the gizmos again.

## Limitations

- There are no auto-tiling rules, animated tiles, or per-tile collision shapes: a solid tile collides as a full cell.
- Vertical runs are not merged, so a wall is one box per row. That is harmless for collisions, only a few more shapes.
- The palette shows at most 1024 tiles.
- Painting is not undoable yet (undo/redo is a later Milestone 8 stage).
