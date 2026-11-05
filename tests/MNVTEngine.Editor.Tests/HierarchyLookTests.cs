using MNVTEngine.Editor.Panels;

namespace MNVTEngine.Editor.Tests;

public sealed class HierarchyLookTests
{
    [Fact]
    public void RowIcon_ShowsWhatTheObjectIs_AndBadgesTheRest()
    {
        var scene = new Scene();
        var camera = scene.CreateGameObject("Main Camera");
        camera.AddComponent<Camera>();
        camera.AddComponent<SpriteRenderer>();
        camera.AddComponent<HierarchyStyle>();
        Assert.Equal(Icons.Video, HierarchyIcons.Primary(camera, open: false).Icon);
        Assert.Equal([(Icons.Image, "SpriteRenderer")], HierarchyIcons.Badges(camera, Icons.Video).Select(b => (b.Icon, b.Name)));

        var folder = scene.CreateGameObject("Props");
        scene.CreateGameObject("Rock").Transform.SetParent(folder.Transform);
        Assert.Equal((Icons.Folder, Icons.FolderOpen), (HierarchyIcons.Primary(folder, open: false).Icon, HierarchyIcons.Primary(folder, open: true).Icon));
        Assert.Equal(Icons.Box, HierarchyIcons.Primary(scene.CreateGameObject("Empty"), open: false).Icon);
    }

    [Fact]
    public void NativeMenuText_EscapesAmpersands_AndShowsShortcutsAfterATab()
    {
        Assert.Equal("Save Scene	Ctrl+S", NativeMenuBar.ItemText(new MenuEntry("Save Scene", "Ctrl+S")));
        Assert.Equal("Build && Run", NativeMenuBar.ItemText(new MenuEntry("Build & Run")));
        Assert.True(MenuEntry.Separator.IsSeparator);
        Assert.False(new MenuEntry("-").IsSeparator); // only the shared instance is a separator
    }

    [Fact]
    public void IconFont_IsEmbeddedInTheEditor()
    {
        var (ttf, first, last) = Icons.Font;
        Assert.True(ttf.Length > 100_000 && ttf[0] == 0 && ttf[1] == 1); // a TrueType file (version 1.0 header)
        Assert.InRange(Icons.Play[0], first, last);
    }

    [Fact]
    public void Windows_HaveIconTitles_OnStableIds_AndOldLayoutsAreMigrated()
    {
        Assert.Equal($"{Icons.ListTree}  Hierarchy###Hierarchy", EditorWindows.Title("Hierarchy"));
        Assert.Equal("###Scene", EditorWindows.Id("Scene"));

        const string Old = "[Window][Hierarchy]\nPos=0,0\nDockId=0x1\n\n[Window][Debug##Default]\nPos=60,60\n\n[Window][Script Editor]\nDockId=0x2\n";
        Assert.Equal("[Window][###Hierarchy]\nPos=0,0\nDockId=0x1\n\n[Window][Debug##Default]\nPos=60,60\n\n[Window][###Script Editor]\nDockId=0x2\n", EditorWindows.MigrateLayout(Old));
        Assert.Equal(EditorWindows.MigrateLayout(Old), EditorWindows.MigrateLayout(EditorWindows.MigrateLayout(Old)));
    }
}
