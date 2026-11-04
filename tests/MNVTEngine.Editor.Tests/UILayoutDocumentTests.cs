using MNVTEngine.Editor.Services;
using MNVTEngine.Graphics.Text;
using MNVTEngine.Mathematics;
using MNVTEngine.UI;
using MNVTEngine.UI.Widgets;

namespace MNVTEngine.Editor.Tests;

public sealed class UILayoutDocumentTests : IDisposable
{
    private static readonly AssetRef FontAsset = new(Guid.Parse("e2000000-0000-4000-8000-000000000001"));
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-uidoc-").FullName;
    private readonly Font _font = Font.LoadDefault();

    public void Dispose()
    {
        _font.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private UILayoutDocument Create()
    {
        var path = Path.Combine(_root, "Menu.mnvtui");
        UILayoutDocument.CreateNew(path);
        return new UILayoutDocument(path, asset => asset == FontAsset ? _font : null);
    }

    [Fact]
    public void StructuralEdits_UndoRedo_AndSave()
    {
        var document = Create();
        Assert.False(document.IsDirty);
        var button = (Button)document.Add("Button");
        Assert.Equal("Button", button.Text); // visible at once
        document.Commit();
        document.Selected = document.Root; // add next to the button, not into it
        var scroll = (ScrollView)document.Add("ScrollView");
        var label = document.Add("Label"); // into the selected scroll view's content
        Assert.Same(scroll.Content, label.Parent);
        document.Commit();
        Assert.True(document.IsDirty);

        document.Selected = button;
        Assert.True(document.Move(1));
        Assert.Same(button, document.Root.Children[1]);
        var copy = document.Duplicate()!;
        Assert.Equal(3, document.Root.Children.Count);
        Assert.Same(copy, document.Selected);
        Assert.True(document.Delete());
        Assert.Same(document.Root, document.Selected);
        document.Selected = document.Root;
        Assert.False(document.Delete()); // never the root
        document.Commit();

        document.Undo(); // the duplicate and its deletion were one step: back to before the move
        Assert.IsType<Button>(document.Root.Children[0]);
        document.Undo();
        Assert.Single(document.Root.Children);
        document.Redo();
        Assert.Equal(2, document.Root.Children.Count);
        Assert.Single(UILayoutDocument.EditableChildren(document.Root.Children[1]));

        document.Save();
        Assert.False(document.IsDirty);
        var reopened = new UILayoutDocument(document.FullPath, _ => null);
        Assert.Equal(document.Serialize(), reopened.Serialize());
    }

    [Fact]
    public void PropertyStyleAndFontEdits_AreSaved()
    {
        var document = Create();
        var label = (Label)document.Add("Label");
        UILayoutDocument.SetProperty(label, typeof(Label).GetProperty(nameof(Label.Text))!, "Zażółć");
        UILayoutDocument.SetStyle(label, label.Style, typeof(UIStyle).GetProperty(nameof(UIStyle.FontSize))!, 30f);
        Assert.True(document.SetFont(label, FontAsset));
        Assert.False(document.SetFont(label, new AssetRef(Guid.NewGuid()))); // not a loadable font: unchanged
        Assert.Equal(FontAsset, document.FontOf(label));
        document.Save();

        var json = File.ReadAllText(document.FullPath);
        Assert.Contains("\"font\": \"asset:e2000000-0000-4000-8000-000000000001\"", json, StringComparison.Ordinal);
        var loaded = new UILayoutDocument(document.FullPath, asset => asset == FontAsset ? _font : null);
        var copy = (Label)loaded.Root.Children[0];
        Assert.Equal(("Zażółć", 30f), (copy.Text, copy.Style.FontSize));
        Assert.Same(_font, copy.Style.Font);
    }

    [Fact]
    public void ElementAt_PicksTheDeepestStoredElement()
    {
        var document = Create();
        var card = document.Add("Panel");
        card.Width = 200;
        card.Height = 100;
        var button = document.Add("Button");
        using var ui = new UIContext(_font);
        ui.Root.AddChild(document.Root);
        ui.Update(new InputState().ForUI, new Vector2(800, 600), 1f, 0.016f);

        var center = new Vector2(button.Bounds.X + 2, button.Bounds.Y + 2);
        Assert.Same(button, document.ElementAt(center));
        Assert.Same(document.Root, document.ElementAt(new Vector2(1, 1)));
    }

    [Fact]
    public void TheShowcasePauseMenu_IsAValidLayout()
    {
        var path = Path.Combine(Build.EngineLocation.FindRoot(), "samples", "UIShowcase", "Assets", "UI", "PauseMenu.mnvtui");
        var document = new UILayoutDocument(path, _ => _font);
        var actions = document.Root.Descendants().Select(e => e.Action).OfType<string>().ToList();
        Assert.Equal(["resume", "quit"], actions);
    }
}
