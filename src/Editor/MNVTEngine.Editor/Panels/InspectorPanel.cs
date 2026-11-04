using ImGuiNET;
using Microsoft.Extensions.Logging;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Editor.Services;
using MNVTEngine.Mathematics;
using MNVTEngine.Scenes;
using SysVector2 = System.Numerics.Vector2;

namespace MNVTEngine.Editor.Panels;

/// <summary>Edits the selected GameObject: name, active state, Transform, and every component's persisted members.</summary>
internal sealed partial class InspectorPanel(EditorContext context) : IDisposable
{
    private readonly Dictionary<Type, IReadOnlyList<InspectableMember>> _members = [];
    private readonly AssetInspector _assetInspector = new(context);
    private Component? _pendingRemove;

    /// <summary>Render phase: asset previews (material spheres).</summary>
    public void Render() => _assetInspector.Render();

    public void Dispose() => _assetInspector.Dispose();

    public void Draw()
    {
        if (ImGui.Begin("Inspector") && context.Session.Selection.Asset is { } asset)
        {
            _assetInspector.Draw(asset);
        }
        else if (context.Selected is { } gameObject)
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
        else
        {
            Gui.Muted("Select an object in the Hierarchy or Scene view, or an asset in the Project panel.");
        }

        ImGui.End();

        if (_pendingRemove is { } removed)
        {
            _pendingRemove = null;
            removed.Destroy();
            context.Changed("Remove Component");
        }
    }

    /// <summary>Drops cached member lists; required after a script reload replaced component types.</summary>
    public void ClearCaches() => _members.Clear();

    private void DrawHeader(GameObject gameObject)
    {
        var active = gameObject.ActiveSelf;
        if (ImGui.Checkbox("##active", ref active))
        {
            gameObject.SetActive(active);
            context.Changed("Toggle Active");
        }

        ImGui.SameLine();
        var name = gameObject.Name;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##name", ref name, 128) && name.Length > 0)
        {
            gameObject.Name = name;
            context.Changed("Rename");
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
        if (ImGui.DragFloat3("Position", ref position, 0.05f))
        {
            transform.LocalPosition = position.ToEngine();
            context.Changed("Move");
        }

        // Euler angles in degrees; the transform remembers them, so editing one does not disturb the others.
        var rotation = transform.LocalEulerAngles.ToSys();
        if (ImGui.DragFloat3("Rotation", ref rotation, 0.5f))
        {
            transform.LocalEulerAngles = rotation.ToEngine();
            context.Changed("Rotate");
        }

        var scale = transform.LocalScale.ToSys();
        if (ImGui.DragFloat3("Scale", ref scale, 0.01f))
        {
            transform.LocalScale = scale.ToEngine();
            context.Changed("Scale");
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
                Gui.TextColored(new System.Numerics.Vector4(1f, 0.7f, 0.3f, 1f), "Type is not registered. Data is kept and saved unchanged.");
                Gui.Muted(missing.Data.ToString(Newtonsoft.Json.Formatting.None));
            }

            ImGui.PopID();
            return;
        }

        var enabled = component.Enabled;
        if (ImGui.Checkbox("##enabled", ref enabled))
        {
            component.Enabled = enabled;
            context.Changed("Toggle Component");
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

            if (component is Tilemap tilemap)
            {
                DrawTilePalette(tilemap);
            }

            if (component is AudioMixer mixer)
            {
                DrawMixer(mixer);
            }
        }

        ImGui.PopID();
    }

    /// <summary>One row per group: name, parent, volume, mute; Master's settings are the members above.</summary>
    private void DrawMixer(AudioMixer mixer)
    {
        ImGui.SeparatorText("Groups");
        AudioMixerGroup? remove = null;
        foreach (var group in mixer.Groups)
        {
            ImGui.PushID(group.GetHashCode());
            var name = group.Name;
            var width = ImGui.GetContentRegionAvail().X;
            ImGui.SetNextItemWidth((width * 0.5f) - 30f);
            if (ImGui.InputText("##name", ref name, 48, ImGuiInputTextFlags.EnterReturnsTrue) && name.Trim().Length > 0 && mixer.Find(name.Trim()) is null && name.Trim() != AudioMixer.MasterGroup)
            {
                group.Name = name.Trim();
                context.Changed("Rename Mixer Group");
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth((width * 0.5f) - 30f);
            if (ImGui.BeginCombo("##parent", group.Parent.Length == 0 ? AudioMixer.MasterGroup : group.Parent))
            {
                foreach (var parent in mixer.Groups.Where(g => g != group).Select(g => g.Name).Prepend(""))
                {
                    if (ImGui.Selectable(parent.Length == 0 ? AudioMixer.MasterGroup : parent, parent == group.Parent))
                    {
                        group.Parent = parent;
                        context.Changed("Edit Mixer");
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("X"))
            {
                remove = group;
            }

            ImGui.SetNextItemWidth(width - 80f);
            var volume = group.Volume;
            if (ImGui.SliderFloat("##volume", ref volume, 0f, 1f, "%.2f"))
            {
                group.Volume = volume;
                context.Changed("Edit Mixer");
            }

            ImGui.SameLine();
            var muted = group.Muted;
            if (ImGui.Checkbox("Mute", ref muted))
            {
                group.Muted = muted;
                context.Changed("Edit Mixer");
            }

            ImGui.Spacing();
            ImGui.PopID();
        }

        if (remove is not null)
        {
            mixer.Groups.Remove(remove);
            context.Changed("Remove Mixer Group");
        }

        if (ImGui.Button("+ Group"))
        {
            var index = 1;
            while (mixer.Find($"Group {index}") is not null)
            {
                index++;
            }

            mixer.Groups.Add(new AudioMixerGroup { Name = $"Group {index}" });
            context.Changed("Add Mixer Group");
        }

        Gui.Muted("AudioSource.Group picks the group; a group's volume multiplies its parents' up to Master.");
    }

    /// <summary>Paint toggle, eraser and the sheet's tiles as buttons; the Scene view paints with the chosen tile.</summary>
    private void DrawTilePalette(Tilemap tilemap)
    {
        var brush = context.Tiles;
        var active = brush.Active;
        if (ImGui.Checkbox("Paint in Scene view (B)", ref active))
        {
            brush.Active = active;
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("Eraser", brush.Tile < 0))
        {
            brush.Tile = -1;
        }

        Gui.Muted($"{tilemap.Count} tiles. Left drag paints, Ctrl + left drag erases.");
        if (tilemap.Texture.IsNone)
        {
            Gui.Muted("Assign a sheet texture to show the palette.");
            return;
        }

        var texture = context.Resources.GetTexture(tilemap.Texture);
        var id = context.ImGui.Register(texture);
        var rows = Math.Max(1, texture.Height / tilemap.TileHeight);
        var count = Math.Min(rows * tilemap.Columns, 1024);
        var perLine = Math.Max(1, (int)(ImGui.GetContentRegionAvail().X / 40f));
        for (var tile = 0; tile < count; tile++)
        {
            var rect = tilemap.SourceRect(tile);
            if (rect.X + rect.Width > texture.Width || rect.Y + rect.Height > texture.Height)
            {
                break;
            }

            if (tile % perLine != 0)
            {
                ImGui.SameLine();
            }

            var uv0 = new SysVector2(rect.X / texture.Width, rect.Y / texture.Height);
            var uv1 = new SysVector2((rect.X + rect.Width) / texture.Width, (rect.Y + rect.Height) / texture.Height);
            using (Gui.StyleColor(ImGuiCol.Button, new System.Numerics.Vector4(1f, 0.6f, 0.1f, 1f), brush.Tile == tile))
            {
                if (ImGui.ImageButton($"tile{tile}", id, new SysVector2(32f, 32f), uv0, uv1))
                {
                    brush.Tile = tile; // changes the push condition: the scope still pops what it pushed
                    brush.Active = true;
                }
            }

            if (ImGui.IsItemHovered())
            {
                Gui.Tooltip($"Tile {tile}");
            }
        }
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
            case MemberKind.AssetReference:
                (changed, updated) = DrawAssetReference(member, (AssetRef)value!);
                break;
            case MemberKind.GameObjectReference or MemberKind.ComponentReference:
                (changed, updated) = DrawReference(member, value);
                break;
            default:
                Gui.TextDisabled(value is System.Collections.ICollection items
                    ? $"{label}: {items.Count} items"
                    : $"{label}: {value?.ToString() ?? "none"} (not editable)");
                break;
        }

        if (changed)
        {
            if (member.TrySetValue(component, updated, out var error))
            {
                context.Changed("Edit Property");
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

    private (bool Changed, object? Value) DrawAssetReference(InspectableMember member, AssetRef value)
    {
        var assets = context.Session.Assets;
        var record = value.IsNone ? null : assets.Get(value.Guid);
        var text = value.IsNone ? $"None ({member.AssetType ?? "Asset"})" : record?.Name ?? $"Missing ({value.Guid})";
        using (Gui.StyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.45f, 0.45f, 1f), record is null && !value.IsNone))
        {
            ImGui.Button($"{text}##asset", new SysVector2(ImGui.CalcItemWidth(), 0f));
        }

        if (record is not null && ImGui.IsItemHovered())
        {
            Gui.Tooltip(record.Path);
        }

        var result = (false, (object?)value);
        if (ImGui.BeginDragDropTarget())
        {
            if (Gui.AcceptStringPayload(Gui.AssetPayload) is { } path && context.ResolveAsset(path) is { } dropped)
            {
                if (member.AcceptsAssetType(dropped.Type))
                {
                    result = (true, new AssetRef(dropped.Guid));
                }
                else
                {
                    LogRejected(context.Logger, $"{member.DisplayName} needs a {member.AssetType}, not a {dropped.Type}.");
                }
            }

            ImGui.EndDragDropTarget();
        }

        if (ImGui.BeginPopupContextItem())
        {
            if (ImGui.MenuItem("Clear"))
            {
                result = (true, AssetRef.None);
            }

            ImGui.Separator();
            foreach (var candidate in assets.Assets.Where(a => member.AcceptsAssetType(a.Type)).OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (ImGui.MenuItem(candidate.Path, null, candidate.Guid == value.Guid))
                {
                    result = (true, new AssetRef(candidate.Guid));
                }
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
                    context.Changed("Add Component");
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
