using ImGuiNET;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Mathematics;

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

        if (ImGui.BeginMenu("3D Object"))
        {
            foreach (var primitive in Enum.GetValues<PrimitiveMesh>().Where(p => p != PrimitiveMesh.None))
            {
                if (ImGui.MenuItem(primitive.ToString()))
                {
                    var gameObject = context.CreateGameObject(primitive.ToString(), parent);
                    gameObject.AddComponent<MeshRenderer>().Primitive = primitive;
                    AddCollider(gameObject, primitive);
                }
            }

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Light"))
        {
            foreach (var type in Enum.GetValues<LightType>())
            {
                if (ImGui.MenuItem($"{type} Light"))
                {
                    var gameObject = context.CreateGameObject($"{type} Light", parent);
                    gameObject.AddComponent<Light>().Type = type;
                    // Directional and spot lights shine along -Z: point them down at the scene.
                    gameObject.Transform.LocalRotation = type == LightType.Point ? Quaternion.Identity : Quaternion.Euler(type == LightType.Directional ? -50f : -90f, type == LightType.Directional ? 30f : 0f, 0f);
                    gameObject.Transform.LocalPosition = type == LightType.Point ? new Vector3(0f, 2f, 0f) : new Vector3(0f, 4f, 0f);
                }
            }

            if (ImGui.MenuItem("Ambient Light"))
            {
                context.CreateGameObject("Ambient Light", parent).AddComponent<AmbientLight>();
            }

            ImGui.EndMenu();
        }
    }

    /// <summary>A collider matching a primitive's shape, as Unity adds one (add a Rigidbody to make it fall).</summary>
    private static void AddCollider(GameObject gameObject, PrimitiveMesh primitive)
    {
        switch (primitive)
        {
            case PrimitiveMesh.Sphere:
                gameObject.AddComponent<SphereCollider>();
                break;
            case PrimitiveMesh.Capsule:
            case PrimitiveMesh.Cylinder:
                gameObject.AddComponent<CapsuleCollider>();
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

        bool open;
        using (Gui.StyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled], !gameObject.ActiveInHierarchy))
        {
            open = ImGui.TreeNodeEx(gameObject.Id.ToString(), flags, gameObject.Name);
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
