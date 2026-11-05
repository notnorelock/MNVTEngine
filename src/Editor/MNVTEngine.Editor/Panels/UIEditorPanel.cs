using System.Globalization;
using System.Reflection;
using ImGuiNET;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Editor.Services;
using MNVTEngine.Mathematics;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;
using MNVTEngine.UI;
using SysVector2 = System.Numerics.Vector2;
using SysVector4 = System.Numerics.Vector4;

namespace MNVTEngine.Editor.Panels;

/// <summary>
/// Edits a UI layout (<c>.mnvtui</c>):
/// <list type="bullet">
/// <item>the element tree, with add, duplicate, delete and reorder;</item>
/// <item>a live preview, drawn with the game UI renderer at a chosen resolution (click to select);</item>
/// <item>an inspector for text, font, widget properties, style and state styles.</item>
/// </list>
/// Ctrl+S saves and Ctrl+Z/Y undo and redo while the window has focus. The preview runs in its own scene with a UI
/// system, so images and fonts resolve like in the game.
/// </summary>
internal sealed class UIEditorPanel : IDisposable
{
    private static readonly (string Name, int Width, int Height)[] Resolutions = [("1280 x 720", 1280, 720), ("1920 x 1080", 1920, 1080), ("800 x 600", 800, 600), ("1080 x 1920 (portrait)", 1080, 1920)];
    private static readonly SysVector4 ErrorColor = new(1f, 0.45f, 0.45f, 1f);
    private readonly EditorContext _context;
    private readonly ViewTarget _view;
    private readonly Scene _previewScene = new("UI Preview");
    private readonly UISystem _previewUI;
    private readonly UIContext _preview;
    private readonly InputState _noInput = new();
    private UILayoutDocument? _document;
    private UIElement? _shown;
    private int _resolution;
    private string _addType = "Button";
    private UIState _stateStyle = UIState.Hover;
    private string? _error;
    private bool _visible;
    private int _focusFrames;

    public UIEditorPanel(EditorContext context, UIServices services)
    {
        _context = context;
        _view = new ViewTarget(context, "UIEditor");
        _previewUI = UISystem.Attach(_previewScene, services);
        _preview = _previewUI.CreateContext();
    }

    public bool IsFocused { get; private set; }

    public bool HasUnsavedChanges => _document?.IsDirty == true;

    public void Open(string fullPath)
    {
        try
        {
            _document = new UILayoutDocument(fullPath, TryLoadFont);
            _document.Selected = _document.Root;
            _error = null;
        }
        catch (Exception ex) when (ex is SerializationException or IOException or UnauthorizedAccessException)
        {
            _error = $"{Path.GetFileName(fullPath)}: {ex.Message}";
            EditorLog.ActionFailed(_context.Logger, ex, $"Open UI layout {fullPath}");
        }

        _focusFrames = 3;
    }

    public void Save()
    {
        if (_document is null)
        {
            return;
        }

        try
        {
            _document.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _error = ex.Message;
            EditorLog.ActionFailed(_context.Logger, ex, $"Save UI layout {_document.FullPath}");
        }
    }

    public void Undo() => _document?.Undo();

    public void Redo() => _document?.Redo();

    public void Draw()
    {
        if (_focusFrames > 0)
        {
            _focusFrames--;
            ImGui.SetWindowFocus(EditorWindows.Id("UI Editor"));
        }

        if (_context.CenterDockId != 0)
        {
            ImGui.SetNextWindowDockID(_context.CenterDockId, ImGuiCond.FirstUseEver); // layouts saved before this panel existed
        }

        ImGui.SetNextWindowSize(new SysVector2(1100f, 650f), ImGuiCond.FirstUseEver);
        _visible = ImGui.Begin(EditorWindows.Title("UI Editor"));
        IsFocused = _visible && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        if (!_visible)
        {
            ImGui.End();
            return;
        }

        if (_error is not null)
        {
            Gui.TextColored(ErrorColor, _error);
        }

        if (_document is not { } document)
        {
            Gui.Muted("Double-click a .mnvtui layout in the Project panel, or right-click a folder there: Create > UI Layout.");
            ImGui.End();
            return;
        }

        DrawToolbar(document);
        var avail = ImGui.GetContentRegionAvail();
        const float treeWidth = 210f, inspectorWidth = 320f;
        ImGui.BeginChild("##tree", new SysVector2(treeWidth, 0), ImGuiChildFlags.Borders);
        DrawTree(document, document.Root);
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("##preview", new SysVector2(Math.Max(100f, avail.X - treeWidth - inspectorWidth - 16f), 0), ImGuiChildFlags.Borders);
        DrawPreview(document);
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("##inspector", SysVector2.Zero, ImGuiChildFlags.Borders);
        if (document.Selected is { } selected)
        {
            DrawInspector(document, selected);
        }
        else
        {
            Gui.Muted("Select an element in the tree or the preview.");
        }

        ImGui.EndChild();
        ImGui.End();

        // A drag or a typed value becomes one undo step once the interaction is over.
        if (!ImGui.IsAnyItemActive() && !ImGui.IsAnyMouseDown())
        {
            document.Commit();
        }
    }

    /// <summary>Lays out and draws the preview into its target (render phase).</summary>
    public void Render(float deltaSeconds)
    {
        if (!_visible || _document is null)
        {
            return;
        }

        if (!ReferenceEquals(_shown, _document.Root))
        {
            _shown?.RemoveFromParent();
            _shown = _preview.Root.AddChild(_document.Root); // after undo the document has a new root
        }

        var (_, width, height) = Resolutions[_resolution];
        _view.Request(width, height);
        _view.EnsureSize();
        _noInput.BeginFrame();
        _preview.Update(_noInput.ForUI, new Vector2(width, height), 1f, deltaSeconds);
        _context.Device.SetRenderTarget(_view.Target);
        _context.Device.Clear(new Color(0.11f, 0.12f, 0.14f, 1f));
        _context.GameUI.Render(_preview, _view.Target.Width, _view.Target.Height);
    }

    private void DrawToolbar(UILayoutDocument document)
    {
        if (ImGui.Button(document.IsDirty ? "Save*" : "Save"))
        {
            Save();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(!document.CanUndo);
        if (Gui.IconButton(Icons.Undo2, "Undo (Ctrl+Z)"))
        {
            document.Undo();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!document.CanRedo);
        if (Gui.IconButton(Icons.Redo2, "Redo (Ctrl+Y)"))
        {
            document.Redo();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130f);
        if (ImGui.BeginCombo("##type", _addType))
        {
            foreach (var type in UILayoutSerializer.TypeNames.Where(t => t != nameof(UIElement)).Order(StringComparer.Ordinal))
            {
                if (ImGui.Selectable(type, type == _addType))
                {
                    _addType = type;
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button($"{Icons.Plus}  Add"))
        {
            document.Add(_addType);
        }

        Hint("Adds the widget inside the selected element.");
        var isChild = document.Selected is { } s && s != document.Root;
        ImGui.BeginDisabled(!isChild);
        foreach (var (label, action) in ((string, Action)[])[("Duplicate", () => document.Duplicate()), ("Delete", () => document.Delete()), ("Up", () => document.Move(-1)), ("Down", () => document.Move(1))])
        {
            ImGui.SameLine();
            if (ImGui.Button(label))
            {
                action();
            }
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(170f);
        if (ImGui.BeginCombo("##resolution", Resolutions[_resolution].Name))
        {
            for (var i = 0; i < Resolutions.Length; i++)
            {
                if (ImGui.Selectable(Resolutions[i].Name, i == _resolution))
                {
                    _resolution = i;
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        Gui.Muted(Path.GetRelativePath(_context.Session.Project.RootDirectory, document.FullPath));
    }

    private static void DrawTree(UILayoutDocument document, UIElement element)
    {
        var children = UILayoutDocument.EditableChildren(element);
        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth
            | (children.Count == 0 ? ImGuiTreeNodeFlags.Leaf : 0) | (element == document.Selected ? ImGuiTreeNodeFlags.Selected : 0);
        var label = element.Name.Length > 0 ? $"{element.GetType().Name} '{element.Name}'" : element.GetType().Name;
        var open = ImGui.TreeNodeEx($"{label}##{element.GetHashCode()}", flags);
        if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
        {
            document.Selected = element;
        }

        if (open)
        {
            foreach (var child in children)
            {
                DrawTree(document, child);
            }

            ImGui.TreePop();
        }
    }

    private void DrawPreview(UILayoutDocument document)
    {
        var (_, width, height) = Resolutions[_resolution];
        var avail = ImGui.GetContentRegionAvail();
        var scale = Math.Min(avail.X / width, avail.Y / height);
        if (scale <= 0f)
        {
            return;
        }

        var size = new SysVector2(width * scale, height * scale);
        ImGui.SetCursorPos(ImGui.GetCursorPos() + ((avail - size) * 0.5f));
        var origin = ImGui.GetCursorScreenPos();
        Gui.Image(_view.Id, _view.Target, size);
        if (ImGui.IsItemClicked())
        {
            var local = (ImGui.GetMousePos() - origin) / scale;
            document.Selected = document.ElementAt(new Vector2(local.X, local.Y)) ?? document.Root;
        }

        if (document.Selected is { } selected)
        {
            var b = selected.Bounds;
            var min = origin + (new SysVector2(b.X, b.Y) * scale);
            ImGui.GetWindowDrawList().AddRect(min, min + (new SysVector2(b.Width, b.Height) * scale), ImGui.GetColorU32(new SysVector4(1f, 0.3f, 1f, 1f)), 0f, ImDrawFlags.None, 2f);
        }
    }

    private void DrawInspector(UILayoutDocument document, UIElement element)
    {
        var type = element.GetType();
        ImGui.TextUnformatted(type.Name);
        var name = element.Name;
        if (ImGui.InputText("Name", ref name, 128))
        {
            element.Name = name;
        }

        foreach (var text in (PropertyInfo?[])[UILayoutSerializer.TextPropertyOf(type), UILayoutSerializer.PlaceholderPropertyOf(type)])
        {
            if (text is not null && DrawValue(text.Name, text.PropertyType, text.GetValue(element), out var value))
            {
                UILayoutDocument.SetProperty(element, text, value);
            }
        }

        var font = document.FontOf(element) is { } fontRef ? AssetText(fontRef) : "";
        if (ImGui.InputText("Font", ref font, 260, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            if (font.Trim().Length == 0)
            {
                document.SetFont(element, null);
            }
            else if (ParseAsset(font) is not { } asset || !document.SetFont(element, asset))
            {
                _error = $"Font '{font}' is not a font asset.";
            }
        }

        Hint("A font asset path (Fonts/Title.ttf) or asset:<guid>; empty for the default font. Press Enter.");
        if (ImGui.CollapsingHeader("Properties", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var property in UILayoutSerializer.EditableProperties(type))
            {
                if (DrawValue(property.Name, property.PropertyType, property.GetValue(element), out var value))
                {
                    UILayoutDocument.SetProperty(element, property, value);
                }
            }
        }

        if (ImGui.CollapsingHeader("Style", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawStyle(element, element.Style, "own");
        }

        if (ImGui.CollapsingHeader("State styles"))
        {
            ImGui.SetNextItemWidth(160f);
            if (ImGui.BeginCombo("State", _stateStyle.ToString()))
            {
                foreach (var state in (UIState[])[UIState.Hover, UIState.Pressed, UIState.Focused, UIState.Disabled, UIState.Checked])
                {
                    if (ImGui.Selectable(state.ToString(), state == _stateStyle))
                    {
                        _stateStyle = state;
                    }
                }

                ImGui.EndCombo();
            }

            if (element.StateStyles.TryGetValue(_stateStyle, out var style))
            {
                DrawStyle(element, style, _stateStyle.ToString());
                if (ImGui.Button($"{Icons.Trash2}  Remove state style"))
                {
                    element.StateStyles.Remove(_stateStyle);
                    element.InvalidateLayout();
                }
            }
            else if (ImGui.Button($"Add {_stateStyle} style"))
            {
                element.StateStyles[_stateStyle] = new UIStyle();
            }
        }
    }

    private void DrawStyle(UIElement element, UIStyle style, string id)
    {
        ImGui.PushID(id);
        foreach (var property in UILayoutSerializer.StyleProperties)
        {
            if (DrawValue(property.Name, property.PropertyType, property.GetValue(style), out var value))
            {
                UILayoutDocument.SetStyle(element, style, property, value);
            }
        }

        ImGui.PopID();
    }

    /// <summary>One editor per supported value type; nullable values get a checkbox that sets or clears them.</summary>
    private bool DrawValue(string label, Type type, object? current, out object? value)
    {
        value = current;
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            var set = current is not null;
            if (ImGui.Checkbox($"##set{label}", ref set))
            {
                value = set ? DefaultOf(underlying) : null;
                return true;
            }

            Hint(set ? "Clear (use the theme / default)" : "Set a value");
            ImGui.SameLine();
            if (!set)
            {
                Gui.TextDisabled(label);
                return false;
            }

            type = underlying;
        }

        switch (current)
        {
            case string or null when type == typeof(string):
                var s = current as string ?? "";
                if (ImGui.InputText(label, ref s, 1024))
                {
                    value = s;
                    return true;
                }

                return false;
            case bool b:
                if (ImGui.Checkbox(label, ref b))
                {
                    value = b;
                    return true;
                }

                return false;
            case float f:
                if (ImGui.DragFloat(label, ref f, 0.5f))
                {
                    value = f;
                    return true;
                }

                return false;
            case int i:
                if (ImGui.DragInt(label, ref i, 0.2f))
                {
                    value = i;
                    return true;
                }

                return false;
            case Enum e:
                if (ImGui.BeginCombo(label, e.ToString()))
                {
                    var picked = false;
                    foreach (var option in Enum.GetValues(type))
                    {
                        if (ImGui.Selectable(option.ToString(), option.Equals(e)))
                        {
                            (value, picked) = (option, true);
                        }
                    }

                    ImGui.EndCombo();
                    return picked;
                }

                return false;
            case UIUnit unit:
                var text = unit.ToString();
                if (ImGui.InputText(label, ref text, 32, ImGuiInputTextFlags.EnterReturnsTrue))
                {
                    try
                    {
                        value = UIUnit.Parse(text);
                        return true;
                    }
                    catch (FormatException ex)
                    {
                        _error = $"{label}: {ex.Message}";
                    }
                }

                Hint("Pixels (120), percent (50%) or auto. Press Enter.");
                return false;
            case Thickness t:
                var v4 = new SysVector4(t.Left, t.Top, t.Right, t.Bottom);
                if (ImGui.DragFloat4(label, ref v4, 0.5f))
                {
                    value = new Thickness(v4.X, v4.Y, v4.Z, v4.W);
                    return true;
                }

                Hint("Left, top, right, bottom");
                return false;
            case Vector2 v:
                var v2 = v.ToSys();
                if (ImGui.DragFloat2(label, ref v2, 0.01f))
                {
                    value = v2.ToEngine();
                    return true;
                }

                return false;
            case Color c:
                var color = c.ToSys();
                if (ImGui.ColorEdit4(label, ref color))
                {
                    value = new Color(color.X, color.Y, color.Z, color.W);
                    return true;
                }

                return false;
            case AssetRef or null when type == typeof(AssetRef):
                var path = current is AssetRef asset ? AssetText(asset) : "";
                if (ImGui.InputText(label, ref path, 260, ImGuiInputTextFlags.EnterReturnsTrue))
                {
                    if (path.Trim().Length == 0)
                    {
                        value = underlying is not null ? null : AssetRef.None;
                        return true;
                    }

                    if (ParseAsset(path) is { } parsed)
                    {
                        value = parsed;
                        return true;
                    }

                    _error = $"{label}: '{path}' is not an asset.";
                }

                Hint("An asset path (Textures/logo.png) or asset:<guid>. Press Enter.");
                return false;
            case List<string> list:
                var lines = string.Join('\n', list);
                if (ImGui.InputTextMultiline(label, ref lines, 4096, new SysVector2(0, 80)))
                {
                    list.Clear();
                    list.AddRange(lines.Split('\n'));
                    value = list;
                    return true;
                }

                Hint("One entry per line");
                return false;
            default:
                Gui.Muted($"{label}: {Convert.ToString(current, CultureInfo.InvariantCulture)}");
                return false;
        }
    }

    private static void Hint(string text)
    {
        if (ImGui.IsItemHovered())
        {
            Gui.Tooltip(text);
        }
    }

    private static object DefaultOf(Type type) => type == typeof(Color) ? Color.White : type == typeof(float) ? 0f : Activator.CreateInstance(type)!;

    private string AssetText(AssetRef asset) =>
        _context.Session.Assets.TryGetPath(asset.Guid, out var path) ? path : AssetRef.Prefix + asset.Guid.ToString("D");

    private AssetRef? ParseAsset(string text)
    {
        text = text.Trim();
        if (text.StartsWith(AssetRef.Prefix, StringComparison.Ordinal) && Guid.TryParse(text.AsSpan(AssetRef.Prefix.Length), out var guid))
        {
            return new AssetRef(guid);
        }

        return _context.Session.Assets.TryGetGuid(text, out guid) ? new AssetRef(guid) : null;
    }

    private Graphics.Text.Font? TryLoadFont(AssetRef asset)
    {
        if (_context.Session.Assets.GetAssetType(asset.Guid) != AssetTypes.Font)
        {
            return null;
        }

        try
        {
            return _previewUI.LoadFont(asset);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or ArgumentException)
        {
            EditorLog.ActionFailed(_context.Logger, ex, $"Load font {asset}");
            return null;
        }
    }

    public void Dispose()
    {
        _previewScene.Unload(); // disposes the preview context and the fonts it loaded
        _view.Dispose();
    }
}
