using ImGuiNET;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Mathematics;
using SysVector2 = System.Numerics.Vector2;
using SysVector4 = System.Numerics.Vector4;

namespace MNVTEngine.Editor.Panels;

/// <summary>
/// Tree of GameObjects: select, reparent by drag and drop, create and delete, search. Rows show an icon for what the
/// object is, badges for its other components, tree lines, and the object's <see cref="HierarchyStyle"/> (colored labels,
/// gradients, section headers).
/// </summary>
internal sealed class HierarchyPanel(EditorContext context)
{
    private const float Gutter = 18f;

    private static readonly (string Name, Color Color)[] LabelColors =
    [
        ("Orange", new(0.91f, 0.5f, 0.16f)), ("Blue", new(0.25f, 0.55f, 0.95f)), ("Green", new(0.3f, 0.75f, 0.35f)),
        ("Red", new(0.88f, 0.28f, 0.28f)), ("Purple", new(0.62f, 0.4f, 0.92f)), ("Teal", new(0.2f, 0.72f, 0.7f)),
        ("Grey", new(0.45f, 0.47f, 0.52f)),
    ];

    private GameObject? _pendingDelete;
    private string _search = "";
    private int _row;
    private ImDrawListPtr _drawList;

    public void Draw()
    {
        if (ImGui.Begin(EditorWindows.Title("Hierarchy")))
        {
            var scene = context.ActiveScene;
            if (Gui.IconButton(Icons.Plus, "Create"))
            {
                ImGui.OpenPopup("##hierarchy_create");
            }

            if (ImGui.BeginPopup("##hierarchy_create"))
            {
                ImGuiMenus.Draw(CreateEntries(context.Selected?.Transform));
                ImGui.EndPopup();
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("##hierarchy_search", $"{Icons.Search}  Search", ref _search, 128);

            using (Gui.StyleColor(ImGuiCol.Text, new SysVector4(0.85f, 0.86f, 0.9f, 1f)))
            {
                ImGui.TextUnformatted($"{Icons.Boxes}  {(context.PlayMode.IsPlaying ? $"{scene.Name} (playing copy)" : context.Session.DisplayName)}");
            }

            ImGui.Separator();

            // Row backgrounds (stripes, labels, headers) go on a channel under the rows' text.
            _drawList = ImGui.GetWindowDrawList();
            _drawList.ChannelsSplit(2);
            _drawList.ChannelsSetCurrent(1);
            _row = 0;
            if (_search.Trim() is { Length: > 0 } search)
            {
                ImGui.Indent(Gutter); // the visibility eye sits left of the rows
                foreach (var match in scene.RootObjects.SelectMany(Descendants).Where(g => g.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray())
                {
                    DrawNode(match, flat: true);
                }

                ImGui.Unindent(Gutter);
            }
            else
            {
                ImGui.Indent(Gutter);
                foreach (var root in scene.RootObjects.ToArray())
                {
                    DrawNode(root, flat: false);
                }

                ImGui.Unindent(Gutter);
            }

            _drawList.ChannelsMerge();

            // Remaining space: drop here to move an object to the root; right-click to create.
            var avail = ImGui.GetContentRegionAvail();
            ImGui.InvisibleButton("##hierarchy_background", new System.Numerics.Vector2(Math.Max(avail.X, 1f), Math.Max(avail.Y, 20f)));
            if (ImGui.IsItemClicked())
            {
                context.Session.Selection.Clear();
            }

            if (ImGui.BeginDragDropTarget())
            {
                if (Gui.AcceptGuidPayload() is { } id && scene.FindById(id) is { } dropped)
                {
                    dropped.Transform.SetParent(null);
                    context.Changed("Reparent");
                }

                AcceptPrefabDrop(null);

                ImGui.EndDragDropTarget();
            }

            if (ImGui.BeginPopupContextItem("##hierarchy_context"))
            {
                ImGuiMenus.Draw(CreateEntries(null));
                ImGui.EndPopup();
            }
        }

        ImGui.End();

        if (_pendingDelete is { } doomed)
        {
            _pendingDelete = null;
            context.Delete(doomed);
        }
    }

    /// <summary>The "create" items, for the GameObject menu and the Hierarchy's context menus.</summary>
    public IReadOnlyList<MenuEntry> CreateEntries(Transform? parent) =>
    [
        new("Create Empty", Action: () => context.CreateGameObject("GameObject", parent)),
        new("Create Header", Action: () => context.CreateGameObject("Header", parent).AddComponent<HierarchyStyle>().Kind = HierarchyStyleKind.Header),
        new("Create Sprite", Action: () => context.CreateGameObject("Sprite", parent).AddComponent<SpriteRenderer>()),
        new("Create Camera", Action: () => context.CreateGameObject("Camera", parent).AddComponent<Camera>()),
        MenuEntry.Submenu("3D Object",
        [
            .. Enum.GetValues<PrimitiveMesh>().Where(p => p != PrimitiveMesh.None).Select(primitive => new MenuEntry(primitive.ToString(), Action: () =>
            {
                var gameObject = context.CreateGameObject(primitive.ToString(), parent);
                gameObject.AddComponent<MeshRenderer>().Primitive = primitive;
                AddCollider(gameObject, primitive);
            })),
        ]),
        MenuEntry.Submenu("Light",
        [
            .. Enum.GetValues<LightType>().Select(type => new MenuEntry($"{type} Light", Action: () =>
            {
                var gameObject = context.CreateGameObject($"{type} Light", parent);
                gameObject.AddComponent<Light>().Type = type;
                // Directional and spot lights shine along -Z: point them down at the scene.
                gameObject.Transform.LocalRotation = type == LightType.Point ? Quaternion.Identity : Quaternion.Euler(type == LightType.Directional ? -50f : -90f, type == LightType.Directional ? 30f : 0f, 0f);
                gameObject.Transform.LocalPosition = type == LightType.Point ? new Vector3(0f, 2f, 0f) : new Vector3(0f, 4f, 0f);
            })),
            new("Ambient Light", Action: () => context.CreateGameObject("Ambient Light", parent).AddComponent<AmbientLight>()),
        ]),
    ];

    /// <summary>A collider matching a primitive's shape, as Unity adds one (add a Rigidbody to make it fall).</summary>
    private static void AddCollider(GameObject gameObject, PrimitiveMesh primitive)
    {
        switch (primitive)
        {
            case PrimitiveMesh.Sphere:
                gameObject.AddComponent<SphereCollider>();
                break;
            case PrimitiveMesh.Capsule:
                gameObject.AddComponent<CapsuleCollider>();
                break;
            case PrimitiveMesh.Cylinder:
                gameObject.AddComponent<CylinderCollider>();
                break;
            case PrimitiveMesh.Plane:
                var plane = gameObject.AddComponent<BoxCollider>();
                (plane.Size, plane.Center) = (new Vector3(10f, 0.02f, 10f), new Vector3(0f, -0.01f, 0f));
                break;
            case PrimitiveMesh.Quad:
                gameObject.AddComponent<BoxCollider>().Size = new Vector3(1f, 1f, 0.02f);
                break;
            default:
                gameObject.AddComponent<BoxCollider>(); // cube, cone (its bounding box)
                break;
        }
    }

    private static IEnumerable<GameObject> Descendants(GameObject root) =>
        root.Transform.Children.SelectMany(c => Descendants(c.GameObject)).Prepend(root);

    /// <summary>Draws a row (and, unless <paramref name="flat"/>, its open subtree); returns where tree lines meet it.</summary>
    private (float MidY, float Left, bool HasChildren) DrawNode(GameObject gameObject, bool flat)
    {
        var style = gameObject.GetComponent<HierarchyStyle>();
        var header = style?.Kind == HierarchyStyleKind.Header;
        var hasChildren = !flat && gameObject.Transform.ChildCount > 0;
        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.AllowOverlap;
        if (!hasChildren)
        {
            flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
        }

        var selected = context.Session.Selection.IsSelected(gameObject);
        if (selected)
        {
            flags |= ImGuiTreeNodeFlags.Selected;
        }

        // The label is drawn by hand (colored icon, badges, headers): the node itself has none. The row spans the full
        // width, so the indented start is taken from the cursor.
        var left = ImGui.GetCursorScreenPos().X;
        var right = left + ImGui.GetContentRegionAvail().X; // the content edge, left of the scrollbar
        var open = ImGui.TreeNodeEx($"##{gameObject.Id}", flags);
        var (min, max) = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        var hovered = ImGui.IsItemHovered();
        HandleRowInput(gameObject);

        var rowMin = new SysVector2(ImGui.GetWindowPos().X, min.Y);
        var rowMax = new SysVector2(right, max.Y);
        DrawRowBackground(rowMin, rowMax, style, selected || hovered);

        var font = ImGui.GetFontSize();
        var textY = min.Y + ((max.Y - min.Y - font) * 0.5f);
        var textColor = gameObject.ActiveInHierarchy ? ImGui.GetStyle().Colors[(int)ImGuiCol.Text] : ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled];
        var labelX = left + ImGui.GetTreeNodeToLabelSpacing();
        if (header)
        {
            var width = ImGui.CalcTextSize(gameObject.Name).X;
            _drawList.AddText(new SysVector2(((rowMin.X + rowMax.X) * 0.5f) - (width * 0.5f), textY), ImGui.GetColorU32(textColor), gameObject.Name);
        }
        else
        {
            var (icon, iconColor) = HierarchyIcons.Primary(gameObject, open && hasChildren);
            if (!gameObject.ActiveInHierarchy)
            {
                iconColor.W *= 0.45f;
            }

            _drawList.AddText(new SysVector2(labelX, textY), ImGui.GetColorU32(iconColor), icon);
            _drawList.AddText(new SysVector2(labelX + font + 6f, textY), ImGui.GetColorU32(textColor), gameObject.Name);
            DrawBadges(gameObject, icon, rowMax.X - 2f, textY, font);
        }

        DrawVisibility(gameObject, rowMin, min.Y, max.Y, hovered);

        if (open && hasChildren)
        {
            // Tree lines: from under this row's arrow down to each child, then across to it.
            var lineX = left + (ImGui.GetTreeNodeToLabelSpacing() * 0.5f);
            var lineColor = ImGui.GetColorU32(new SysVector4(1f, 1f, 1f, 0.18f));
            var lastY = max.Y;
            foreach (var child in gameObject.Transform.Children.ToArray())
            {
                var (midY, childLeft, childHasChildren) = DrawNode(child.GameObject, flat: false);
                var end = childLeft + (childHasChildren ? 2f : ImGui.GetTreeNodeToLabelSpacing() * 0.75f);
                _drawList.AddLine(new SysVector2(lineX, midY), new SysVector2(end, midY), lineColor);
                lastY = midY;
            }

            _drawList.AddLine(new SysVector2(lineX, max.Y), new SysVector2(lineX, lastY), lineColor);
            ImGui.TreePop();
        }

        return ((min.Y + max.Y) * 0.5f, left, hasChildren);
    }

    /// <summary>Unity's eye in the left gutter: hides the object (and its children) in the Scene view only.</summary>
    private void DrawVisibility(GameObject gameObject, SysVector2 rowMin, float top, float bottom, bool rowHovered)
    {
        var visibility = context.Visibility;
        var hiddenSelf = visibility.IsHiddenSelf(gameObject);
        var hiddenByParent = !hiddenSelf && !visibility.ShowHidden && !visibility.IsVisible(gameObject);
        var cursor = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new SysVector2(rowMin.X + 2f, top));
        ImGui.InvisibleButton($"##eye{gameObject.Id}", new SysVector2(Gutter - 4f, bottom - top));
        var hot = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked())
        {
            visibility.Toggle(gameObject);
            hiddenSelf = !hiddenSelf;
        }

        if (hot)
        {
            Gui.Tooltip(hiddenSelf ? "Hidden in the Scene view (click to show)" : "Click to hide in the Scene view");
        }

        ImGui.SetCursorScreenPos(cursor);
        if (hiddenSelf || hiddenByParent || rowHovered || hot)
        {
            var color = hiddenSelf ? new SysVector4(0.9f, 0.9f, 0.95f, 1f) : new SysVector4(0.6f, 0.62f, 0.68f, hiddenByParent ? 0.5f : 1f);
            var textY = top + ((bottom - top - ImGui.GetFontSize()) * 0.5f);
            _drawList.AddText(new SysVector2(rowMin.X + 4f, textY), ImGui.GetColorU32(color), hiddenSelf || hiddenByParent ? Icons.EyeOff : Icons.Eye);
        }
    }

    /// <summary>Selection, drag and drop and the context menu of the row just drawn.</summary>
    private void HandleRowInput(GameObject gameObject)
    {
        if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
        {
            context.Session.Selection.Select(gameObject);
        }

        if (ImGui.BeginDragDropSource())
        {
            Gui.SetGuidPayload(gameObject.Id);
            ImGui.TextUnformatted(gameObject.Name);
            ImGui.EndDragDropSource();
        }

        if (ImGui.BeginDragDropTarget())
        {
            if (Gui.AcceptGuidPayload() is { } id && context.ActiveScene.FindById(id) is { } dropped && !IsSelfOrAncestor(dropped.Transform, gameObject.Transform))
            {
                dropped.Transform.SetParent(gameObject.Transform);
                context.Changed("Reparent");
            }

            AcceptPrefabDrop(gameObject.Transform);

            ImGui.EndDragDropTarget();
        }

        if (ImGui.BeginPopupContextItem())
        {
            context.Session.Selection.Select(gameObject);
            ImGuiMenus.Draw(CreateEntries(gameObject.Transform));
            ImGui.Separator();
            DrawStyleMenu(gameObject);
            ImGui.Separator();
            if (ImGui.MenuItem($"{Icons.Trash2}  Delete", "Del"))
            {
                _pendingDelete = gameObject;
            }

            ImGui.EndPopup();
        }
    }

    private void DrawStyleMenu(GameObject gameObject)
    {
        if (!ImGui.BeginMenu($"{Icons.Palette}  Hierarchy Style"))
        {
            return;
        }

        var style = gameObject.GetComponent<HierarchyStyle>();
        HierarchyStyle Ensure() => style ??= gameObject.AddComponent<HierarchyStyle>();
        if (ImGui.MenuItem("Header", "", style?.Kind == HierarchyStyleKind.Header))
        {
            var edited = Ensure();
            edited.Kind = edited.Kind == HierarchyStyleKind.Header ? HierarchyStyleKind.Label : HierarchyStyleKind.Header;
            context.Changed("Hierarchy Style");
        }

        if (ImGui.MenuItem("Gradient", "", style?.Gradient == true))
        {
            var edited = Ensure();
            edited.Gradient = !edited.Gradient;
            context.Changed("Hierarchy Style");
        }

        ImGui.Separator();
        foreach (var (name, color) in LabelColors)
        {
            using (Gui.StyleColor(ImGuiCol.Text, new SysVector4(color.R, color.G, color.B, 1f)))
            {
                if (ImGui.MenuItem($"{Icons.Square}  {name}", "", style is not null && style.Color == color))
                {
                    Ensure().Color = color;
                    context.Changed("Hierarchy Style");
                }
            }
        }

        ImGui.Separator();
        if (ImGui.MenuItem("Clear", "", false, style is not null))
        {
            style!.Destroy();
            context.Changed("Hierarchy Style");
        }

        ImGui.EndMenu();
    }

    /// <summary>Stripes on every other row; a label's marker and fill or a header's bar; under the row's text.</summary>
    private void DrawRowBackground(SysVector2 min, SysVector2 max, HierarchyStyle? style, bool highlighted)
    {
        _drawList.ChannelsSetCurrent(0);
        if (_row++ % 2 == 1 && !highlighted)
        {
            _drawList.AddRectFilled(min, max, ImGui.GetColorU32(new SysVector4(1f, 1f, 1f, 0.025f)));
        }

        if (style is not null)
        {
            var color = new SysVector4(style.Color.R, style.Color.G, style.Color.B, 1f);
            uint Alpha(float a) => ImGui.GetColorU32(color with { W = a });
            if (style.Kind == HierarchyStyleKind.Header)
            {
                var bar = (new SysVector2(min.X + 4f, min.Y + 1f), new SysVector2(max.X - 4f, max.Y - 1f));
                if (style.Gradient)
                {
                    var middle = (bar.Item1.X + bar.Item2.X) * 0.5f;
                    _drawList.AddRectFilledMultiColor(bar.Item1, new SysVector2(middle, bar.Item2.Y), Alpha(0f), Alpha(0.85f), Alpha(0.85f), Alpha(0f));
                    _drawList.AddRectFilledMultiColor(new SysVector2(middle, bar.Item1.Y), bar.Item2, Alpha(0.85f), Alpha(0f), Alpha(0f), Alpha(0.85f));
                }
                else
                {
                    _drawList.AddRectFilled(bar.Item1, bar.Item2, Alpha(0.85f), 3f);
                }
            }
            else
            {
                _drawList.AddRectFilled(new SysVector2(min.X + 2f, min.Y + 2f), new SysVector2(min.X + 5f, max.Y - 2f), Alpha(1f));
                if (style.Gradient)
                {
                    var from = min.X + ((max.X - min.X) * 0.25f);
                    _drawList.AddRectFilledMultiColor(new SysVector2(from, min.Y + 1f), new SysVector2(max.X, max.Y - 1f), Alpha(0f), Alpha(0.8f), Alpha(0.8f), Alpha(0f));
                }
                else
                {
                    _drawList.AddRectFilled(new SysVector2(min.X + 6f, min.Y + 1f), new SysVector2(max.X, max.Y - 1f), Alpha(0.22f));
                }
            }
        }

        _drawList.ChannelsSetCurrent(1);
    }

    /// <summary>Component badges, right-aligned, with the component names on hover.</summary>
    private void DrawBadges(GameObject gameObject, string primary, float right, float y, float size)
    {
        var mouse = ImGui.GetMousePos();
        var x = right;
        foreach (var (icon, color, name) in HierarchyIcons.Badges(gameObject, primary).Take(6))
        {
            x -= size + 2f;
            _drawList.AddText(new SysVector2(x, y), ImGui.GetColorU32(gameObject.ActiveInHierarchy ? color : color with { W = color.W * 0.45f }), icon);
            if (ImGui.IsWindowHovered() && mouse.X >= x && mouse.X < x + size && mouse.Y >= y && mouse.Y < y + size)
            {
                Gui.Tooltip(name);
            }
        }
    }

    private void AcceptPrefabDrop(Transform? parent)
    {
        if (Gui.AcceptStringPayload(Gui.AssetPayload) is { } path && context.ResolveAsset(path) is { } asset)
        {
            if (asset.Type == Serialization.AssetTypes.Prefab)
            {
                context.InstantiatePrefab(asset.Guid, parent);
            }
            else if (asset.Type == Serialization.AssetTypes.Model)
            {
                context.InstantiateModel(asset.Guid, parent);
            }
        }
    }

    private static bool IsSelfOrAncestor(Transform candidate, Transform node)
    {
        for (var t = node; t is not null; t = t.Parent)
        {
            if (t == candidate)
            {
                return true;
            }
        }

        return false;
    }
}
