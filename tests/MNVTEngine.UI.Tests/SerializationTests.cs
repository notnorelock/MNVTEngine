using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;
using MNVTEngine.UI.Widgets;

namespace MNVTEngine.UI.Tests;

public sealed class SerializationTests
{
    private static Panel BuildMenu()
    {
        var root = new Panel { Name = "Menu", Width = UIUnit.Percent(50), Height = UIUnit.Auto, Layout = LayoutMode.Column, Gap = 8, StyleClass = "panel", Anchor = new Vector2(0.5f, 0.5f), Pivot = new Vector2(0.5f, 0.5f) };
        root.BackgroundColor = new Color(0.1f, 0.2f, 0.3f, 0.9f);
        root.Padding = new Thickness(4, 6, 8, 10);
        root.StateStyles[UIState.Hover] = new UIStyle { BorderColor = Color.Yellow };
        root.AddChild(new Label { Name = "Title", Text = "Zażółć gęślą jaźń", Alignment = Graphics.Text.TextAlign.Center, Wrap = true });
        root.AddChild(new Button { Name = "Play", Text = "Play", Action = "play", Tooltip = "Start", Width = 200 });
        var quality = root.AddChild(new Dropdown { Name = "Quality" });
        quality.Options.AddRange(["Low", "High"]);
        quality.SelectedIndex = 1;
        root.AddChild(new Slider { Name = "Volume", Minimum = 0, Maximum = 10, Step = 1, Value = 7 });
        root.AddChild(new TextField { Name = "Player", Placeholder = "Your name", MaxLength = 12, Password = true });
        var scroll = root.AddChild(new ScrollView { Name = "List", Height = 120 });
        scroll.Content.AddChild(new Label { Text = "Item" });
        root.AddChild(new Image { Name = "Logo", Asset = new AssetRef(Guid.Parse("d0000000-0000-4000-8000-000000000001")), ScaleMode = ImageScaleMode.Fill });
        return root;
    }

    [Fact]
    public void Layouts_RoundTrip_WithPropertiesStylesStatesAndChildren()
    {
        var json = UILayoutSerializer.Save(BuildMenu());
        var loaded = UILayoutSerializer.Load(json, "Menu.mnvtui");
        Assert.Equal(json, UILayoutSerializer.Save(loaded)); // stable

        Assert.Equal((UIUnit.Percent(50), LayoutMode.Column, 8f, "panel"), (loaded.Width, loaded.Layout, loaded.Gap, loaded.StyleClass));
        Assert.Equal(new Thickness(4, 6, 8, 10), loaded.Padding);
        Assert.Equal(Color.Yellow, loaded.StateStyles[UIState.Hover].BorderColor);
        Assert.Equal("Zażółć gęślą jaźń", loaded.Find<Label>("Title")!.Text);
        Assert.Equal("play", loaded.Find<Button>("Play")!.Action);
        Assert.Equal(("High", 1), (loaded.Find<Dropdown>("Quality")!.SelectedOption, loaded.Find<Dropdown>("Quality")!.SelectedIndex));
        Assert.Equal(7f, loaded.Find<Slider>("Volume")!.Value);
        var field = loaded.Find<TextField>("Player")!;
        Assert.Equal(("Your name", 12, true), (field.Placeholder, field.MaxLength, field.Password));
        var list = loaded.Find<ScrollView>("List")!;
        Assert.Single(list.Content.Children); // children of a scroll view go into its content
        Assert.Equal(Guid.Parse("d0000000-0000-4000-8000-000000000001"), loaded.Find<Image>("Logo")!.Asset!.Value.Guid);
        Assert.DoesNotContain("\"focusable\"", json, StringComparison.Ordinal); // defaults are not written
    }

    [Fact]
    public void LoadedLayouts_BindActions_InAContext()
    {
        using var ui = new UIHarness();
        var menu = UILayoutSerializer.Load(UILayoutSerializer.Save(BuildMenu()), "Menu.mnvtui");
        ui.Context.Root.AddChild(menu);
        string? fired = null;
        ui.Context.OnAction("play", e => fired = e.Name);
        ui.Frame();
        ui.Click(menu.Find("Play")!);
        Assert.Equal("Play", fired);
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "root": { "type": "Spaceship" } }""", "root.type")]
    [InlineData("""{ "schemaVersion": 1, "root": { "type": "Panel", "properties": { "wobble": 3 } } }""", "root.properties.wobble")]
    [InlineData("""{ "schemaVersion": 1, "root": { "type": "Panel", "properties": { "width": "wide" } } }""", "root.properties.width")]
    [InlineData("""{ "schemaVersion": 1, "root": { "type": "Panel", "style": { "glow": 1 } } }""", "root.style.glow")]
    [InlineData("""{ "schemaVersion": 1, "root": { "type": "Panel", "children": [ { "type": "Label", "properties": { "layout": "spiral" } } ] } }""", "root.children[0].properties.layout")]
    [InlineData("""{ "schemaVersion": 1, "root": { "type": "Panel", "states": { "angry": {} } } }""", "root.states.angry")]
    [InlineData("""{ "schemaVersion": 1, "root": { "type": "Panel", "text": "x" } }""", "root.text")]
    public void InvalidLayouts_AreRejected_WithThePath(string json, string path)
    {
        var ex = Assert.Throws<SerializationException>(() => UILayoutSerializer.Load(json, "Bad.mnvtui"));
        Assert.Contains(ex.Diagnostics, d => d.Path == path && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void NewerSchemas_AndMissingRoots_AreRejected()
    {
        Assert.Contains("version 9", Assert.Throws<SerializationException>(() => UILayoutSerializer.Load("""{ "schemaVersion": 9, "root": {} }""", "x")).Message, StringComparison.Ordinal);
        Assert.Throws<SerializationException>(() => UILayoutSerializer.Load("""{ "schemaVersion": 1 }""", "x"));
        Assert.Throws<SerializationException>(() => UILayoutSerializer.Load("not json", "x"));
    }
}
