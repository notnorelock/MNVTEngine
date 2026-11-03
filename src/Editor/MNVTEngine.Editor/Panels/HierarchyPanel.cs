using ImGuiNET;
using MNVTEngine.Editor.ImGuiBackend;

namespace MNVTEngine.Editor.Panels;

/// <summary>Tree of GameObjects: select, reparent by drag and drop, create and delete.</summary>
internal sealed class HierarchyPanel(EditorContext context)
{
    private GameObject? _pendingDelete;

    public void Draw()
    {
        if (ImGui.Begin("Hierarchy"))
        {
            var scene = context.ActiveScene;
            Gui.Muted(context.PlayMode.IsPlaying ? $"{scene.Name} (playing copy)" : context.Session.DisplayName);
            ImGui.Separator();

            foreach (var root in scene.RootObjects.ToArray())
            {
                DrawNode(root);
            }

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
                DrawCreateMenu(null);
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

    public void DrawCreateMenu(Transform? parent)
    {
        if (ImGui.MenuItem("Create Empty"))
        {
            context.CreateGameObject("GameObject", parent);
        }

        if (ImGui.MenuItem("Create Sprite"))
        {
            context.CreateGameObject("Sprite", parent).AddComponent<SpriteRenderer>();
        }

        if (ImGui.MenuItem("Create Camera"))
        {
            context.CreateGameObject("Camera", parent).AddComponent<Camera>();
        }
    }

    private void DrawNode(GameObject gameObject)
    {
        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen;
        if (gameObject.Transform.ChildCount == 0)
        {
            flags |= ImGuiTreeNodeFlags.Leaf;
        }

        if (context.Session.Selection.IsSelected(gameObject))
        {
            flags |= ImGuiTreeNodeFlags.Selected;
        }

        if (!gameObject.ActiveInHierarchy)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        }

        var open = ImGui.TreeNodeEx(gameObject.Id.ToString(), flags, gameObject.Name);

        if (!gameObject.ActiveInHierarchy)
        {
            ImGui.PopStyleColor();
        }

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
            DrawCreateMenu(gameObject.Transform);
            ImGui.Separator();
            if (ImGui.MenuItem("Delete", "Del"))
            {
                _pendingDelete = gameObject;
            }

            ImGui.EndPopup();
        }

        if (open)
        {
            foreach (var child in gameObject.Transform.Children.ToArray())
            {
                DrawNode(child.GameObject);
            }

            ImGui.TreePop();
        }
    }

    private void AcceptPrefabDrop(Transform? parent)
    {
        if (Gui.AcceptStringPayload(Gui.AssetPayload) is { } path && context.ResolveAsset(path) is { Type: Serialization.AssetTypes.Prefab } asset)
        {
            context.InstantiatePrefab(asset.Guid, parent);
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
