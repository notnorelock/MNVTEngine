using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Editor.Services;
using MNVTEngine.Mathematics;
using MNVTEngine.Terrain.Elements;
using MNVTEngine.Terrain.Foliage;
using MNVTEngine.Terrain.Project;

namespace MNVTEngine.Editor.Tests;

public sealed class TerrainEditorTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"mnvt-terrain-{Guid.NewGuid():N}.mnvtterrain");
    private readonly WorldTerrain _component;
    private readonly TerrainEditor _editor = new(NullLogger.Instance);

    public TerrainEditorTests()
    {
        var project = new TerrainProject();
        project.World.Add(new Terrain.Elements.Terrain(Vector3.Zero, 40, 40));
        File.WriteAllText(_file, project.Save());
        _component = new Scene().CreateGameObject("Terrain").AddComponent<WorldTerrain>();
        Assert.True(_editor.Begin(_component, _file));
    }

    public void Dispose() => File.Delete(_file);

    /// <summary>A straight-down ray at (x, z).</summary>
    private static Ray Down(float x, float z) => new(new Vector3(x, 50f, z), new Vector3(0f, -1f, 0f));

    /// <summary>Top-down view: 10 pixels per meter.</summary>
    private static Vector2? Screen(Vector3 world) => new Vector2(world.X * 10f, world.Z * 10f);

    private static Vector2 Pixel(float x, float z) => new(x * 10f, z * 10f);

    private void Drag(float x0, float z0, float x1, float z1, TerrainModifiers modifiers = TerrainModifiers.None)
    {
        _editor.PointerDown(Down(x0, z0), Pixel(x0, z0), Screen, modifiers);
        _editor.PointerMove(Down(x1, z1), true, modifiers);
        _editor.PointerUp(Pixel(x1, z1), Screen, modifiers);
    }

    private TerrainProject Saved() => Load(File.ReadAllText(_file));

    private static TerrainProject Load(string json)
    {
        var project = new TerrainProject();
        project.Load(json);
        return project;
    }

    [Fact]
    public void EditsTheComponentsWorld_AndSavesEveryAction()
    {
        Assert.Same(_editor.Project, _component.Data);
        _editor.Tool = TerrainTool.Intersection;
        _editor.PointerDown(Down(5f, 5f), Pixel(5f, 5f), Screen, TerrainModifiers.None);
        Assert.IsType<Intersection>(_editor.SelectedElement);
        Assert.Equal(2, Saved().World.Elements.Count);
        Assert.Equal(["Open", "Add Intersection"], _editor.History!.Labels);
    }

    [Fact]
    public void DragsOutARoad_AndConnectsItsEndsToFreeNodes()
    {
        _editor.Tool = TerrainTool.Intersection;
        _editor.PointerDown(Down(0f, 0f), Pixel(0f, 0f), Screen, TerrainModifiers.None);
        var intersection = (Intersection)_editor.SelectedElement!;
        var outlet = intersection.GetNode(1).Position; // the east outlet

        _editor.Tool = TerrainTool.Road;
        Drag((float)outlet.X, (float)outlet.Z, 18f, 0f);
        var road = Assert.IsType<Road>(_editor.SelectedElement);
        Assert.Same(intersection.GetNode(1), road.GetNode(0));
        Assert.Equal(18, road.GetNode(1).Position.X, 3);

        Drag(0f, -15f, 0.05f, -15f); // shorter than the minimum: nothing placed
        Assert.Equal(3, _editor.Project!.World.Elements.Count);
    }

    [Fact]
    public void CurvesASplineWithCtrl_AndSnapsTo45DegreesWithShift()
    {
        _editor.Tool = TerrainTool.Fence;
        Drag(-10f, -10f, 0f, -9f, TerrainModifiers.Ctrl | TerrainModifiers.Shift);
        var fence = Assert.IsType<Fence>(_editor.SelectedElement);
        Assert.True(fence.Divisions > 0);
        Assert.Equal(-10, fence.NodeB.Position.Z, 6); // snapped to the X axis
    }

    [Fact]
    public void MovesASelectedNode_AsOneUndoableAction()
    {
        _editor.Tool = TerrainTool.Road;
        Drag(-10f, 10f, 10f, 10f);
        var road = (Road)_editor.SelectedElement!;

        _editor.Tool = TerrainTool.Select;
        _editor.PointerDown(Down(10f, 10f), Pixel(10f, 10f), Screen, TerrainModifiers.None);
        Assert.Same(road.NodeB, _editor.SelectedNode);
        _editor.PointerMove(Down(12f, 14f), true, TerrainModifiers.None);
        _editor.PointerMove(Down(14f, 16f), true, TerrainModifiers.None);
        _editor.PointerUp(Pixel(14f, 16f), Screen, TerrainModifiers.None);
        Assert.Equal((14.0, 16.0), (Math.Round(road.NodeB.Position.X, 3), Math.Round(road.NodeB.Position.Z, 3)));
        Assert.Equal("Move Node", _editor.History!.Labels[^1]);

        Assert.True(_editor.Undo());
        Assert.Equal(10, ((Road)_editor.Project!.World.Elements[1]).NodeB.Position.X, 3);
        Assert.Equal(10, ((Road)Saved().World.Elements[1]).NodeB.Position.X, 3); // the file follows undo
    }

    [Fact]
    public void HeightBrushRaisesAndCtrlLowers()
    {
        var tile = (Terrain.Elements.Terrain)_editor.Project!.World.Elements[0];
        _editor.Tool = TerrainTool.Height;
        _editor.HeightRadius = 4;
        Drag(0f, 0f, 3f, 0f);
        Assert.True(tile.PaintedHeights.Max(h => h.Height) > 0);
        Assert.Equal("Raise Terrain", _editor.History!.Labels[^1]);
        Drag(0f, 0f, 0f, 0f, TerrainModifiers.Ctrl);
        Assert.Equal("Lower Terrain", _editor.History.Labels[^1]);
    }

    [Fact]
    public void FoliageBrushScattersOnTheSurface_AndCtrlErases()
    {
        var store = _editor.Project!.Foliage;
        var grass = store.AddType(FoliageTypeData.CreateDefault("Grass"));
        _editor.FoliageTypes.Add(grass);
        _editor.Tool = TerrainTool.Foliage;
        Drag(0f, 0f, 4f, 0f);
        Assert.NotEmpty(store.GetInstances(grass));
        Assert.All(store.GetInstances(grass), i => Assert.True(i.Position.ToVector3().DistanceTo(Vector3.Zero) < 7));
        Drag(2f, 0f, 2f, 0f, TerrainModifiers.Ctrl);
        Assert.DoesNotContain(store.GetInstances(grass), i => i.Position.ToVector3().DistanceTo(new Vector3(2, 0, 0)) <= _editor.FoliageRadius);
    }

    [Fact]
    public void PolygonFinishesWithEnter_CancelDropsIt_AndDeleteRemovesTheSelection()
    {
        _editor.Tool = TerrainTool.Polygon;
        foreach (var (x, z) in new[] { (0f, 0f), (5f, 0f), (5f, 5f) })
        {
            _editor.PointerDown(Down(x, z), Pixel(x, z), Screen, TerrainModifiers.None);
        }

        _editor.Finish();
        var polygon = Assert.IsType<PolygonTerrain>(_editor.SelectedElement);
        Assert.Equal(3, polygon.PointCount);

        _editor.Tool = TerrainTool.Polygon;
        _editor.PointerDown(Down(1f, 1f), Pixel(1f, 1f), Screen, TerrainModifiers.None);
        _editor.Cancel();
        Assert.Empty(_editor.PolygonPoints);

        _editor.Tool = TerrainTool.Select;
        _editor.PointerDown(Down(4f, 1f), Pixel(4f, 1f), Screen, TerrainModifiers.None);
        Assert.Same(polygon, _editor.SelectedElement);
        _editor.DeleteSelection();
        Assert.DoesNotContain(polygon, _editor.Project!.World.Elements);
    }

    [Fact]
    public void FollowsTheGameObjectTransform()
    {
        _component.Transform.Position = new Vector3(100f, 0f, 0f);
        _editor.Tool = TerrainTool.Intersection;
        _editor.PointerDown(Down(105f, 0f), Pixel(105f, 0f), Screen, TerrainModifiers.None);
        Assert.Equal(5, ((Intersection)_editor.SelectedElement!).Center.X, 3); // placed in the terrain's local space
    }

    [Fact]
    public void EndReloadsTheSavedWorld()
    {
        _editor.Tool = TerrainTool.Tile;
        _editor.PointerDown(Down(60f, 0f), Pixel(60f, 0f), Screen, TerrainModifiers.None);
        _editor.End();
        Assert.Null(_editor.Target);
        Assert.Null(_editor.Project);
        Assert.False(_editor.Begin(_component, _file + ".missing"));
    }
}
