// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-27.

using MNVTEngine.Mathematics;

namespace MNVTEngine.Scenes.Tests;

public class TilemapTests
{
    [Fact]
    public void Tiles_AreSparse_AndChangesAreReported()
    {
        var map = new Scene().CreateGameObject("Map").AddComponent<Tilemap>();
        var changes = 0;
        map.Changed += _ => changes++;

        Assert.True(map.SetTile(2, -1, 5));
        Assert.False(map.SetTile(2, -1, 5)); // same tile: no change
        Assert.Equal(5, map.GetTile(2, -1));
        Assert.Equal(-1, map.GetTile(0, 0));
        Assert.True(map.SetTile(2, -1, -1));
        Assert.Equal(0, map.Count);
        Assert.False(map.SetTile(9, 9, -1));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void CellsMapToWorldThroughTheTransform_AndSheetRectsUseTheGrid()
    {
        var go = new Scene().CreateGameObject("Map");
        go.Transform.Position = new Vector2(10, 5);
        var map = go.AddComponent<Tilemap>();
        (map.CellSize, map.Columns, map.TileWidth, map.TileHeight) = (new Vector2(2, 1), 4, 16, 8);

        Assert.Equal(new Vector2(16, 4), map.CellToWorld(3, -1));
        Assert.Equal((3, -1), map.WorldToCell(new Vector2(17.9f, 4.5f)));
        Assert.Equal((-1, 0), map.WorldToCell(new Vector2(9.5f, 5.2f)));
        Assert.Equal(new Rect(16, 8, 16, 8), map.SourceRect(5));
    }

    [Fact]
    public void SheetRects_SkipTheMarginAndTheSpacing()
    {
        var map = new Scene().CreateGameObject("Map").AddComponent<Tilemap>();
        (map.Columns, map.TileWidth, map.TileHeight, map.Margin, map.Spacing) = (27, 16, 16, 0, 1); // Kenney's 457 x 305 sheets

        Assert.Equal(new Rect(0, 0, 16, 16), map.SourceRect(0));
        Assert.Equal(new Rect(17, 17, 16, 16), map.SourceRect(28));
        Assert.Equal(18, map.RowsIn(305));
        map.Margin = 2;
        Assert.Equal(new Rect(19, 2, 16, 16), map.SourceRect(1));
        map.Spacing = -3;
        Assert.Equal(0, map.Spacing);
    }

    [Fact]
    public void Cells_SurviveSerialization_InStableOrder()
    {
        var scene = new Scene();
        var map = scene.CreateGameObject("Map").AddComponent<Tilemap>();
        map.SetTile(1, 1, 3);
        map.SetTile(-2, 0, 7);
        map.SetTile(0, 0, 1);
        (map.SortingOrder, map.Margin, map.Spacing) = (-3, 1, 2);
        var serializer = new SceneSerializer(TestRegistry.Create());

        var json = serializer.Serialize(scene);
        var copy = serializer.Deserialize(json).Scene.Find("Map")!.GetComponent<Tilemap>()!;
        Assert.Equal([new TileCell(-2, 0, 7), new TileCell(0, 0, 1), new TileCell(1, 1, 3)], copy.Cells);
        Assert.Equal((-3, 1, 2), (copy.SortingOrder, copy.Margin, copy.Spacing));
        Assert.Equal(json, serializer.Serialize(serializer.Deserialize(json).Scene));
    }
}
