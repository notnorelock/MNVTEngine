using MNVTEngine.Mathematics;
using MNVTEngine.Terrain.Editing;
using MNVTEngine.Terrain.Elements;
using MNVTEngine.Terrain.Geometry;
using MNVTEngine.Terrain.Project;

namespace MNVTEngine.Terrain.Tests;

public sealed class EditingTests
{
    [Fact]
    public void HistoryUndoesRedoesAndCollapsesActions()
    {
        var project = new TerrainProject();
        var history = new TerrainHistory(project);
        history.Reset("Open");
        project.World.Add(new Road(Vector3.Zero, new Vector3(10, 0, 0)));
        history.Record("Add road");
        history.Record("No-op"); // unchanged: no entry

        history.BeginAction("Drag");
        var road = (Road)project.World.Elements[0];
        road.NodeB.Update(new Vector3(12, 0, 0));
        road.NodeB.Update(new Vector3(14, 0, 0));
        history.BeginAction("Nested"); // ignored while an action is open
        history.EndAction();
        Assert.Equal(["Open", "Add road", "Drag"], history.Labels);

        var restored = 0;
        history.Restored += () => restored++;
        Assert.True(history.Undo());
        Assert.Equal(10, ((Road)project.World.Elements[0]).NodeB.Position.X, 6);
        Assert.True(history.Undo());
        Assert.Empty(project.World.Elements);
        Assert.False(history.Undo());
        Assert.True(history.Redo());
        Assert.Single(project.World.Elements);

        project.World.Add(new Intersection(new Vector3(30, 0, 0)));
        history.Record("Add intersection"); // drops the redo branch
        Assert.Equal(["Open", "Add road", "Add intersection"], history.Labels);
        Assert.False(history.CanRedo);
        Assert.True(history.JumpTo(0));
        Assert.Empty(project.World.Elements);
        Assert.Equal(4, restored);
    }

    [Fact]
    public void ClipboardPastesAnElementWithItsSpanSettingsAndTextures()
    {
        var world = new TerrainWorld();
        var source = new Road(new Vector3(0, 0, 0), new Vector3(0, 0, 6)) { Width = 7 };
        source.SetGroupTextureReference("road", "asphalt.png");
        source.SetTextureRotation("road", 90);
        world.Add(source);
        var clipboard = new TerrainClipboard();
        clipboard.CopyElement(source);
        Assert.True(clipboard.CanPasteElement());

        var pasted = Assert.IsType<Road>(clipboard.PasteElement(new Vector3(0, 5, 0), new Vector3(1, -0.5, 0), world));
        Assert.Equal(2, world.Elements.Count);
        Assert.Equal(7, pasted.Width);
        Assert.Equal(new Vector3(8, 0, 0), (pasted.NodeA.Position + pasted.NodeB.Position) / 2);
        Assert.Equal(6, pasted.NodeA.Position.DistanceTo(pasted.NodeB.Position), 5);
        Assert.Equal("asphalt.png", pasted.GetGroupTextureReferences()["road"]);
        Assert.Equal(90, pasted.TextureRotations["road"]);
    }

    [Fact]
    public void ClipboardPastesPropertiesOnlyOntoTheSameType()
    {
        var a = new Fence(Vector3.Zero, new Vector3(5, 0, 0)) { Style = "box", PostSpacing = 3.5 };
        var b = new Fence(Vector3.Zero, new Vector3(9, 0, 0));
        var clipboard = new TerrainClipboard();
        clipboard.CopyProperties(a);
        Assert.False(clipboard.CanPasteElement());
        Assert.False(clipboard.CanPastePropertiesOnto(new Road(Vector3.Zero, Vector3.UnitX)));
        Assert.True(clipboard.CanPastePropertiesOnto(b));
        clipboard.PastePropertiesOnto(b);
        Assert.Equal(("box", 3.5), (b.Style, b.PostSpacing));
        Assert.Equal(9, b.NodeB.Position.X); // positions are not part of a property copy
    }

    [Fact]
    public void PresetsApplyDependentFieldsAndRoundTripTheStore()
    {
        var presets = new ElementPresets();
        var changes = 0;
        presets.Changed += () => changes++;
        var fence = new Fence(Vector3.Zero, new Vector3(5, 0, 0)) { Style = "box", Thickness = 0.42 };
        fence.SetGroupTextureReference("fence", "planks.png");
        var preset = presets.SavePreset("  Picket  ", fence);
        Assert.Equal(("Picket", "Fence"), (preset.Name, preset.ElementType));
        Assert.Matches("^[0-9a-z]+-[0-9a-z]{7}$", preset.Id);

        var reloaded = new ElementPresets();
        Assert.True(reloaded.Load(presets.ToJson()));
        var target = new Fence(Vector3.Zero, new Vector3(3, 0, 0));
        Assert.True(reloaded.ApplyPreset(preset.Id, target));
        Assert.Equal(("box", 0.42), (target.Style, target.Thickness)); // thickness appears only once Style is box
        Assert.Equal("planks.png", target.GetGroupTextureReferences()["fence"]);
        Assert.False(reloaded.ApplyPreset(preset.Id, new Road(Vector3.Zero, Vector3.UnitX)));

        reloaded.SetDefault(preset.Id);
        Assert.True(reloaded.IsDefault(reloaded.GetPreset(preset.Id)!));
        Assert.True(reloaded.ApplyDefault(new Fence(Vector3.Zero, Vector3.UnitX)));
        reloaded.DeletePreset(preset.Id);
        Assert.False(reloaded.ApplyDefault(new Fence(Vector3.Zero, Vector3.UnitX)));
        Assert.Equal(1, changes);

        Assert.False(new ElementPresets().Load("{ not json"));
        Assert.False(new ElementPresets().Load("""{ "version": 2, "presets": [] }"""));
    }
}
