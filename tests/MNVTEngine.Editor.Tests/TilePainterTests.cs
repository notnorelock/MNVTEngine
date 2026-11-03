using MNVTEngine.Editor.Services;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Editor.Tests;

public class TilePainterTests
{
    [Fact]
    public void Stroke_PaintsEveryCellAlongAFastDrag_AndErases()
    {
        var map = new Scene().CreateGameObject("Map").AddComponent<Tilemap>();

        Assert.Equal(6, TilePainter.Stroke(map, new Vector2(0.5f, 0.5f), new Vector2(5.5f, 0.5f), 3));
        Assert.Equal([0, 1, 2, 3, 4, 5], map.Cells.Select(c => c.X));
        Assert.Equal(0, TilePainter.Stroke(map, new Vector2(2.2f, 0.7f), new Vector2(2.8f, 0.1f), 3)); // same cell, same tile

        Assert.Equal(4, TilePainter.Stroke(map, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 3.5f), 7)); // vertical
        Assert.Equal(7, map.GetTile(0, 0));

        Assert.Equal(2, TilePainter.Stroke(map, new Vector2(4.5f, 0.5f), new Vector2(5.5f, 0.5f), -1));
        Assert.Equal(-1, map.GetTile(5, 0));
        Assert.Equal(7, map.Count);
    }
}
