using MNVTEngine.Mathematics;

namespace MNVTEngine.Editor.Services;

/// <summary>Brush state and painting for tilemaps in the Scene view (UI-free).</summary>
public sealed class TilePainter
{
    /// <summary>Sheet index painted by the brush; -1 erases.</summary>
    public int Tile { get; set; }

    /// <summary>Scene-view clicks paint the selected tilemap instead of selecting.</summary>
    public bool Active { get; set; }

    /// <summary>
    /// Paints every cell the segment from <paramref name="from"/> to <paramref name="to"/> passes through (a drag
    /// between two frames), so fast strokes leave no gaps. Returns the number of cells that changed.
    /// </summary>
    public static int Stroke(Tilemap tilemap, Vector2 from, Vector2 to, int tile)
    {
        ArgumentNullException.ThrowIfNull(tilemap);
        var start = tilemap.WorldToCell(from);
        var end = tilemap.WorldToCell(to);
        var steps = Math.Max(Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
        var changed = 0;
        (int, int)? last = null;
        for (var i = 0; i <= steps; i++)
        {
            var t = steps == 0 ? 0f : (float)i / steps;
            var cell = ((int)MathF.Round(start.X + ((end.X - start.X) * t)), (int)MathF.Round(start.Y + ((end.Y - start.Y) * t)));
            if (cell != last && tilemap.SetTile(cell.Item1, cell.Item2, tile))
            {
                changed++;
            }

            last = cell;
        }

        return changed;
    }
}
