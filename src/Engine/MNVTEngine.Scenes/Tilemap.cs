using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;

namespace MNVTEngine;

/// <summary>One painted cell: grid coordinates and sheet index.</summary>
public readonly record struct TileCell(int X, int Y, int Tile);

/// <summary>
/// A grid of tiles from one sprite sheet, laid out like animation clips: sheet index <c>n</c> is at column
/// <c>n % Columns</c>, row <c>n / Columns</c> from the image's top-left. Cell (0, 0) has its bottom-left corner at the
/// GameObject's position; +x is right, +y is up, in local units of <see cref="CellSize"/>. Cells are sparse: only
/// painted cells are stored and rendered.
/// </summary>
[ComponentId("MNVTEngine.Tilemap")]
public sealed class Tilemap : Component
{
    private readonly Dictionary<(int X, int Y), int> _tiles = [];
    private int _tileWidth = 16;
    private int _tileHeight = 16;
    private int _columns = 1;
    private Vector2 _cellSize = Vector2.One;

    [SerializeField]
    [AssetType(AssetTypes.Texture)]
    public AssetRef Texture { get; set; }

    /// <summary>Tile size in the sheet, in texels.</summary>
    [SerializeField]
    public int TileWidth { get => _tileWidth; set => _tileWidth = Math.Max(1, value); }

    [SerializeField]
    public int TileHeight { get => _tileHeight; set => _tileHeight = Math.Max(1, value); }

    /// <summary>Tiles per row in the sheet.</summary>
    [SerializeField]
    public int Columns { get => _columns; set => _columns = Math.Max(1, value); }

    /// <summary>Cell size in local units.</summary>
    [SerializeField]
    public Vector2 CellSize { get => _cellSize; set => _cellSize = new Vector2(Math.Max(0.001f, value.X), Math.Max(0.001f, value.Y)); }

    [SerializeField]
    public Color Color { get; set; } = Color.White;

    [SerializeField]
    [AssetType(AssetTypes.Material)]
    public AssetRef Material { get; set; }

    /// <summary>Higher values draw on top (same scale as <see cref="SpriteRenderer.SortingOrder"/>).</summary>
    [SerializeField]
    public int SortingOrder { get; set; }

    /// <summary>Painted cells, sorted by row then column (stable files and diffs).</summary>
    [SerializeField]
    [HideInInspector] // painted in the Scene view
    [Newtonsoft.Json.JsonProperty(ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace)] // the getter returns a copy: always call the setter
    public IReadOnlyList<TileCell> Cells
    {
        get => [.. _tiles.OrderBy(t => t.Key.Y).ThenBy(t => t.Key.X).Select(t => new TileCell(t.Key.X, t.Key.Y, t.Value))];
        set
        {
            _tiles.Clear();
            foreach (var cell in value ?? [])
            {
                if (cell.Tile >= 0)
                {
                    _tiles[(cell.X, cell.Y)] = cell.Tile;
                }
            }

            Changed?.Invoke(this);
        }
    }

    public int Count => _tiles.Count;

    /// <summary>Raised after any tile changes (colliders rebuild from it).</summary>
    public event Action<Tilemap>? Changed;

    /// <summary>Sheet index at the cell, or -1 if empty.</summary>
    public int GetTile(int x, int y) => _tiles.GetValueOrDefault((x, y), -1);

    /// <summary>Paints a cell; a negative tile clears it. Returns true if the cell changed.</summary>
    public bool SetTile(int x, int y, int tile)
    {
        var changed = tile < 0 ? _tiles.Remove((x, y)) : !_tiles.TryGetValue((x, y), out var old) || old != tile;
        if (tile >= 0)
        {
            _tiles[(x, y)] = tile;
        }

        if (changed)
        {
            Changed?.Invoke(this);
        }

        return changed;
    }

    public void Clear()
    {
        _tiles.Clear();
        Changed?.Invoke(this);
    }

    /// <summary>Texel rectangle of a sheet index.</summary>
    public Rect SourceRect(int tile) => new(tile % _columns * _tileWidth, tile / _columns * _tileHeight, _tileWidth, _tileHeight);

    /// <summary>The cell containing a world point.</summary>
    public (int X, int Y) WorldToCell(Vector2 world)
    {
        var local = (Vector2)Transform.WorldToLocalMatrix.MultiplyPoint3x4(new Vector3(world, 0f));
        return ((int)MathF.Floor(local.X / _cellSize.X), (int)MathF.Floor(local.Y / _cellSize.Y));
    }

    /// <summary>World position of a cell's bottom-left corner.</summary>
    public Vector2 CellToWorld(int x, int y) =>
        (Vector2)Transform.LocalToWorldMatrix.MultiplyPoint3x4(new Vector3(x * _cellSize.X, y * _cellSize.Y, 0f));

    public IEnumerable<TileCell> EnumerateTiles()
    {
        foreach (var ((x, y), tile) in _tiles)
        {
            yield return new TileCell(x, y, tile);
        }
    }
}
