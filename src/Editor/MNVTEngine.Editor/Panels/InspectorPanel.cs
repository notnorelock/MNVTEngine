using ImGuiNET;
using Microsoft.Extensions.Logging;
using MNVTEngine.Editor.ImGuiBackend;
using MNVTEngine.Editor.Services;
using MNVTEngine.Mathematics;
using MNVTEngine.Scenes;
using SysVector2 = System.Numerics.Vector2;

namespace MNVTEngine.Editor.Panels;

/// <summary>Edits the selected GameObject: name, active state, Transform, and every component's persisted members.</summary>
internal sealed partial class InspectorPanel(EditorContext context)
{
    private readonly Dictionary<Type, IReadOnlyList<InspectableMember>> _members = [];
    private Component? _pendingRemove;

    public void Draw()
    {
        if (ImGui.Begin("Inspector") && context.Selected is { } gameObject)
        {
            DrawHeader(gameObject);
            DrawTransform(gameObject.Transform);
            foreach (var component in gameObject.Components.ToArray())
            {
                if (component is not Transform)
                {
                    DrawComponent(component);
                }
            }

            ImGui.Spacing();
            DrawAddComponent(gameObject);
        }
        else if (context.Selected is null)
        {
            Gui.Muted("Select an object in the Hierarchy or Scene view.");
        }

        ImGui.End();

        if (_pendingRemove is { } removed)
        {
            _pendingRemove = null;
            removed.Destroy();
            context.Changed();
        }
    }

    private void DrawHeader(GameObject gameObject)
    {
        var active = gameObject.ActiveSelf;
        if (ImGui.Checkbox("##active", ref active))
        {
            gameObject.SetActive(active);
            context.Changed();
        }

        ImGui.SameLine();
        var name = gameObject.Name;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##name", ref name, 128) && name.Length > 0)
        {
            gameObject.Name = name;
            context.Changed();
        }

        Gui.Muted($"id {gameObject.Id}");
    }

    private void DrawTransform(Transform transform)
    {
        if (!ImGui.CollapsingHeader("Transform", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        var position = transform.LocalPosition.ToSys();
        if (ImGui.DragFloat2("Position", ref position, 0.05f))
        {
            transform.LocalPosition = position.ToEngine();
            context.Changed();
        }

        var rotation = transform.LocalRotation;
        if (ImGui.DragFloat("Rotation", ref rotation, 0.5f))
        {
            transform.LocalRotation = rotation;
            context.Changed();
        }

        var scale = transform.LocalScale.ToSys();
        if (ImGui.DragFloat2("Scale", ref scale, 0.01f))
        {
            transform.LocalScale = scale.ToEngine();
            context.Changed();
        }
    }

    private void DrawComponent(Component component)
    {
        ImGui.PushID(component.Id.ToString());
        if (component is MissingComponent missing)
        {
            var open = ImGui.CollapsingHeader($"Missing: {missing.TypeId}", ImGuiTreeNodeFlags.DefaultOpen);
            DrawComponentContextMenu(component);
            if (open)
            {
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.7f, 0.3f, 1f), "Type is not registered. Data is kept and saved unchanged.");
                Gui.Muted(missing.Data.ToString(Newtonsoft.Json.Formatting.None));
            }

            ImGui.PopID();
            return;
        }

        var enabled = component.Enabled;
        if (ImGui.Checkbox("##enabled", ref enabled))
        {
            component.Enabled = enabled;
            context.Changed();
        }

        ImGui.SameLine();
        var expanded = ImGui.CollapsingHeader(component.GetType().Name, ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.AllowOverlap);
        DrawComponentContextMenu(component);
        if (expanded)
        {
            foreach (var member in MembersOf(component.GetType()))
            {
                DrawMember(component, member);
            }
        }

        ImGui.PopID();
    }

    private void DrawComponentContextMenu(Component component)
    {
        if (ImGui.BeginPopupContextItem())
        {
            if (ImGui.MenuItem("Remove Component"))
            {
                _pendingRemove = component;
            }

            ImGui.EndPopup();
        }
    }

    private void DrawMember(Component component, InspectableMember member)
    {
        var label = member.DisplayName;
        var value = member.GetValue(component);
        object? updated = null;
        var changed = false;

        switch (member.Kind)
        {
            case MemberKind.Float:
                var f = (float)value!;
                changed = ImGui.DragFloat(label, ref f, 0.05f);
                updated = f;
                break;
            case MemberKind.Int:
                var i = (int)value!;
                changed = ImGui.DragInt(label, ref i);
                updated = i;
                break;
            case MemberKind.Bool:
                var b = (bool)value!;
                changed = ImGui.Checkbox(label, ref b);
                updated = b;
                break;
            case MemberKind.String:
                var s = (string?)value ?? "";
                changed = ImGui.InputText(label, ref s, 512);
                updated = s;
                break;
            case MemberKind.AssetPath:
                var path = (string?)value ?? "";
                changed = ImGui.InputText(label, ref path, 512);
                updated = path.Length == 0 ? null : path;
                if (ImGui.BeginDragDropTarget())
                {
                    if (Gui.AcceptStringPayload(Gui.AssetPayload) is { } dropped && member.AcceptsAsset(dropped))
                    {
                        changed = true;
                        updated = dropped;
                    }

                    ImGui.EndDragDropTarget();
                }

                break;
            case MemberKind.Vector2:
                var v = ((Vector2)value!).ToSys();
                changed = ImGui.DragFloat2(label, ref v, 0.05f);
                updated = v.ToEngine();
                break;
            case MemberKind.Color:
                var c = ((Color)value!).ToSys();
                changed = ImGui.ColorEdit4(label, ref c, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.AlphaBar);
                updated = c.ToColor();
                break;
            case MemberKind.Enum:
                var names = Enum.GetNames(member.Type);
                var index = Array.IndexOf(names, value!.ToString());
                if (ImGui.Combo(label, ref index, names, names.Length))
                {
                    changed = true;
                    updated = Enum.Parse(member.Type, names[index]);
                }

                break;
            case MemberKind.GameObjectReference or MemberKind.ComponentReference:
                (changed, updated) = DrawReference(member, value);
                break;
            default:
                ImGui.TextDisabled($"{label}: {member.Type.Name} (not editable)");
                break;
        }

        if (changed)
        {
            if (member.TrySetValue(component, updated, out var error))
            {
                context.Changed();
            }
            else
            {
                LogRejected(context.Logger, error!);
            }
        }
    }

    private (bool Changed, object? Value) DrawReference(InspectableMember member, object? value)
    {
        var text = value switch
        {
            GameObject g => g.Name,
            Component c => $"{c.GameObject.Name} ({c.GetType().Name})",
            _ => $"None ({member.Type.Name})",
        };

        ImGui.Button($"{text}##ref", new SysVector2(ImGui.CalcItemWidth(), 0f));
        var result = (false, value);
        if (ImGui.BeginDragDropTarget())
        {
            if (Gui.AcceptGuidPayload() is { } id && context.ActiveScene.FindById(id) is { } dropped)
            {
                if (MemberInspector.ResolveReference(member, dropped) is { } resolved)
                {
                    result = (true, resolved);
                }
                else
                {
                    LogRejected(context.Logger, $"{dropped.Name} has no {member.Type.Name}.");
                }
            }

            ImGui.EndDragDropTarget();
        }

        if (ImGui.BeginPopupContextItem())
        {
            if (ImGui.MenuItem("Clear"))
            {
                result = (true, null);
            }

            ImGui.EndPopup();
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(member.DisplayName);
        return result;
    }

    private void DrawAddComponent(GameObject gameObject)
    {
        if (ImGui.Button("Add Component", new SysVector2(-1f, 0f)))
        {
            ImGui.OpenPopup("add_component");
        }

        if (ImGui.BeginPopup("add_component"))
        {
            foreach (var (id, type) in context.Session.Registry.Types.OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                if (type == typeof(Transform))
                {
                    continue;
                }

                var present = gameObject.Components.Any(c => c.GetType() == type);
                if (ImGui.MenuItem(id, null, false, !present))
                {
                    gameObject.AddComponent(type);
                    context.Changed();
                }
            }

            ImGui.EndPopup();
        }
    }

    private IReadOnlyList<InspectableMember> MembersOf(Type type)
    {
        if (!_members.TryGetValue(type, out var members))
        {
            members = MemberInspector.GetMembers(type);
            _members.Add(type, members);
        }

        return members;
    }

    [LoggerMessage(EventId = 5000, Level = LogLevel.Warning, Message = "Edit rejected: {Reason}")]
    private static partial void LogRejected(ILogger logger, string reason);
}
