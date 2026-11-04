using MNVTEngine.Mathematics;
using MNVTEngine.Scenes;
using MNVTEngine.UI.Widgets;

namespace MNVTEngine.UI.Tests;

public sealed class UIDocumentTests
{
    private static readonly AssetRef Layout = new(Guid.Parse("e1000000-0000-4000-8000-000000000001"));

    private static string Menu()
    {
        var menu = new Panel { Name = "Menu" };
        menu.AddChild(new Button { Name = "Play", Text = "Play", Action = "play" });
        return UILayoutSerializer.Save(menu);
    }

    [Fact]
    public void Settings_SurviveSerialization()
    {
        var scene = new Scene();
        var document = scene.CreateGameObject("HUD").AddComponent<UIDocument>();
        (document.Layout, document.SortOrder, document.Scale) = (Layout, 5, 1.5f);
        var serializer = new SceneSerializer(UIComponents.Register(ComponentRegistry.CreateDefault()));
        var copy = serializer.Deserialize(serializer.Serialize(scene)).Scene.Find("HUD")!.GetComponent<UIDocument>()!;
        Assert.Equal((Layout, 5, 1.5f), (copy.Layout, copy.SortOrder, copy.Scale));
    }

    [Fact]
    public void ShowsItsLayout_InItsOwnContext_WhileActive()
    {
        var scene = new Scene();
        var system = UISystem.Attach(scene, new UIServices(UIHarness.Font, null, asset => asset == Layout ? System.Text.Encoding.UTF8.GetBytes(Menu()) : throw new KeyNotFoundException(), null));
        var holder = scene.CreateGameObject("HUD");
        var document = holder.AddComponent<UIDocument>();
        (document.Layout, document.SortOrder) = (Layout, 3);
        scene.Start();

        Assert.NotNull(document.Context);
        Assert.Equal("Menu", document.Root!.Name);
        Assert.Equal(3, document.Context.SortOrder);
        Assert.Contains(document.Context, system.Contexts);
        Assert.NotNull(document.Root.Find<Button>("Play"));

        holder.SetActive(false);
        Assert.False(document.Context.Visible);
        holder.SetActive(true);
        Assert.True(document.Context.Visible);

        document.Destroy();
        scene.Update(new Core.Timing.FrameTime(0.016f, 0.016f, 0.016f, 0.016f, 1)); // destruction happens at the end of the frame
        Assert.Empty(system.Contexts);
    }

    [Fact]
    public void ABrokenLayout_LeavesAnEmptyContext_InsteadOfFailingTheScene()
    {
        var scene = new Scene();
        UISystem.Attach(scene, new UIServices(UIHarness.Font, null, _ => """{ "schemaVersion": 1, "root": { "type": "Spaceship" } }"""u8.ToArray(), null));
        var document = scene.CreateGameObject("HUD").AddComponent<UIDocument>();
        document.Layout = Layout;
        scene.Start();
        Assert.NotNull(document.Context);
        Assert.Null(document.Root);
    }
}
