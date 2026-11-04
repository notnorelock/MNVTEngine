using System.Reflection;
using MNVTEngine.Graphics.Text;
using MNVTEngine.Mathematics;
using MNVTEngine.UI;
using MNVTEngine.UI.Widgets;

namespace MNVTEngine.Editor.Services;

/// <summary>
/// A UI layout (<c>.mnvtui</c>) open in the UI Editor: the element tree, selection, structural edits, property edits,
/// undo/redo by snapshots and saving. UI-free and testable; the editor panel draws it.
/// </summary>
public sealed class UILayoutDocument
{
    private readonly Func<AssetRef, Font?> _loadFont;
    private readonly Dictionary<Font, AssetRef> _fontRefs = new(ReferenceEqualityComparer.Instance);
    private readonly List<string> _undo = [];
    private readonly List<string> _redo = [];
    private string _committed;
    private string _saved;

    /// <param name="loadFont">Loads a font asset; null when it cannot be loaded.</param>
    /// <exception cref="Serialization.SerializationException">The file is not a valid layout.</exception>
    public UILayoutDocument(string fullPath, Func<AssetRef, Font?> loadFont)
    {
        ArgumentNullException.ThrowIfNull(loadFont);
        FullPath = fullPath;
        _loadFont = loadFont;
        Root = Load(File.ReadAllText(fullPath));
        _committed = _saved = Serialize();
    }

    public string FullPath { get; }

    public UIElement Root { get; private set; }

    public UIElement? Selected { get; set; }

    /// <summary>Changed since the last save (as of the last <see cref="Commit"/>).</summary>
    public bool IsDirty => _committed != _saved;

    public bool CanUndo => _undo.Count > 0 || Serialize() != _committed;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Writes a new layout: a full-screen panel.</summary>
    public static void CreateNew(string fullPath)
    {
        var root = new Panel { Name = "Root", Width = UIUnit.Percent(100), Height = UIUnit.Percent(100), Layout = LayoutMode.Column, Justify = Justify.Center, AlignItems = Align.Center, Gap = 8 };
        File.WriteAllText(fullPath, UILayoutSerializer.Save(root));
    }

    /// <summary>The children a layout stores: a scroll view's content, other elements' own children.</summary>
    public static IReadOnlyList<UIElement> EditableChildren(UIElement element) =>
        element is ScrollView scroll ? scroll.Content.Children : element.Children;

    public string Serialize() => UILayoutSerializer.Save(Root, font => _fontRefs.TryGetValue(font, out var asset) ? asset : null);

    /// <summary>Records one undo step if the layout changed since the last commit. Call when no edit is in progress.</summary>
    public void Commit()
    {
        var json = Serialize();
        if (json == _committed)
        {
            return;
        }

        _undo.Add(_committed);
        _redo.Clear();
        _committed = json;
    }

    public void Undo()
    {
        Commit();
        if (_undo.Count == 0)
        {
            return;
        }

        _redo.Add(_committed);
        _committed = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Restore(_committed);
    }

    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        _undo.Add(_committed);
        _committed = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Restore(_committed);
    }

    public void Save()
    {
        Commit();
        File.WriteAllText(FullPath, _committed);
        _saved = _committed;
    }

    /// <summary>Adds a new widget into the selected element (or the root) and selects it.</summary>
    public UIElement Add(string typeName)
    {
        var element = UILayoutSerializer.Create(typeName);
        if (UILayoutSerializer.TextPropertyOf(element.GetType()) is { } text && string.IsNullOrEmpty(text.GetValue(element) as string))
        {
            text.SetValue(element, typeName); // visible at once
        }

        var parent = Selected ?? Root;
        (parent is ScrollView scroll ? scroll.Content : parent).AddChild(element);
        Selected = element;
        return element;
    }

    /// <summary>Copies the selected element (with its children) next to it.</summary>
    public UIElement? Duplicate()
    {
        if (Selected is not { Parent: { } parent } original || original == Root)
        {
            return null;
        }

        var copy = Load(UILayoutSerializer.Save(original, font => _fontRefs.TryGetValue(font, out var asset) ? asset : null));
        parent.InsertChild(IndexIn(parent, original) + 1, copy);
        Selected = copy;
        return copy;
    }

    /// <summary>Removes the selected element (never the root) and selects its parent.</summary>
    public bool Delete()
    {
        if (Selected is not { Parent: { } parent } element || element == Root)
        {
            return false;
        }

        parent.RemoveChild(element);
        Selected = Owner(parent);
        return true;
    }

    /// <summary>Moves the selected element among its siblings (-1 up, +1 down).</summary>
    public bool Move(int delta)
    {
        if (Selected is not { Parent: { } parent } element || element == Root)
        {
            return false;
        }

        var index = IndexIn(parent, element);
        var target = Math.Clamp(index + delta, 0, parent.Children.Count - 1);
        if (target == index)
        {
            return false;
        }

        parent.RemoveChild(element);
        parent.InsertChild(target, element);
        return true;
    }

    /// <summary>Sets a widget property and updates layout.</summary>
    public static void SetProperty(UIElement element, PropertyInfo property, object? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(property);
        property.SetValue(element, value);
        element.InvalidateLayout();
    }

    /// <summary>Sets a style property of <paramref name="style"/> (the element's own or a state style) and updates it.</summary>
    public static void SetStyle(UIElement element, UIStyle style, PropertyInfo property, object? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(property);
        property.SetValue(style, value);
        element.InvalidateLayout();
    }

    /// <summary>The font asset of an element (null when it uses the default font).</summary>
    public AssetRef? FontOf(UIElement element) =>
        element?.Style.Font is { } font && _fontRefs.TryGetValue(font, out var asset) ? asset : null;

    /// <summary>Sets an element's font from an asset (or the default font with null); false when it cannot be loaded.</summary>
    public bool SetFont(UIElement element, AssetRef? asset)
    {
        ArgumentNullException.ThrowIfNull(element);
        Font? font = null;
        if (asset is { } value && (font = LoadFont(value)) is null)
        {
            return false;
        }

        element.Style.Font = font;
        element.InvalidateLayout();
        return true;
    }

    /// <summary>The deepest visible stored element at a point (layout pixels), for picking in the preview.</summary>
    public UIElement? ElementAt(Vector2 point) => ElementAt(Root, point);

    private static UIElement? ElementAt(UIElement element, Vector2 point)
    {
        if (!element.Visible || !element.Bounds.Contains(point))
        {
            return null;
        }

        var children = EditableChildren(element);
        for (var i = children.Count - 1; i >= 0; i--)
        {
            if (ElementAt(children[i], point) is { } hit)
            {
                return hit;
            }
        }

        return element;
    }

    private UIElement Load(string json) => UILayoutSerializer.Load(json, FullPath, LoadFont);

    private Font? LoadFont(AssetRef asset)
    {
        var font = _loadFont(asset);
        if (font is not null)
        {
            _fontRefs[font] = asset;
        }

        return font;
    }

    private void Restore(string json)
    {
        var path = PathOf(Selected);
        Root = Load(json);
        Selected = Find(path);
    }

    /// <summary>The stored element that owns <paramref name="element"/> (a scroll view instead of its content).</summary>
    private static UIElement Owner(UIElement element) => element.Parent is ScrollView scroll && scroll.Content == element ? scroll : element;

    private static int IndexIn(UIElement parent, UIElement child)
    {
        for (var i = 0; i < parent.Children.Count; i++)
        {
            if (parent.Children[i] == child)
            {
                return i;
            }
        }

        return -1;
    }

    private List<int>? PathOf(UIElement? element)
    {
        var path = new List<int>();
        for (var e = element; e is not null && e != Root; e = e.Parent is { } p ? Owner(p) : null)
        {
            var index = e.Parent is null ? -1 : IndexIn(e.Parent, e);
            if (index < 0)
            {
                return null;
            }

            path.Insert(0, index);
        }

        return element is null ? null : path;
    }

    private UIElement? Find(List<int>? path)
    {
        if (path is null)
        {
            return null;
        }

        var element = Root;
        foreach (var index in path)
        {
            var children = EditableChildren(element);
            if (index >= children.Count)
            {
                return null;
            }

            element = children[index];
        }

        return element;
    }
}
